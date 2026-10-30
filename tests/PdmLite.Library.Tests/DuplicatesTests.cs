using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PdmLite.Core.Documents;
using PdmLite.Data.Library;
using PdmLite.Library.Import;
using PdmLite.Library.Items;
using PdmLite.Library.Storage;

namespace PdmLite.Library.Tests;

/// <summary>Копия компонента, поиск и объединение дубликатов.</summary>
public class DuplicatesTests
{
    private static (LibraryImportService Import, LibraryItemService Items) Services(TestDatabase db, FakePropertyStore store, TempFolders folders)
    {
        LibraryStorage Storage() => new(folders.Library);
        return (new LibraryImportService(db.Database, store, Storage, NullLogger<LibraryImportService>.Instance),
                new LibraryItemService(db.Database, store, Storage, NullLogger<LibraryItemService>.Instance));
    }

    [PostgresFact]
    public async Task Duplicate_CopiesFile_WithNewDesignationAndProperties()
    {
        await using var db = await TestDatabase.CreateAsync();
        using var folders = new TempFolders();
        var store = new FakePropertyStore();
        var (import, items) = Services(db, store, folders);
        await import.CommitAsync(await import.AnalyzeAsync([folders.AddFile("Болт M8-6gx20.109.016 ГОСТ 7798-70.ipt", "bolt")]));
        var catalog = await items.LoadCatalogAsync();
        var source = Assert.Single(await items.ListAsync(catalog));

        // Та же копия без смены обозначения — нельзя.
        var same = await items.CheckDuplicateAsync(catalog, source.Id, new LibraryItemEdit(source.Designation, source.Kind, null, source.CategoryId));
        Assert.Contains(same.Issues, i => i.Message.Contains("должно быть своё"));

        var id = await items.DuplicateAsync(catalog, source.Id,
            new LibraryItemEdit("Болт M8-6gx20.109.40X.019 ГОСТ 7798-70", LibraryItemKind.Standard, null, source.CategoryId));

        var all = await items.ListAsync(catalog);
        Assert.Equal(2, all.Count);
        var copy = all.Single(i => i.Id == id);
        Assert.Equal("ГОСТ 7798-70", copy.Standard);
        var full = Path.Combine(folders.Library, copy.RelativePath);
        Assert.True(File.Exists(full));
        Assert.True(File.Exists(Path.Combine(folders.Library, source.RelativePath)));
        Assert.Equal("Болт M8-6gx20.109.40X.019 ГОСТ 7798-70", store.Written[full].PartNumber);
        Assert.Equal(id.ToString(), store.Written[full].Custom[LibraryProperties.LibraryId]);
        await using var context = db.Database.CreateContext();
        Assert.Equal("duplicate", (await context.LibraryLog.OrderByDescending(l => l.At).FirstAsync()).Action);
    }

    [Theory]
    [InlineData("Болт M8-6gx20.109 ГОСТ 7798-70", "Болт M8-6g x 20 .109 ГОСТ 7798-70")]
    [InlineData("Гайка M8-6H.10 ГОСТ 5915-70", "гайка м8 - 6н.10 гост 5915-70")]
    public void LooseKey_IgnoresSpacesAndSigns(string a, string b) =>
        Assert.Equal(LibraryItemService.LooseKey(a), LibraryItemService.LooseKey(b));

    [PostgresFact]
    public async Task FindAndMerge_RelinksAssemblies_RemovesExtra_RemembersPath()
    {
        await using var db = await TestDatabase.CreateAsync();
        using var folders = new TempFolders();
        var store = new FakePropertyStore();
        var (import, items) = Services(db, store, folders);

        // Две записи одного болта: обозначения отличаются только пробелом перед «.109» (ключи разные).
        var a = folders.AddFile("a/Болт M8-6gx20.109 ГОСТ 7798-70.ipt", "bolt-a");
        var b = folders.AddFile("b/Болт M8-6gx20 .109 ГОСТ 7798-70.ipt", "bolt-b");
        var asm = folders.AddFile("Узел.iam", "asm");
        store.Files[b] = new DocumentProperties { PartNumber = "Болт M8-6gx20-.109 ГОСТ 7798-70" };
        store.Files[asm] = new DocumentProperties { PartNumber = "Узел", References = [b] };
        var session = await import.AnalyzeAsync([a, b, asm]);
        var asmCandidate = session.Candidates.Single(c => c.SourcePath == asm);
        asmCandidate.CategoryId = session.Catalog.Leaves.First().Id;
        asmCandidate.Manufacturer = "Свой";
        import.Validate(session);
        var result = await import.CommitAsync(session);
        Assert.Equal(3, result.Added);

        var catalog = await items.LoadCatalogAsync();
        var group = Assert.Single(await items.FindDuplicatesAsync(catalog));
        Assert.Equal(2, group.Items.Count);
        Assert.Contains("пробелами", group.Reason);
        var used = group.Items.Single(e => e.UsedIn == 1).Item; // стоит в узле
        var keep = group.Items.Single(e => e.UsedIn == 0).Item;

        await items.MergeDuplicatesAsync(keep.Id, [used.Id]);

        var all = await items.ListAsync(catalog);
        Assert.Equal(2, all.Count);
        Assert.False(File.Exists(Path.Combine(folders.Library, used.RelativePath)));
        var asmItem = all.Single(i => i.DocumentType == LibraryDocumentType.Assembly);
        var asmFull = Path.Combine(folders.Library, asmItem.RelativePath);
        Assert.Equal(Path.Combine(folders.Library, keep.RelativePath), store.Relinked[asmFull].Values.Last());
        Assert.Equal([asmItem.Designation], await items.UsedInAsync(keep.Id));
        Assert.Empty(await items.FindDuplicatesAsync(catalog));

        // Сборки проектов со старым путём получат оставленный компонент.
        var index = await items.LoadPathIndexAsync();
        Assert.Equal(keep.RelativePath, index.Find(Path.Combine(folders.Library, used.RelativePath), missing: true));
    }
}
