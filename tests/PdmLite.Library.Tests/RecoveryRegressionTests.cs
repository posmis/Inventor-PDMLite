using System.IO.Compression;
using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PdmLite.Data;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PdmLite.Data.Library;
using PdmLite.Library.Backup;
using PdmLite.Library.Items;
using PdmLite.Library.Storage;

namespace PdmLite.Library.Tests;

public sealed partial class ReviewRegressionTests
{
    [PostgresFact]
    public async Task Edit_DestinationAppearsAfterValidation_FailedMovePreservesBothFiles()
    {
        await using var db = await TestDatabase.CreateAsync(); var model = await SeedAsync(db.Database);
        var items = Items(db.Database); var catalog = await items.LoadCatalogAsync();
        var edit = new LibraryItemEdit("Renamed", LibraryItemKind.Purchased, "Test", model.Category);
        var check = await items.CheckEditAsync(catalog, model.Remove, edit);
        Assert.False(check.HasErrors);
        var destination = _folders.InLibrary(check.NewRelativePath!);
        var original = File.ReadAllText(model.RemovePath);
        var collision = new DestinationCollision(() =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.WriteAllText(destination, "created by another operation");
        });
        var racing = new LibraryItemService(new CollisionDatabase(db.Database, collision), _store,
            () => new LibraryStorage(_folders.Library), NullLogger<LibraryItemService>.Instance);

        await Assert.ThrowsAsync<IOException>(() => racing.ApplyEditAsync(catalog, model.Remove, edit));

