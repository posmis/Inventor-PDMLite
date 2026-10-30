using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PdmLite.Core.Documents;
using PdmLite.Data.Library;
using PdmLite.Library.Import;
using PdmLite.Library.Items;
using PdmLite.Library.Storage;

namespace PdmLite.Library.Tests;

public class ItemTests
{
    private static (LibraryImportService Import, LibraryItemService Items) Services(TestDatabase db, FakePropertyStore store, TempFolders folders)
    {
        LibraryStorage Storage() => new(folders.Library);
        return (new LibraryImportService(db.Database, store, Storage, NullLogger<LibraryImportService>.Instance),
                new LibraryItemService(db.Database, store, Storage, NullLogger<LibraryItemService>.Instance));
    }

    private static async Task ImportAsync(LibraryImportService import, IEnumerable<string> files, Action<ImportCandidate>? adjust = null)
    {
        var session = await import.AnalyzeAsync(files);
        foreach (var c in session.Candidates)
            adjust?.Invoke(c);
        import.Validate(session);
        var result = await import.CommitAsync(session);
        Assert.Empty(result.Failed);
    }

    [PostgresFact]
    public async Task Edit_RenamesFile_RewritesProperties_UpdatesDatabaseAndLog()
    {
        await using var db = await TestDatabase.CreateAsync();
        using var folders = new TempFolders();
        var store = new FakePropertyStore();
        var (import, items) = Services(db, store, folders);
        await ImportAsync(import, [folders.AddFile("Болт M8-6gx20.109 ГОСТ 7798-70.ipt", "b")]);

        var catalog = await items.LoadCatalogAsync();
        var item = Assert.Single(await items.ListAsync(catalog));
        var oldFull = Path.Combine(folders.Library, item.RelativePath.Replace('/', Path.DirectorySeparatorChar));

        var edit = new LibraryItemEdit("Болт М8-6gх25.109 ГОСТ 7798-70", LibraryItemKind.Standard, null, item.CategoryId);
        var check = await items.CheckEditAsync(catalog, item.Id, edit);
        Assert.False(check.HasErrors);
        Assert.Equal("Крепёж/Болты/ГОСТ 7798-70/Болт M8-6gx25.109 ГОСТ 7798-70.ipt", check.NewRelativePath);

        await items.ApplyEditAsync(catalog, item.Id, edit);

        var newFull = Path.Combine(folders.Library, "Крепёж", "Болты", "ГОСТ 7798-70", "Болт M8-6gx25.109 ГОСТ 7798-70.ipt");
        Assert.False(File.Exists(oldFull));
        Assert.True(File.Exists(newFull));
        Assert.True(File.GetAttributes(newFull).HasFlag(FileAttributes.ReadOnly));
        Assert.Equal("Болт M8-6gx25.109 ГОСТ 7798-70", store.Written[newFull].PartNumber);

        await using var context = db.Database.CreateContext();
        var saved = await context.LibraryItems.Include(i => i.Versions).SingleAsync();
        Assert.Equal("Болт M8-6gx25.109 ГОСТ 7798-70", saved.Designation);
        Assert.Equal(saved.RelativePath, saved.Versions.Single().RelativePath);
        Assert.Equal(1, await context.LibraryLog.CountAsync(l => l.Action == "edit"));
    }

    [PostgresFact]
    public async Task Edit_ToExistingDesignation_IsRejected_AndNothingChanges()
    {
        await using var db = await TestDatabase.CreateAsync();
        using var folders = new TempFolders();
        var store = new FakePropertyStore();
        var (import, items) = Services(db, store, folders);
        await ImportAsync(import, [folders.AddFile("Гайка M8-6H.10 ГОСТ 5915-70.ipt", "a"), folders.AddFile("Гайка M10-6H.10 ГОСТ 5915-70.ipt", "b")]);

        var catalog = await items.LoadCatalogAsync();
        var m10 = (await items.ListAsync(catalog)).Single(i => i.Designation.Contains("M10"));
        var edit = new LibraryItemEdit("Гайка М8-6Н.10 ГОСТ 5915-70", LibraryItemKind.Standard, null, m10.CategoryId);

        Assert.True((await items.CheckEditAsync(catalog, m10.Id, edit)).HasErrors);
        await Assert.ThrowsAsync<InvalidOperationException>(() => items.ApplyEditAsync(catalog, m10.Id, edit));
        Assert.True(File.Exists(Path.Combine(folders.Library, m10.RelativePath.Replace('/', Path.DirectorySeparatorChar))));
    }

