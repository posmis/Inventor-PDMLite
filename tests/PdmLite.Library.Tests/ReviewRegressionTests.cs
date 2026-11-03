using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PdmLite.Core.Documents;
using PdmLite.Data;
using PdmLite.Data.Library;
using PdmLite.Index;
using PdmLite.Index.Operations;
using PdmLite.Library.Backup;
using PdmLite.Library.Items;
using PdmLite.Library.Storage;

namespace PdmLite.Library.Tests;

public sealed partial class ReviewRegressionTests : IDisposable
{
    private readonly TempFolders _folders = new();
    private readonly SwitchableStore _store = new();
    private string Project => Path.Combine(_folders.Root, "Project");
    private string AddProject(string name, params string[] refs)
    {
        var path = Path.Combine(Project, name); FileBackedStore.Create(path, name, "Test", refs); return path;
    }
    private ReferenceIndexService Index(IPdmDatabase db) => new(db, _store, NullLogger<ReferenceIndexService>.Instance);
    private ProjectFileOperations Operations(IPdmDatabase db) => new(db, _store, Index(db), new NoOpenDocuments(), NullLogger<ProjectFileOperations>.Instance);

    [Fact]
    public async Task RenamePlan_WithoutDatabase_UnreadableParentBlocksOperation()
    {
        var part = AddProject("part.ipt"); var parent = AddProject("assembly.iam", part); _store.Failed.Add(parent);
        var db = new PdmDatabase(new DatabaseSettings(), NullLoggerFactory.Instance);
        var operations = Operations(db);
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => operations.PlanAsync(Project, [new(part, Path.Combine(Project, "new.ipt"))], []));
        Assert.True(File.Exists(part)); Assert.Equal([part], FileBackedStore.References(parent));
        _store.Failed.Clear();
        var plan = await operations.PlanAsync(Project, [new(part, Path.Combine(Project, "new.ipt"))], []);
        Assert.True(plan.CanExecute); Assert.Contains(plan.References, r => r.Document == parent);
    }

    [PostgresFact]
    public async Task RenamePlan_WithDatabase_UnreadableParentBlocksOperation()
    {
        await using var db = await TestDatabase.CreateAsync();
        var part = AddProject("part.ipt"); var parent = AddProject("assembly.iam", part); _store.Failed.Add(parent);
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => Operations(db.Database).PlanAsync(Project, [new(part, Path.Combine(Project, "new.ipt"))], []));
        Assert.True(File.Exists(part)); Assert.Equal([part], FileBackedStore.References(parent));
    }

    [PostgresFact]
    public async Task FailedRescan_KeepsLastKnownReferences_AndDoesNotClaimVerifiedTimestamp()
    {
        await using var db = await TestDatabase.CreateAsync(); var part = AddProject("part.ipt"); var parent = AddProject("assembly.iam", part);
        var index = Index(db.Database); await index.ScanAsync(Project);
        File.AppendAllText(parent, "changed"); _store.Failed.Add(parent);
        Assert.Single((await index.ScanAsync(Project)).Errors);
        Assert.Contains(await index.ComponentsAsync(parent), d => d.Path == part);
        Assert.Null(await index.ScannedAtAsync(parent));
    }

    [PostgresFact]
    public async Task FailedRescan_IsRetriedWithoutAnotherFileChange()
    {
        await using var db = await TestDatabase.CreateAsync(); var part = AddProject("part.ipt"); var parent = AddProject("assembly.iam", part);
        _store.Failed.Add(parent); var index = Index(db.Database); await index.ScanAsync(Project);
        _store.Failed.Clear();
        var retry = await index.ScanAsync(Project);
        Assert.Equal(1, retry.Read); Assert.Empty(retry.Errors);
        Assert.Contains(await index.ComponentsAsync(parent), d => d.Path == part);
    }

    private sealed record Models(Guid Keep, Guid Remove, Guid Assembly, int Category, string KeepPath, string RemovePath, string AssemblyPath);
    private LibraryItemService Items(IPdmDatabase db) => new(db, _store, () => new LibraryStorage(_folders.Library), NullLogger<LibraryItemService>.Instance);
    private LibraryBackupService Backups(IPdmDatabase db) => new(db, () => new LibraryStorage(_folders.Library),
        () => new BackupOptions(Path.Combine(_folders.Root, "Backups"), 10, 7), NullLogger<LibraryBackupService>.Instance);

    private async Task<Models> SeedAsync(IPdmDatabase database)
    {
        await database.MigrateAsync();
        var keepPath = _folders.InLibrary("keep/keep.ipt"); var removePath = _folders.InLibrary("remove/remove.ipt"); var assemblyPath = _folders.InLibrary("assemblies/assembly.iam");
        FileBackedStore.Create(keepPath, "Keep", "Keep"); FileBackedStore.Create(removePath, "Remove", "Remove");
        FileBackedStore.Create(assemblyPath, "Assembly", "Assembly", removePath);
        await using var db = database.CreateContext();
        var category = new LibraryCategory { Name = "Review" }; db.LibraryCategories.Add(category); await db.SaveChangesAsync();
        async Task<LibraryItem> Model(string path, string name, LibraryDocumentType type)
        {
            var (hash, size) = await LibraryStorage.HashAsync(path);
            var relative = Path.GetRelativePath(_folders.Library, path).Replace('\\', '/');
            return new() { Id = Guid.NewGuid(), CategoryId = category.Id, Kind = LibraryItemKind.Purchased, DocumentType = type,
                Designation = name, DesignationKey = name.ToUpperInvariant(), Manufacturer = "Test", RelativePath = relative,
                FileHash = hash, FileSize = size, CreatedAt = DateTime.UtcNow, ModifiedAt = DateTime.UtcNow, CreatedBy = "test", ModifiedBy = "test",
                Versions = [new() { Version = 1, RelativePath = relative, FileHash = hash, FileSize = size, CreatedAt = DateTime.UtcNow, CreatedBy = "test" }] };
        }
        var keep = await Model(keepPath, "Keep", LibraryDocumentType.Part); var remove = await Model(removePath, "Remove", LibraryDocumentType.Part);
        var assembly = await Model(assemblyPath, "Assembly", LibraryDocumentType.Assembly);
        assembly.References.Add(new() { AssemblyId = assembly.Id, ComponentId = remove.Id });
        db.LibraryItems.AddRange(keep, remove, assembly); await db.SaveChangesAsync();
        foreach (var path in new[] { keepPath, removePath, assemblyPath }) LibraryStorage.MakeReadOnly(path);
        return new(keep.Id, remove.Id, assembly.Id, category.Id, keepPath, removePath, assemblyPath);
    }

    [PostgresFact]
    public async Task Edit_SilentReferenceReplacementFailureRollsBackFilesAndDatabase()
    {
        await using var db = await TestDatabase.CreateAsync(); var model = await SeedAsync(db.Database);
        var before = File.ReadAllText(model.AssemblyPath); _store.Inner.IgnoreReplaceFor = "assembly.iam";
        var service = Items(db.Database); var catalog = await service.LoadCatalogAsync();
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => service.ApplyEditAsync(catalog, model.Remove,
            new LibraryItemEdit("Renamed", LibraryItemKind.Purchased, "Test", model.Category)));
        Assert.True(File.Exists(model.RemovePath)); Assert.Equal(before, File.ReadAllText(model.AssemblyPath));
        await using var after = db.Database.CreateContext(); Assert.Equal("Remove", (await after.LibraryItems.FindAsync(model.Remove))!.Designation);
        Assert.False(await after.LibraryLog.AnyAsync(l => l.Action == "edit"));
    }

    [PostgresFact]
    public async Task Merge_SilentReferenceReplacementFailureDoesNotDeleteComponent()
    {
        await using var db = await TestDatabase.CreateAsync(); var model = await SeedAsync(db.Database);
        _store.Inner.IgnoreReplaceFor = "assembly.iam";
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => Items(db.Database).MergeDuplicatesAsync(model.Keep, [model.Remove]));
        Assert.True(File.Exists(model.RemovePath)); Assert.Equal([model.RemovePath], FileBackedStore.References(model.AssemblyPath));
        await using var after = db.Database.CreateContext(); Assert.NotNull(await after.LibraryItems.FindAsync(model.Remove));
    }

    [PostgresFact]
    public async Task Relink_UpdatesAssemblyCurrentVersionHashAndSize()
    {
        await using var db = await TestDatabase.CreateAsync(); var model = await SeedAsync(db.Database); var service = Items(db.Database);
        await service.ApplyEditAsync(await service.LoadCatalogAsync(), model.Remove,
            new LibraryItemEdit("Renamed", LibraryItemKind.Purchased, "Test", model.Category));
        await using var after = db.Database.CreateContext(); var assembly = await after.LibraryItems.Include(i => i.Versions).SingleAsync(i => i.Id == model.Assembly);
        var current = Assert.Single(assembly.Versions, v => v.Version == assembly.Version);
        var (hash, size) = await LibraryStorage.HashAsync(model.AssemblyPath);
        Assert.Equal(hash, assembly.FileHash); Assert.Equal(hash, current.FileHash); Assert.Equal(size, current.FileSize);
    }

    [PostgresFact]
    public async Task Merge_FileCannotBeRemoved_DoesNotCommitPartialSuccess()
    {
        await using var db = await TestDatabase.CreateAsync(); var model = await SeedAsync(db.Database);
        var folder = Path.GetDirectoryName(model.RemovePath)!; FileStream? locked = null; UnixFileMode mode = default;
        try
        {
            if (OperatingSystem.IsWindows()) locked = File.Open(model.RemovePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            else { mode = File.GetUnixFileMode(folder); File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserExecute); }
            await Assert.ThrowsAnyAsync<Exception>(() => Items(db.Database).MergeDuplicatesAsync(model.Keep, [model.Remove]));
        }
        finally { locked?.Dispose(); if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(folder, mode); }
        await using var after = db.Database.CreateContext(); Assert.NotNull(await after.LibraryItems.FindAsync(model.Remove));
        Assert.Equal([model.RemovePath], FileBackedStore.References(model.AssemblyPath));
        Assert.False(await after.LibraryLog.AnyAsync(l => l.Action == "merge"));
    }

    [PostgresFact]
    public async Task Backup_ConcurrentDatabaseAndFileChangeIsRejectedBeforeArchivePublication()
    {
        await using var db = await TestDatabase.CreateAsync(); var model = await SeedAsync(db.Database); var changed = false;
        var progress = new CallbackProgress(p =>
        {
            if (changed || p.Done != 0 || p.Current is null) return; changed = true;
            LibraryStorage.ClearReadOnly(model.KeepPath); File.AppendAllText(model.KeepPath, "new revision");
            using var context = db.Database.CreateContext(); var item = context.LibraryItems.Single(i => i.Id == model.Keep);
            item.Designation = "Changed during backup"; context.SaveChanges();
        });
        await Assert.ThrowsAnyAsync<IOException>(() => Backups(db.Database).CreateAsync(BackupReason.Manual, progress));
        Assert.True(changed); Assert.Empty(Backups(db.Database).List());
        Assert.Empty(Directory.GetFiles(Path.Combine(_folders.Root, "Backups"), "*.part"));
    }

    [PostgresFact]
    public async Task Backup_ChangeToAlreadyCopiedFileIsRejected()
    {
        await using var db = await TestDatabase.CreateAsync(); var model = await SeedAsync(db.Database); var changed = false;
        var progress = new CallbackProgress(p =>
        {
            if (changed || p.Done != 1 || p.Current is null) return; changed = true;
            LibraryStorage.ClearReadOnly(model.AssemblyPath); File.AppendAllText(model.AssemblyPath, "changed after copy");
        });
        await Assert.ThrowsAnyAsync<IOException>(() => Backups(db.Database).CreateAsync(BackupReason.Manual, progress));
        Assert.True(changed); Assert.Empty(Backups(db.Database).List());
    }

    [PostgresFact]
    public async Task Backup_DatabaseOnlyChangeIsDetectedAndPreviousBackupIsKept()
    {
        await using var db = await TestDatabase.CreateAsync(); var model = await SeedAsync(db.Database);
        var service = Backups(db.Database); var previous = await service.CreateAsync(BackupReason.Manual); var changed = false;
        var progress = new CallbackProgress(p =>
        {
            if (changed || p.Done != 0 || p.Current is null) return; changed = true;
            using var context = db.Database.CreateContext(); var item = context.LibraryItems.Single(i => i.Id == model.Keep);
            item.Designation = "Changed metadata during backup"; context.SaveChanges();
        });
        await Assert.ThrowsAnyAsync<IOException>(() => service.CreateAsync(BackupReason.Manual, progress));
        Assert.True(changed); Assert.Equal(previous.Path, Assert.Single(service.List()).Path);
        Assert.False((await service.PlanRestoreAsync(previous.Path)).IsDamaged);
    }

    private sealed class CallbackProgress(Action<(int Done, int Total, string? Current)> report) : IProgress<(int Done, int Total, string? Current)>
    { public void Report((int Done, int Total, string? Current) value) => report(value); }
    private sealed class SwitchableStore : IDocumentPropertyStore
    {
        public FileBackedStore Inner { get; } = new();
        public Action<string>? BeforeWrite { get; set; }
        public Action<string>? BeforeReplace { get; set; }
        public HashSet<string> Failed { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool IsAvailable => true;
        public string? UnavailableReason => null;
        public Task<DocumentProperties> ReadAsync(string path, CancellationToken cancellationToken = default) => ReadWithoutGeometryAsync(path, cancellationToken);
        public Task<DocumentProperties> ReadWithoutGeometryAsync(string path, CancellationToken cancellationToken = default) =>
            Failed.Contains(path) ? throw new IOException("Unreadable parent") : Inner.ReadWithoutGeometryAsync(path, cancellationToken);
        public Task WriteAsync(string path, DocumentPropertyChanges changes, CancellationToken cancellationToken = default)
        { BeforeWrite?.Invoke(path); return Inner.WriteAsync(path, changes, cancellationToken); }
        public Task ReplaceReferencesAsync(string path, IReadOnlyDictionary<string, string> map, CancellationToken cancellationToken = default)
        { BeforeReplace?.Invoke(path); return Inner.ReplaceReferencesAsync(path, map, cancellationToken); }
    }
    public void Dispose() => _folders.Dispose();
}
