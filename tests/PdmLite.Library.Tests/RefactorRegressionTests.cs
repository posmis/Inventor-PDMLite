using Microsoft.Extensions.Logging.Abstractions;
using PdmLite.Core.Documents;
using PdmLite.Data.Library;
using PdmLite.Library.Designations;
using PdmLite.Library.Import;
using PdmLite.Library.Items;
using PdmLite.Library.Storage;

namespace PdmLite.Library.Tests;

/// <summary>Ошибки, найденные при разборе кода, и граничные случаи новых функций.</summary>
public class RefactorRegressionTests
{
    private static (LibraryImportService Import, LibraryItemService Items) Services(TestDatabase db, FakePropertyStore store, TempFolders folders)
    {
        LibraryStorage Storage() => new(folders.Library);
        return (new LibraryImportService(db.Database, store, Storage, NullLogger<LibraryImportService>.Instance),
                new LibraryItemService(db.Database, store, Storage, NullLogger<LibraryItemService>.Instance));
    }

    [Theory]
    [InlineData("Винт ISO 4017-M6x20", "ISO 4017")]
    [InlineData("Шпилька ГОСТ 22032-76 M10x40", "ГОСТ 22032-76")]
    [InlineData("Кольцо DIN 471-15", "DIN 471-15")] // без размера после — неоднозначно, берём как есть (можно исправить вручную)
    [InlineData("Шайба DIN 125-1 A 8,4", "DIN 125-1")]
    [InlineData("Гайка DIN 6334 M8-10", "DIN 6334")]
    public void StandardParser_MoreCases(string designation, string expected) =>
        Assert.Equal(expected, StandardParser.Find(designation));