        Assert.True(collision.Fired);
        Assert.Equal(original, File.ReadAllText(model.RemovePath));
        Assert.Equal("created by another operation", File.ReadAllText(destination));
        Assert.Equal([model.RemovePath], FileBackedStore.References(model.AssemblyPath));
        await using var after = db.Database.CreateContext();
        Assert.Equal("Remove", (await after.LibraryItems.FindAsync(model.Remove))!.Designation);
        Assert.False(await after.LibraryLog.AnyAsync(l => l.Action == "edit"));
    }

    [PostgresFact]
    public async Task Edit_FailedRollbackKeepsCopiesAndReportsIncompleteRecovery()
    {
        await using var db = await TestDatabase.CreateAsync(); var model = await SeedAsync(db.Database);
        var original = File.ReadAllText(model.RemovePath);
        _store.BeforeWrite = _ => { Directory.CreateDirectory(model.RemovePath); throw new IOException("Original write failure"); };
        var items = Items(db.Database); var catalog = await items.LoadCatalogAsync(); LibraryRollbackException? failure = null;
        try
        {
            failure = await Assert.ThrowsAsync<LibraryRollbackException>(() => items.ApplyEditAsync(
                catalog, model.Remove,
                new LibraryItemEdit("Renamed", LibraryItemKind.Purchased, "Test", model.Category)));
            Assert.Contains("восстановление неполное", failure.Message);
            var copy = Assert.Single(failure.Copies, c => c.Original == model.RemovePath);
            Assert.True(File.Exists(copy.Backup)); Assert.Equal(original, File.ReadAllText(copy.Backup));
            Assert.Contains("Original write failure", failure.InnerException!.ToString());
            Assert.Contains(copy.Backup, failure.Message);
            await using var after = db.Database.CreateContext();
            Assert.Equal("Remove", (await after.LibraryItems.FindAsync(model.Remove))!.Designation);
        }
        finally { if (failure is not null) foreach (var copy in failure.Copies) LibraryStorage.Delete(copy.Backup); }
    }

    [PostgresFact]
    public async Task Merge_FailedAssemblyRollbackKeepsCopiesAndDoesNotHideOriginalFailure()
    {
        await using var db = await TestDatabase.CreateAsync(); var model = await SeedAsync(db.Database);
        var original = File.ReadAllText(model.AssemblyPath);
        _store.BeforeReplace = path =>
        { File.Delete(path); Directory.CreateDirectory(path); throw new IOException("Original relink failure"); };
        LibraryRollbackException? failure = null;
        try
        {
            failure = await Assert.ThrowsAsync<LibraryRollbackException>(() => Items(db.Database).MergeDuplicatesAsync(model.Keep, [model.Remove]));
            var copy = Assert.Single(failure.Copies, c => c.Original == model.AssemblyPath);
            Assert.Equal(original, File.ReadAllText(copy.Backup)); Assert.Contains(copy.Backup, failure.Message);
            Assert.Contains("Original relink failure", failure.InnerException!.ToString());
            await using var after = db.Database.CreateContext(); Assert.NotNull(await after.LibraryItems.FindAsync(model.Remove));
        }
        finally { if (failure is not null) foreach (var copy in failure.Copies) LibraryStorage.Delete(copy.Backup); }
    }

    [PostgresFact]
    public async Task Edit_CleanupFailureAfterCommitIsNotReportedAsFailedSave()
    {
        await using var db = await TestDatabase.CreateAsync(); var model = await SeedAsync(db.Database);
        DirectoryBlock? blocked = null; var oldFolder = Path.GetDirectoryName(model.RemovePath)!;
        _store.BeforeWrite = _ => blocked = new DirectoryBlock(oldFolder);
        try
        {
            var items = Items(db.Database); var catalog = await items.LoadCatalogAsync();
            await items.ApplyEditAsync(catalog, model.Remove, new LibraryItemEdit("Renamed", LibraryItemKind.Purchased, "Test", model.Category));
            Assert.True(Directory.Exists(oldFolder));
            await using var after = db.Database.CreateContext(); var item = (await after.LibraryItems.FindAsync(model.Remove))!;
            Assert.Equal("Renamed", item.Designation); Assert.True(File.Exists(_folders.InLibrary(item.RelativePath)));
            Assert.True(await after.LibraryLog.AnyAsync(l => l.Action == "edit"));
        }
        finally { blocked?.Dispose(); }
    }

    [PostgresFact]
    public async Task Restore_SourceArchiveChangedDuringSafetyBackupUsesValidatedSnapshot() => await RestoreWithSourceChange(false);

    [PostgresFact]
    public async Task Restore_SourceArchiveDeletedDuringSafetyBackupUsesValidatedSnapshot() => await RestoreWithSourceChange(true);

    private async Task RestoreWithSourceChange(bool delete)
    {
        await using var db = await TestDatabase.CreateAsync(); var model = await SeedAsync(db.Database);
        var service = Backups(db.Database); var expected = File.ReadAllText(model.KeepPath);
        var source = await service.CreateAsync(BackupReason.Manual);
        LibraryStorage.ClearReadOnly(model.KeepPath); File.AppendAllText(model.KeepPath, "current changes");
        var changed = false;
        var progress = new CallbackProgress(p =>
        {
            if (changed || p.Done != 0 || p.Current is null) return; changed = true;
            if (delete) File.Delete(source.Path);
            else
            {
                using var zip = ZipFile.Open(source.Path, ZipArchiveMode.Update);
                var name = "files/" + Path.GetRelativePath(_folders.Library, model.KeepPath).Replace('\\', '/');
                zip.GetEntry(name)!.Delete();
                using var writer = new StreamWriter(zip.CreateEntry(name).Open()); writer.Write("tampered after validation");
            }
        });
        await service.RestoreAsync(source.Path, progress);
        Assert.True(changed); Assert.Equal(expected, File.ReadAllText(model.KeepPath));
        if (delete) Assert.False(File.Exists(source.Path));
        else Assert.True((await service.PlanRestoreAsync(source.Path)).IsDamaged);
    }

    [PostgresFact]
    public async Task Restore_PruneMayRemoveOriginalAutomaticArchiveButNotRestoreSnapshot()
    {
        await using var db = await TestDatabase.CreateAsync(); var model = await SeedAsync(db.Database);
        var service = new LibraryBackupService(db.Database, () => new LibraryStorage(_folders.Library),
            () => new BackupOptions(Path.Combine(_folders.Root, "Backups"), 1, 7), NullLogger<LibraryBackupService>.Instance);
        var expected = File.ReadAllText(model.KeepPath); var source = await service.CreateAsync(BackupReason.Scheduled);
        LibraryStorage.ClearReadOnly(model.KeepPath); File.AppendAllText(model.KeepPath, "current changes");
        var safety = await service.RestoreAsync(source.Path);
        Assert.False(File.Exists(source.Path)); Assert.True(File.Exists(safety.Path));
        Assert.Equal(expected, File.ReadAllText(model.KeepPath));
    }

    // Инъекция на фактическом чтении редактируемой карточки после CheckEdit, а не по счётчику factory-вызовов.
    private sealed class DestinationCollision(Action create) : DbCommandInterceptor
    {
        public bool Fired;
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData data,
            InterceptionResult<DbDataReader> result, CancellationToken ct = default)
        {
            if (!Fired && command.CommandText.Contains("item_versions")) { Fired = true; create(); }
            return ValueTask.FromResult(result);
        }
    }
    private sealed class CollisionDatabase(IPdmDatabase inner, DestinationCollision collision) : IPdmDatabase
    {
        public bool IsConfigured => inner.IsConfigured;
        public DatabaseSettings Settings => inner.Settings;
        public void Configure(DatabaseSettings value) => inner.Configure(value);
        public PdmDbContext CreateContext() => new(new DbContextOptionsBuilder<PdmDbContext>().UseNpgsql(Settings.BuildConnectionString())
            .UseSnakeCaseNamingConvention().AddInterceptors(collision).Options);
        public Task MigrateAsync(CancellationToken ct = default) => inner.MigrateAsync(ct);
        public Task<(bool Ok, string Message)> TestAsync(DatabaseSettings value, CancellationToken ct = default) => inner.TestAsync(value, ct);
    }

    private sealed class DirectoryBlock : IDisposable
    {
        private readonly string _path;
        private readonly UnixFileMode _mode;
        private readonly SafeFileHandle? _handle;
        public DirectoryBlock(string path)
        {
            _path = path;
            if (OperatingSystem.IsWindows())
            {
                _handle = CreateFile(path, 1, 3, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
                if (_handle.IsInvalid) throw new IOException("Cannot lock test directory", new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
            }
            else { _mode = File.GetUnixFileMode(path); File.SetUnixFileMode(path, 0); }
        }
        public void Dispose()
        {
            _handle?.Dispose();
            if (!OperatingSystem.IsWindows() && Directory.Exists(_path)) File.SetUnixFileMode(_path, _mode);
        }
        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    }
}
