using Microsoft.EntityFrameworkCore;
using PdmLite.Core.Documents;
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
        var store = new FakePropertyStore();
        var (items, item) = await ImportOneAsync(db, store, folders, "Гайка M8-6H.10 ГОСТ 5915-70.ipt");

        await items.ApplyEditAsync(await items.LoadCatalogAsync(), item.Id,
            new LibraryItemEdit("Гайка M8 нерж.", LibraryItemKind.Purchased, " Würth ", item.CategoryId));

        var saved = Assert.Single(await items.ListAsync(await items.LoadCatalogAsync()));
        Assert.Equal("Крепёж/Гайки/Würth/Гайка M8 нерж.ipt", saved.RelativePath);
        Assert.Equal("OtherProducts", store.Written[Path.Combine(folders.Library, "Крепёж", "Гайки", "Würth", "Гайка M8 нерж.ipt")].Custom["SPEC_Section"]);
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

    [PostgresFact]
    public async Task RewriteProperties_WritesOnlyOutdatedFiles_AndKeepsThemReadOnly()
    {
        await using var db = await TestDatabase.CreateAsync();
        using var folders = new TempFolders();
        var store = new FakePropertyStore();
        var (items, item) = await ImportOneAsync(db, store, folders, "Гайка M8-6H.10 ГОСТ 5915-70.ipt");
        var catalog = await items.LoadCatalogAsync();
        var full = Path.Combine(folders.Library, item.RelativePath.Replace('/', Path.DirectorySeparatorChar));

        // Свойства только что записаны — переписывать нечего.
        var writes = store.WriteCount;
        var first = await items.RewritePropertiesAsync(catalog, null);
        Assert.Equal((1, 0, 1), (first.Checked, first.Updated, first.UpToDate));
        Assert.Equal(writes, store.WriteCount);

        // Файл «устарел» (как до появления SPEC_Section) — запишется только он.
        var custom = new Dictionary<string, string>(store.Files[full].Custom);
        custom.Remove("SPEC_Section");
        store.Files[full] = store.Files[full] with { Custom = custom };
        string hashBefore;
        await using (var context = db.Database.CreateContext())
            hashBefore = (await context.LibraryItems.SingleAsync()).FileHash;

        var second = await items.RewritePropertiesAsync(catalog, [item.Id]);

        Assert.Equal((1, 1, 0), (second.Checked, second.Updated, second.UpToDate));
        Assert.Equal("StandardProducts", store.Files[full].Custom["SPEC_Section"]);
        Assert.True(File.GetAttributes(full).HasFlag(FileAttributes.ReadOnly));
        await using var after = db.Database.CreateContext();
        var saved = await after.LibraryItems.Include(i => i.Versions).SingleAsync();
        Assert.NotEqual(hashBefore, saved.FileHash);
        Assert.Equal(saved.FileHash, saved.Versions.Single().FileHash);
        Assert.Equal(2, await after.LibraryLog.CountAsync(l => l.Action == "rewrite-properties"));
    }

    [PostgresFact]
    public async Task RewriteProperties_FailedFile_IsRestored_OthersContinue_AndCancelStopsBetweenFiles()
    {
        await using var db = await TestDatabase.CreateAsync();
        using var folders = new TempFolders();
        var store = new FakePropertyStore();
        LibraryStorage Storage() => new(folders.Library);
        var import = new LibraryImportService(db.Database, store, Storage, NullLogger<LibraryImportService>.Instance);
        var items = new LibraryItemService(db.Database, store, Storage, NullLogger<LibraryItemService>.Instance);
        folders.AddFile("Гайка M8-6H.10 ГОСТ 5915-70.ipt", "a");
        folders.AddFile("Гайка M10-6H.10 ГОСТ 5915-70.ipt", "b");
        Assert.Equal(2, (await import.CommitAsync(await import.AnalyzeAsync([folders.Source]))).Added);
        var catalog = await items.LoadCatalogAsync();
        var all = await items.ListAsync(catalog);
        foreach (var path in all.Select(i => Path.Combine(folders.Library, i.RelativePath.Replace('/', Path.DirectorySeparatorChar))))
            store.Files[path] = new DocumentProperties(); // оба файла устарели

        var m8 = Path.Combine(folders.Library, all.Single(i => i.Designation.Contains("M8")).RelativePath.Replace('/', Path.DirectorySeparatorChar));
        var content = File.ReadAllText(m8);
        store.FailWriteFor = "M8";
        var result = await items.RewritePropertiesAsync(catalog, null);

        Assert.Equal(1, result.Updated);
        Assert.Contains("M8", Assert.Single(result.Failed));
        Assert.Equal(content, File.ReadAllText(m8));
        Assert.True(File.GetAttributes(m8).HasFlag(FileAttributes.ReadOnly));

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => items.RewritePropertiesAsync(catalog, null, ct: cancelled.Token));
    }

    [PostgresFact]
    public async Task RewriteProperties_RemovesLeftoverContentCenterLink()
    {
        await using var db = await TestDatabase.CreateAsync();
        using var folders = new TempFolders();
        var store = new FakePropertyStore();
        var (items, item) = await ImportOneAsync(db, store, folders, "Болт M6-6gx20.88.016 ГОСТ 7798-70.ipt");
        var full = Path.Combine(folders.Library, item.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        store.Files[full] = store.Files[full] with { IsContentCenter = true }; // внесён до появления очистки

        var result = await items.RewritePropertiesAsync(await items.LoadCatalogAsync(), null);

        Assert.Equal(1, result.Updated);
        Assert.False(store.Files[full].IsContentCenter);
    }

    [PostgresFact]
    public async Task DiagnoseProperties_ListsDifferences_WithoutChangingFiles()
    {
        await using var db = await TestDatabase.CreateAsync();
        using var folders = new TempFolders();
        var store = new FakePropertyStore();
        var (items, item) = await ImportOneAsync(db, store, folders, "Гайка M8-6H.10 ГОСТ 5915-70.ipt");
        var catalog = await items.LoadCatalogAsync();
        var full = Path.Combine(folders.Library, item.RelativePath.Replace('/', Path.DirectorySeparatorChar));

        Assert.False(Assert.Single(await items.DiagnosePropertiesAsync(catalog, null)).NeedsUpdate);

        var custom = new Dictionary<string, string>(store.Files[full].Custom);
        custom.Remove("SPEC_Section");
        store.Files[full] = store.Files[full] with { Custom = custom, Title = "Гайка", IsContentCenter = true };
        var writes = store.WriteCount;
        var content = File.ReadAllText(full);

        var diff = Assert.Single(await items.DiagnosePropertiesAsync(catalog, [item.Id]));

        Assert.True(diff.NeedsUpdate);
        Assert.Contains(diff.Changes, c => c.Property == "SPEC_Section" && c.Current == null && c.Expected == "StandardProducts");
        Assert.Contains(diff.Changes, c => c.Property == "Наименование" && c.Current == "Гайка");
        Assert.Contains(diff.Changes, c => c.Property == "Метка Content Center");
        Assert.Equal(writes, store.WriteCount);       // ничего не записано
        Assert.Equal(content, File.ReadAllText(full)); // файл не тронут
    }

    [PostgresFact]
    public async Task DiagnoseProperties_MissingFile_IsReportedAsError()
    {
        await using var db = await TestDatabase.CreateAsync();
        using var folders = new TempFolders();
        var (items, item) = await ImportOneAsync(db, new FakePropertyStore(), folders, "Гайка M8-6H.10 ГОСТ 5915-70.ipt");
        LibraryStorage.Delete(Path.Combine(folders.Library, item.RelativePath.Replace('/', Path.DirectorySeparatorChar)));

        var diff = Assert.Single(await items.DiagnosePropertiesAsync(await items.LoadCatalogAsync(), null));
        Assert.NotNull(diff.Error);
        Assert.False(diff.NeedsUpdate);
    }
}
