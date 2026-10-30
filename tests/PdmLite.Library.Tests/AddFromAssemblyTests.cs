using Microsoft.Extensions.Logging.Abstractions;
using PdmLite.Core.Documents;
using PdmLite.Library.Import;
using PdmLite.Library.Storage;

namespace PdmLite.Library.Tests;

/// <summary>Добавление из сборки: что заменять в сборке после внесения.</summary>
public class AddFromAssemblyTests
{
    private static LibraryImportService Service(TestDatabase db, FakePropertyStore store, TempFolders folders) =>
        new(db.Database, store, () => new LibraryStorage(folders.Library), NullLogger<LibraryImportService>.Instance);

    [PostgresFact]
    public async Task Commit_ReportsSourceToLibraryPaths()
    {
        await using var db = await TestDatabase.CreateAsync();
        using var folders = new TempFolders();
        var nut = folders.AddFile("Гайка M8-6H.10.016 ГОСТ 5915-70.ipt", "nut");
        var service = Service(db, new FakePropertyStore(), folders);

        var session = await service.AnalyzeAsync([nut]);
        var result = await service.CommitAsync(session);

        var (source, target) = Assert.Single(result.Committed);
        Assert.Equal(nut, source);
        Assert.StartsWith(folders.Library, target);
        Assert.True(File.Exists(target));
    }

    [PostgresFact]
    public async Task AlreadyImportedFiles_AreExcluded_WithTheirLibraryPath()
    {
        await using var db = await TestDatabase.CreateAsync();
        using var folders = new TempFolders();
        var store = new FakePropertyStore();
        var bolt = folders.AddFile("Болт M8-6gx20.109 ГОСТ 7798-70.ipt", "bolt");
        var nut = folders.AddFile("Гайка M8-6H.10.016 ГОСТ 5915-70.ipt", "nut");
        var asm = folders.AddFile("Узел крепления.iam", "asm");
        store.Files[asm] = new DocumentProperties { PartNumber = "Узел крепления", References = [bolt, nut] };
        var service = Service(db, store, folders);

        var first = await service.CommitAsync(await service.AnalyzeAsync([bolt]));
        var boltInLibrary = first.Committed[bolt];

        // Подсборка выделена в Inventor: она и все её компоненты.
        var session = await service.AnalyzeAsync([asm, bolt, nut]);
        var a = session.Candidates.Single(c => c.SourcePath == asm);
        a.CategoryId = session.Catalog.Leaves.First().Id;
        a.Manufacturer = "Свой";
        var known = service.ExcludeAlreadyInLibrary(session);

        Assert.Equal(boltInLibrary, Assert.Single(known).Value);
        Assert.False(session.Candidates.Single(c => c.SourcePath == bolt).Include);
        Assert.Equal(ImportStatus.Excluded, session.Candidates.Single(c => c.SourcePath == bolt).Status);
        // Болт уже в библиотеке — сборку можно вносить вместе с гайкой.
        Assert.True(session.Candidates.Single(c => c.SourcePath == asm).CanImport,
            string.Join("; ", session.Candidates.Single(c => c.SourcePath == asm).Issues.Select(i => i.Message)));
        Assert.True(session.Candidates.Single(c => c.SourcePath == nut).CanImport);
    }
}