    [Fact]
    public void ImportList_RejectsOtherVersion_AndOverwritesOnSave()
    {
        var dir = Directory.CreateTempSubdirectory("pdmlite-list-").FullName;
        try
        {
            var path = Path.Combine(dir, "a" + ImportList.Extension);
            File.WriteAllText(path, """{"Version":2,"Items":[]}""");
            Assert.Throws<InvalidDataException>(() => ImportList.Load(path));

            new ImportList { Items = [new ImportListEntry { Path = @"C:\x.ipt", Kind = LibraryItemKind.Purchased }] }.Save(path);
            var loaded = ImportList.Load(path);
            Assert.Equal(LibraryItemKind.Purchased, Assert.Single(loaded.Items).Kind);
            Assert.Contains("\"Purchased\"", File.ReadAllText(path)); // перечисления — словами
            Assert.False(File.Exists(path + ".tmp"));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [PostgresFact]
    public async Task ReplaceInDesignations_RejectsEmptyAndBrokenExpressions()
    {
        await using var db = await TestDatabase.CreateAsync();
        using var folders = new TempFolders();
        var (import, _) = Services(db, new FakePropertyStore(), folders);
        var session = await import.AnalyzeAsync([folders.AddFile("Болт M8x20 ГОСТ 7798-70.ipt", "b")]);

        Assert.Throws<ArgumentException>(() => import.ReplaceInDesignations(session, session.Candidates, "", "x", false));
        Assert.ThrowsAny<ArgumentException>(() => import.ReplaceInDesignations(session, session.Candidates, "(", "x", true));
        Assert.Equal(DesignationSource.FileName, session.Candidates[0].Source); // ничего не изменилось
    }

    [PostgresFact]
    public async Task LoadList_ManualStandard_AppliedBeforeDesignationIsPrepared()
    {
        await using var db = await TestDatabase.CreateAsync();
        using var folders = new TempFolders();
        var store = new FakePropertyStore();
        var (import, _) = Services(db, store, folders);
        var file = folders.AddFile("Кольцо 15x1.ipt", "r");
        store.Files[file] = new DocumentProperties { PartNumber = "Стопорное кольцо 15x1", Description = "Кольцо DIN 471-15x1" };

        var listPath = Path.Combine(folders.Root, "l" + ImportList.Extension);
        new ImportList
        {
            Items = [new ImportListEntry { Path = file, Source = DesignationSource.Description, Kind = LibraryItemKind.Standard, ManualStandard = " DIN  471 " }],
        }.Save(listPath);

        var session = await import.LoadListAsync(listPath);

        var c = Assert.Single(session.Candidates);
        Assert.Equal(DesignationSource.Description, c.Source);
        Assert.Equal("DIN 471", c.ManualStandard);
        Assert.Equal("DIN 471", c.Standard);
        Assert.Equal(LibraryItemKind.Standard, c.Kind);
    }

    [PostgresFact]
    public async Task Merge_RejectsKeepInRemoveList_AndPartWithAssembly()
    {
        await using var db = await TestDatabase.CreateAsync();
        using var folders = new TempFolders();
        var store = new FakePropertyStore();
        var (import, items) = Services(db, store, folders);
        var part = folders.AddFile("Болт M8x20 ГОСТ 7798-70.ipt", "p");
        var asm = folders.AddFile("Узел.iam", "a");
        store.Files[asm] = new DocumentProperties { References = [part] };
        var session = await import.AnalyzeAsync([part, asm]);
        var a = session.Candidates.Single(c => c.SourcePath == asm);
        a.CategoryId = session.Catalog.Leaves.First().Id;
        a.Manufacturer = "Свой";
        import.Validate(session);
        Assert.Equal(2, (await import.CommitAsync(session)).Added);

        var all = await items.ListAsync(await items.LoadCatalogAsync());
        var partId = all.Single(i => i.DocumentType == LibraryDocumentType.Part).Id;
        var asmId = all.Single(i => i.DocumentType == LibraryDocumentType.Assembly).Id;

        await Assert.ThrowsAsync<ArgumentException>(() => items.MergeDuplicatesAsync(partId, [partId]));
        await Assert.ThrowsAsync<InvalidOperationException>(() => items.MergeDuplicatesAsync(partId, [asmId]));
        Assert.Equal(2, (await items.ListAsync(await items.LoadCatalogAsync())).Count);
    }

    [PostgresFact]
    public async Task DuplicateAssembly_KeepsComponentList()
    {
        await using var db = await TestDatabase.CreateAsync();
        using var folders = new TempFolders();
        var store = new FakePropertyStore();
        var (import, items) = Services(db, store, folders);
        var part = folders.AddFile("Болт M8x20 ГОСТ 7798-70.ipt", "p");
        var asm = folders.AddFile("Узел крепления.iam", "a");
        store.Files[asm] = new DocumentProperties { References = [part] };
        var session = await import.AnalyzeAsync([part, asm]);
        var a = session.Candidates.Single(c => c.SourcePath == asm);
        a.CategoryId = session.Catalog.Leaves.First().Id;
        a.Manufacturer = "Свой";
        import.Validate(session);
        await import.CommitAsync(session);

        var catalog = await items.LoadCatalogAsync();
        var source = (await items.ListAsync(catalog)).Single(i => i.DocumentType == LibraryDocumentType.Assembly);
        var copyId = await items.DuplicateAsync(catalog, source.Id,
            new LibraryItemEdit("Узел крепления исп.2", LibraryItemKind.Purchased, "Свой", source.CategoryId));

        var boltId = (await items.ListAsync(catalog)).Single(i => i.DocumentType == LibraryDocumentType.Part).Id;
        var usedIn = await items.UsedInAsync(boltId);
        Assert.Equal(2, usedIn.Count); // болт — в исходной сборке и в копии
        Assert.Contains("Узел крепления исп.2", usedIn);
        Assert.NotEqual(Guid.Empty, copyId);
    }

    [Theory]
    [InlineData("Болт M8-6gx20.109", "болт м8-6gх20.109", true)]
    [InlineData("Болт M8-6gx20.109", "Болт M8-6gx20.88", false)]
    [InlineData("Гайка DIN 6334 M8-10", "Гайка DIN 6334 M8-12", false)]
    [InlineData("Шайба A.1,6.01.08кп.016 ГОСТ 11371-78", "Шайба A.16.01.08кп.016 ГОСТ 11371-78", false)] // 1,6 мм и 16 мм
    [InlineData("Шайба A.1,6.01.08кп.016 ГОСТ 11371-78", "Шайба А.1.6.01.08кп.016 ГОСТ 11371-78", true)] // запятая и точка — одно
    [InlineData("Болт M8-6gx20-.109", "Болт M8-6gx20.109", true)] // лишний знак между числами
    [InlineData("Болт M8-6gx20.109", "Болт M8-6gx2010.9", false)]
    public void LooseKey(string a, string b, bool same) =>
        Assert.Equal(same, LibraryItemService.LooseKey(a) == LibraryItemService.LooseKey(b));
}