    [PostgresFact]
    public async Task Edit_ComponentOfLibraryAssembly_RelinksAssembly()
    {
        await using var db = await TestDatabase.CreateAsync();
        using var folders = new TempFolders();
        var store = new FakePropertyStore();
        var (import, items) = Services(db, store, folders);
        var pin = folders.AddFile("Вилка X1 Harting.ipt", "pin");
        var asm = folders.AddFile("Разъём X1 Harting.iam", "asm");
        store.Files[asm] = new DocumentProperties { References = [pin] };

        var catalog = await items.LoadCatalogAsync();
        var connectors = catalog.FindByPath("Электротехника/Разъёмы")!.Id;
        await ImportAsync(import, [pin, asm], c => { c.CategoryId = connectors; c.Manufacturer = "Harting"; });

        var pinItem = (await items.ListAsync(catalog)).Single(i => i.DocumentType == LibraryDocumentType.Part);
        Assert.Equal(["Разъём X1 Harting"], await items.UsedInAsync(pinItem.Id));

        await items.ApplyEditAsync(catalog, pinItem.Id, new LibraryItemEdit("Вилка X1-B Harting", LibraryItemKind.Purchased, "Harting", connectors));

        var asmFull = Path.Combine(folders.Library, "Электротехника", "Разъёмы", "Harting", "Разъём X1 Harting.iam");
        var map = store.Relinked[asmFull];
        Assert.Equal(Path.Combine(folders.Library, "Электротехника", "Разъёмы", "Harting", "Вилка X1-B Harting.ipt"), map.Values.Single());

        // Удалить деталь, входящую в сборку, нельзя; саму сборку — можно.
        await Assert.ThrowsAsync<InvalidOperationException>(() => items.DeleteAsync(pinItem.Id));
        var asmItem = (await items.ListAsync(catalog)).Single(i => i.DocumentType == LibraryDocumentType.Assembly);
        await items.DeleteAsync(asmItem.Id);
        Assert.False(File.Exists(asmFull));
        await items.DeleteAsync(pinItem.Id);
        Assert.Empty(await items.ListAsync(catalog));
        Assert.False(Directory.Exists(Path.Combine(folders.Library, "Электротехника")));
    }
    [PostgresFact]
    public async Task Edit_ManualStandard_AndKindChange_RewriteSpecSection()
    {
        await using var db = await TestDatabase.CreateAsync();
        using var folders = new TempFolders();
        var store = new FakePropertyStore();
        var (import, items) = Services(db, store, folders);
        await ImportAsync(import, [folders.AddFile("Стопорное кольцо 15x1 DIN 471-1.ipt", "r")]);

        var catalog = await items.LoadCatalogAsync();
        var item = Assert.Single(await items.ListAsync(catalog));
        Assert.Equal("DIN 471-1", item.Standard); // распознано неверно — исправим вручную

        await items.ApplyEditAsync(catalog, item.Id, new LibraryItemEdit(item.Designation, item.Kind, null, item.CategoryId, " DIN  471 "));
        item = Assert.Single(await items.ListAsync(catalog));
        Assert.Equal("DIN 471", item.Standard);
        Assert.True(item.StandardIsManual);
        Assert.EndsWith("/DIN 471/Стопорное кольцо 15x1 DIN 471-1.ipt", item.RelativePath);
        var full = Path.Combine(folders.Library, item.RelativePath);
        Assert.Equal("DIN 471", store.Written[full].Custom[LibraryProperties.Standard]);
        Assert.Equal("StandardProducts", store.Written[full].Custom[LibraryProperties.SpecSection]);

        // Смена типа меняет SPEC_Section в файле.
        await items.ApplyEditAsync(catalog, item.Id, new LibraryItemEdit(item.Designation, LibraryItemKind.Purchased, "Seeger", item.CategoryId));
        item = Assert.Single(await items.ListAsync(catalog));
        Assert.False(item.StandardIsManual);
        full = Path.Combine(folders.Library, item.RelativePath);
        Assert.Equal("OtherProducts", store.Written[full].Custom[LibraryProperties.SpecSection]);
        Assert.Equal("OtherProducts", store.Files[full].Custom[LibraryProperties.SpecSection]);
    }
}
