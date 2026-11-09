using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PdmLite.Core.Documents;
using PdmLite.Library.Import;
using PdmLite.Library.Storage;

namespace PdmLite.Library.Tests;

public sealed class ImportIntegrityTests
{
    private sealed class DiskStore : IDocumentPropertyStore
    {
        public bool IsAvailable => true;
        public string? UnavailableReason => null;
        public string Mode = "complete";
        public List<string> Saved = [];
        public Task<DocumentProperties> ReadAsync(string path, CancellationToken cancellationToken = default)
        {
            if (Mode == "readback-failure" && path.EndsWith(".iam") && Saved.Contains(Path.GetFileName(path)) && path.Contains(Path.DirectorySeparatorChar + "Library" + Path.DirectorySeparatorChar))
                throw new IOException("Не подтверждена запись: ошибка повторного чтения");
            return Task.FromResult(JsonSerializer.Deserialize<DocumentProperties>(File.ReadAllText(path))!);
        }
        public Task WriteAsync(string path, DocumentPropertyChanges changes, CancellationToken cancellationToken = default)
        {
            var props = JsonSerializer.Deserialize<DocumentProperties>(File.ReadAllText(path))!;
            if (Mode == "undo-on-save" && path.EndsWith(".iam")) props = props with { References = ["C:/outside/original.ipt"] };
            File.WriteAllText(path, JsonSerializer.Serialize(props with { PartNumber = changes.PartNumber, Custom = changes.Custom }));
            Saved.Add(Path.GetFileName(path));
            return Task.CompletedTask;
        }
        public Task ReplaceReferencesAsync(string path, IReadOnlyDictionary<string, string> map, CancellationToken cancellationToken = default)
        {
            var props = JsonSerializer.Deserialize<DocumentProperties>(File.ReadAllText(path))!;
            var limit = Mode == "none" ? 0 : Mode == "partial" ? 1 : int.MaxValue;
            File.WriteAllText(path, JsonSerializer.Serialize(props with { References = props.References
                .Select((r, i) => i < limit && map.TryGetValue(r, out var target) ? target : r).ToList() }));
            return Task.CompletedTask;
        }
    }

    private static string Model(TempFolders folders, string name, params string[] refs) =>
        folders.AddFile(name, JsonSerializer.Serialize(new DocumentProperties { PartNumber = Path.GetFileNameWithoutExtension(name), References = refs }));

    private static async Task<ImportSession> Analyze(LibraryImportService service, params string[] files)
    {
        var session = await service.AnalyzeAsync(files);
        var category = session.Catalog.FindByPath("Электротехника/Разъёмы")!.Id;
        foreach (var candidate in session.Candidates) { candidate.CategoryId = category; candidate.Manufacturer = "Test"; }
        service.Validate(session);
        Assert.All(session.Candidates, c => Assert.True(c.CanImport, string.Join("; ", c.Issues)));
        return session;
    }

    [PostgresFact]
    public async Task SilentRelink_DoesNotPublishAssemblyOrReferences() => await UnconfirmedRelink("none");
    [PostgresFact]
    public async Task PartialRelink_DoesNotPublishAssemblyOrReferences() => await UnconfirmedRelink("partial");
    [PostgresFact]
    public async Task FinalPropertySaveUndoesRelink_DoesNotPublishAssemblyOrReferences() => await UnconfirmedRelink("undo-on-save");
    [PostgresFact]
    public async Task ReadbackFailure_DoesNotPublishAssemblyOrReferences() => await UnconfirmedRelink("readback-failure");

    private async Task UnconfirmedRelink(string mode)
    {
        await using var db = await TestDatabase.CreateAsync(); using var folders = new TempFolders();
        var store = new DiskStore { Mode = mode };
        var service = new LibraryImportService(db.Database, store, () => new(folders.Library), NullLogger<LibraryImportService>.Instance);
        var a = Model(folders, "PartA.ipt"); var b = Model(folders, "PartB.ipt"); var asm = Model(folders, "Assembly.iam", a, b);
        var session = await Analyze(service, asm, b, a);
        var result = await service.CommitAsync(session);
        Assert.Equal(2, result.Added);
        Assert.Contains("Не подтверждена", Assert.Single(result.Failed).Error);
        Assert.False(File.Exists(folders.InLibrary(session.Candidates.Single(c => c.SourcePath == asm).TargetRelativePath!)));
        await using var context = db.Database.CreateContext();
        Assert.Equal(2, await context.LibraryItems.CountAsync()); Assert.Empty(await context.LibraryItemReferences.ToListAsync());
        Assert.Equal(2, await context.LibraryLog.CountAsync());
        Assert.Equal([a, b], (await store.ReadAsync(asm)).References);
    }

    [PostgresFact]
    public async Task NestedAssemblies_ReverseOrder_ImportsBottomUpAndVerifiesSavedFiles()
    {
        await using var db = await TestDatabase.CreateAsync(); using var folders = new TempFolders(); var store = new DiskStore();
        var service = new LibraryImportService(db.Database, store, () => new(folders.Library), NullLogger<LibraryImportService>.Instance);
        var part = Model(folders, "Part.ipt"); var sub = Model(folders, "Sub.iam", part);
        var middle = Model(folders, "Middle.iam", sub); var root = Model(folders, "Root.iam", middle, sub);
        var result = await service.CommitAsync(await Analyze(service, root, middle, sub, part));
        Assert.Equal(4, result.Added); Assert.Empty(result.Failed);
        Assert.Equal(["Part.ipt", "Sub.iam", "Middle.iam", "Root.iam"], store.Saved);
        foreach (var (source, target) in result.Committed)
        {
            var before = await store.ReadAsync(source); var after = await store.ReadAsync(target);
            Assert.Equal(before.References.Select(r => result.Committed[r]), after.References);
        }
        await using var context = db.Database.CreateContext(); Assert.Equal(4, await context.LibraryItemReferences.CountAsync());
    }

    [PostgresFact]
    public async Task CycleAndDependentAssembly_AreRejected_IndependentPartStillImports()
    {
        await using var db = await TestDatabase.CreateAsync(); using var folders = new TempFolders(); var store = new DiskStore();
        var service = new LibraryImportService(db.Database, store, () => new(folders.Library), NullLogger<LibraryImportService>.Instance);
        var a = Path.Combine(folders.Source, "A.iam"); var b = Model(folders, "B.iam", a);
        Model(folders, "A.iam", b); var parent = Model(folders, "Parent.iam", a); var part = Model(folders, "Independent.ipt");
        var result = await service.CommitAsync(await Analyze(service, parent, a, b, part));
        Assert.Equal(1, result.Added); Assert.Equal(3, result.Failed.Count);
        Assert.All(result.Failed, f => Assert.Contains("Цикл", f.Error)); Assert.Equal(["Independent.ipt"], store.Saved);
    }
}
