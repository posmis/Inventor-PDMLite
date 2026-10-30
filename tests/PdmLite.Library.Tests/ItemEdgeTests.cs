using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PdmLite.Data.Library;
using PdmLite.Library.Import;
using PdmLite.Library.Items;
using PdmLite.Library.Storage;

namespace PdmLite.Library.Tests;

public class ItemEdgeTests
{
    private static async Task<(LibraryItemService Items, LibraryItemSummary Item)> ImportOneAsync(
        TestDatabase db, FakePropertyStore store, TempFolders folders, string fileName)
    {
        LibraryStorage Storage() => new(folders.Library);
        var import = new LibraryImportService(db.Database, store, Storage, NullLogger<LibraryImportService>.Instance);
        var items = new LibraryItemService(db.Database, store, Storage, NullLogger<LibraryItemService>.Instance);
        var result = await import.CommitAsync(await import.AnalyzeAsync([folders.AddFile(fileName, fileName)]));
        Assert.Equal(1, result.Added);
        var catalog = await items.LoadCatalogAsync();
        return (items, Assert.Single(await items.ListAsync(catalog)));
    }

    [PostgresFact]
    public async Task CheckEdit_WithoutChanges_HasNoMoveWarning()
    {
        await using var db = await TestDatabase.CreateAsync();
        using var folders = new TempFolders();
        var (items, item) = await ImportOneAsync(db, new FakePropertyStore(), folders, "Гайка M8-6H.10 ГОСТ 5915-70.ipt");

        var check = await items.CheckEditAsync(await items.LoadCatalogAsync(), item.Id,
            new LibraryItemEdit(item.Designation, item.Kind, item.Manufacturer, item.CategoryId));

        Assert.False(check.HasErrors);
        Assert.Equal(item.RelativePath, check.NewRelativePath);
        Assert.DoesNotContain(check.Issues, i => i.Severity == ImportSeverity.Warning);
    }

    [PostgresFact]
    public async Task Edit_ChangeCategory_MovesFile_CleansOldFolders_AndLearnsRule()
    {
        await using var db = await TestDatabase.CreateAsync();
        using var folders = new TempFolders();
        var store = new FakePropertyStore();
        var (items, item) = await ImportOneAsync(db, store, folders, "Кольцо 42 ГОСТ 13943-86.ipt");
        var catalog = await items.LoadCatalogAsync();
        Assert.Equal("Уплотнения/Кольца уплотнительные", item.CategoryPath);

        var retaining = catalog.FindByPath("Крепёж/Кольца стопорные")!.Id;
        await items.ApplyEditAsync(catalog, item.Id, new LibraryItemEdit(item.Designation, item.Kind, null, retaining));

        Assert.True(File.Exists(Path.Combine(folders.Library, "Крепёж", "Кольца стопорные", "ГОСТ 13943-86", "Кольцо 42 ГОСТ 13943-86.ipt")));
        Assert.False(Directory.Exists(Path.Combine(folders.Library, "Уплотнения")));

        // Следующий компонент того же стандарта попадёт в исправленный раздел.
        var import = new LibraryImportService(db.Database, store, () => new LibraryStorage(folders.Library), NullLogger<LibraryImportService>.Instance);
        var next = await import.AnalyzeAsync([folders.AddFile("Кольцо 52 ГОСТ 13943-86.ipt", "52")]);
        Assert.Equal(retaining, Assert.Single(next.Candidates).CategoryId);
    }

    [PostgresFact]
    public async Task Edit_FailedPropertyWrite_RestoresFileAndKeepsDatabase()
    {
        await using var db = await TestDatabase.CreateAsync();
        using var folders = new TempFolders();
        var store = new FakePropertyStore();
        var (items, item) = await ImportOneAsync(db, store, folders, "Гайка M8-6H.10 ГОСТ 5915-70.ipt");
        var catalog = await items.LoadCatalogAsync();
        var oldFull = Path.Combine(folders.Library, item.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        var content = File.ReadAllText(oldFull);

        store.FailWriteFor = "M10";
        await Assert.ThrowsAsync<IOException>(() =>
            items.ApplyEditAsync(catalog, item.Id, new LibraryItemEdit("Гайка M10-6H.10 ГОСТ 5915-70", item.Kind, null, item.CategoryId)));

        Assert.Equal(content, File.ReadAllText(oldFull));
        Assert.True(File.GetAttributes(oldFull).HasFlag(FileAttributes.ReadOnly));
        Assert.False(File.Exists(oldFull.Replace("M8", "M10")));
        await using var context = db.Database.CreateContext();
        Assert.Equal(item.Designation, (await context.LibraryItems.SingleAsync()).Designation);
        Assert.Equal(0, await context.LibraryLog.CountAsync(l => l.Action == "edit"));
    }

    [PostgresFact]
    public async Task Edit_ToPurchased_UsesManufacturerFolder()
    {
        await using var db = await TestDatabase.CreateAsync();
        using var folders = new TempFolders();
        var (items, item) = await ImportOneAsync(db, new FakePropertyStore(), folders, "Гайка M8-6H.10 ГОСТ 5915-70.ipt");

        await items.ApplyEditAsync(await items.LoadCatalogAsync(), item.Id,
            new LibraryItemEdit("Гайка M8 нерж.", LibraryItemKind.Purchased, " Würth ", item.CategoryId));

        var saved = Assert.Single(await items.ListAsync(await items.LoadCatalogAsync()));
        Assert.Equal("Крепёж/Гайки/Würth/Гайка M8 нерж.ipt", saved.RelativePath);
        Assert.Equal("Würth", saved.Manufacturer);
        Assert.Null(saved.Standard);
    }

    [PostgresFact]
    public async Task Delete_RemovesFileRecordAndEmptyFolders_KeepsLibraryRoot()
    {
        await using var db = await TestDatabase.CreateAsync();
        using var folders = new TempFolders();
        var (items, item) = await ImportOneAsync(db, new FakePropertyStore(), folders, "Гайка M8-6H.10 ГОСТ 5915-70.ipt");

        await items.DeleteAsync(item.Id);

        Assert.True(Directory.Exists(folders.Library));
        Assert.Empty(Directory.EnumerateFileSystemEntries(folders.Library));
        await using var context = db.Database.CreateContext();
        Assert.Equal(0, await context.LibraryItems.CountAsync());
        Assert.Equal(0, await context.LibraryItemVersions.CountAsync());
        Assert.Equal(1, await context.LibraryLog.CountAsync(l => l.Action == "delete"));
    }
}
