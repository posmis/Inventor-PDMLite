using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using PdmLite.Data.Library;
using PdmLite.Library.Backup;
using PdmLite.Library.Storage;

namespace PdmLite.Library.Tests;

public sealed class BackupAuditTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("backup-audit-").FullName;
    private static LibraryCategory Category(int id, int? parent = null) => new() { Id = id, ParentId = parent, Name = id.ToString() };
    private LibraryBackupService Service => new(null!, () => new LibraryStorage(Path.Combine(_root, "library")),
        () => new BackupOptions(_root, 3, 7), NullLogger<LibraryBackupService>.Instance);

    [Fact]
    public void Categories_DeepHierarchyIsIterativeAndParentsPrecedeChildren()
    {
        var categories = Enumerable.Range(1, 20000).Select(i => Category(i, i == 1 ? null : i - 1)).Reverse().ToList();
        Assert.Equal(Enumerable.Range(1, 20000), BackupValidation.OrderCategories(categories).Select(c => c.Id));
    }

    [Theory]
    [InlineData("self")][InlineData("cycle")][InlineData("orphan")][InlineData("duplicate")][InlineData("null")]
    public void Categories_InvalidHierarchyRejected(string kind)
    {
        LibraryCategory[] categories = kind switch {
            "self" => [Category(1, 1)], "cycle" => [Category(1, 2), Category(2, 1)],
            "orphan" => [Category(1, 99)], "duplicate" => [Category(1), Category(1)], _ => [null!] };
        Assert.Throws<InvalidDataException>(() => BackupValidation.OrderCategories(categories));
    }

    [Theory]
    [InlineData("Categories")][InlineData("Items")][InlineData("Versions")][InlineData("CategoryRules")]
    [InlineData("Properties")][InlineData("References")][InlineData("DesignationRules")][InlineData("Log")]
    public async Task Archive_NullTablesRejectedBeforeDatabaseOrFilesystemChanges(string table)
    {
        var snapshot = JsonSerializer.SerializeToNode(new LibrarySnapshot())!; snapshot[table] = null;
        var path = Archive(snapshot.ToJsonString());
        await Assert.ThrowsAsync<InvalidDataException>(() => Service.PlanRestoreAsync(path));
        Assert.False(Directory.Exists(Path.Combine(_root, "library")));
    }

    [Theory]
    [InlineData("files/../escape.ipt")][InlineData(@"files/..\escape.ipt")]
    [InlineData("files//tmp/escape.ipt")][InlineData("files/C:/escape.ipt")]
    [InlineData("files/NUL.ipt")][InlineData("files/a.ipt:stream")]
    public async Task Archive_InvalidPathsRejectedDuringPreview(string entry)
    {
        var path = Archive(JsonSerializer.Serialize(new LibrarySnapshot()), entry);
        await Assert.ThrowsAsync<InvalidDataException>(() => Service.PlanRestoreAsync(path));
        Assert.False(Directory.Exists(Path.Combine(_root, "library")));
    }

    [Fact]
    public async Task Archive_WindowsCaseAliasesRejectedDuringPreview()
    {
        var path = Archive(JsonSerializer.Serialize(new LibrarySnapshot()), "files/Bolt.ipt", "files/bolt.ipt");
        await Assert.ThrowsAsync<InvalidDataException>(() => Service.PlanRestoreAsync(path));
    }

    [Fact]
    public async Task Archive_CyclicCategoriesRejectedBeforeDatabaseAccess()
    {
        var snapshot = new LibrarySnapshot { Categories = [Category(1, 2), Category(2, 1)] };
        await Assert.ThrowsAsync<InvalidDataException>(() => Service.PlanRestoreAsync(Archive(JsonSerializer.Serialize(snapshot))));
    }

    [Theory]
    [InlineData(0)][InlineData(-1)][InlineData(2)]
    public async Task Archive_UnsupportedVersionRejected(int format)
    {
        var path = Archive(JsonSerializer.Serialize(new LibrarySnapshot()), format: format);
        await Assert.ThrowsAsync<InvalidDataException>(() => Service.PlanRestoreAsync(path));
    }


    [PostgresFact]
    public async Task Archive_UnlistedFileCannotBypassChecksumVerification()
    {
        await using var db = await TestDatabase.CreateAsync();
        var service = new LibraryBackupService(db.Database, () => new LibraryStorage(Path.Combine(_root, "library")),
            () => new BackupOptions(_root, 3, 7), NullLogger<LibraryBackupService>.Instance);
        var path = Archive(JsonSerializer.Serialize(new LibrarySnapshot()), "files/unverified.ipt");
        var plan = await service.PlanRestoreAsync(path);
        Assert.True(plan.IsDamaged);
        Assert.Contains(plan.MissingFiles, p => p.Contains("контрольной суммы"));
        await Assert.ThrowsAsync<InvalidDataException>(() => service.RestoreAsync(path));
        Assert.False(Directory.Exists(Path.Combine(_root, "library")));
    }

    [PostgresFact]
    public async Task ConcurrentBackups_KeepDistinctCompleteArchives()
    {
        await using var db = await TestDatabase.CreateAsync();
        var service = new LibraryBackupService(db.Database, () => new LibraryStorage(Path.Combine(_root, "library")),
            () => new BackupOptions(_root, 3, 7), NullLogger<LibraryBackupService>.Instance);
        var results = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => service.CreateAsync(BackupReason.Manual)));
        Assert.Equal(3, results.Select(b => b.Path).Distinct().Count());
        Assert.Equal(3, service.List().Count);
        Assert.Empty(Directory.GetFiles(_root, "*.part"));
        foreach (var backup in results) Assert.False((await service.PlanRestoreAsync(backup.Path)).IsDamaged);
    }

    private string Archive(string snapshot, string? entry = null, string? second = null, int format = 1)
    {
        var path = Path.Combine(_root, Guid.NewGuid() + ".zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using (var writer = new StreamWriter(zip.CreateEntry("manifest.json").Open()))
            writer.Write(JsonSerializer.Serialize(new BackupManifest { Format = format }));
        using (var writer = new StreamWriter(zip.CreateEntry("data/library.json").Open())) writer.Write(snapshot);
        if (entry is not null) zip.CreateEntry(entry);
        if (second is not null) zip.CreateEntry(second);
        return path;
    }
    public void Dispose() => Directory.Delete(_root, true);
}
