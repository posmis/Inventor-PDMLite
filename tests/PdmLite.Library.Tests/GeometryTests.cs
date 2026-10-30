using Microsoft.Extensions.Logging.Abstractions;
using PdmLite.Core.Documents;
using PdmLite.Data.Library;
using PdmLite.Library.Import;
using PdmLite.Library.Items;
using PdmLite.Library.Storage;

namespace PdmLite.Library.Tests;

/// <summary>Предупреждение о дубликате по геометрии и колонка «В библиотеке».</summary>
public class GeometryTests
{
    private static (LibraryImportService Import, LibraryItemService Items) Services(TestDatabase db, FakePropertyStore store, TempFolders folders)
    {
        LibraryStorage Storage() => new(folders.Library);
        return (new LibraryImportService(db.Database, store, Storage, NullLogger<LibraryImportService>.Instance),
                new LibraryItemService(db.Database, store, Storage, NullLogger<LibraryItemService>.Instance));
    }

    [Theory]
    [InlineData(1.234567, 10.5, 1.234567, 10.5, true)]
    [InlineData(1.234567, 10.5, 1.2345671, 10.5000001, true)]
    [InlineData(1.234567, 10.5, 1.3, 10.5, false)]
    [InlineData(0, 10.5, 0, 10.5, false)] // не прочитано
    public void Same(double v1, double a1, double v2, double a2, bool expected) =>
        Assert.Equal(expected, LibraryGeometry.Same(v1, a1, v2, a2));

    [PostgresFact]
    public async Task SameGeometry_AsLibraryItem_IsWarning_AndShownInLibraryColumn()
    {
        await using var db = await TestDatabase.CreateAsync();
        using var folders = new TempFolders();
        var store = new FakePropertyStore();
        var (import, items) = Services(db, store, folders);
        var m10 = folders.AddFile("Гайка DIN 6334 M8-10.ipt", "nut-10");
        var m12 = folders.AddFile("Гайка DIN 6334 M8-12.ipt", "nut-12");
        var other = folders.AddFile("Гайка DIN 6334 M10-10.ipt", "nut-m10");
        store.Files[m10] = new DocumentProperties { Volume = 2.5, Area = 18.25 };
        store.Files[m12] = new DocumentProperties { Volume = 2.5, Area = 18.25 };
        store.Files[other] = new DocumentProperties { Volume = 4.1, Area = 25.0 };
        Assert.Equal(1, (await import.CommitAsync(await import.AnalyzeAsync([m10]))).Added);

        var session = await import.AnalyzeAsync([m12, other]);

        var c12 = session.Candidates.Single(c => c.SourcePath == m12);
        Assert.Equal(ImportStatus.Warning, c12.Status); // предупреждение, не запрет
        Assert.Contains(c12.Issues, i => i.Severity == ImportSeverity.Warning && i.Message.Contains("«Гайка DIN 6334 M8-10» из библиотеки"));
        Assert.Equal("Похожий (та же геометрия): «Гайка DIN 6334 M8-10»", c12.LibraryMatch);
        var cOther = session.Candidates.Single(c => c.SourcePath == other);
        Assert.Null(cOther.LibraryMatch);
        Assert.DoesNotContain(cOther.Issues, i => i.Message.Contains("геометрия"));

        // Внесли M8-12 — в «Дубликатах» группа по геометрии, по умолчанию не отмечена (может быть законной копией).
        await import.CommitAsync(session);
        var catalog = await items.LoadCatalogAsync();
        var group = Assert.Single(await items.FindDuplicatesAsync(catalog));
        Assert.True(group.GeometryOnly);
        Assert.Equal(2, group.Items.Count);
    }

    [PostgresFact]
    public async Task Check_RemembersGeometry_OfOldItems()
    {
        await using var db = await TestDatabase.CreateAsync();
        using var folders = new TempFolders();
        var store = new FakePropertyStore();
        var (import, items) = Services(db, store, folders);
        await import.CommitAsync(await import.AnalyzeAsync([folders.AddFile("Гайка M8-6H.10 ГОСТ 5915-70.ipt", "n")]));
        var catalog = await items.LoadCatalogAsync();
        var item = Assert.Single(await items.ListAsync(catalog));
        var full = folders.InLibrary(item.RelativePath);
        store.Files[full] = store.Files[full] with { Volume = 3, Area = 20 }; // как будто прочиталось только теперь

        await items.DiagnosePropertiesAsync(catalog, null);

        await using var context = db.Database.CreateContext();
        var saved = context.LibraryItems.Single();
        Assert.Equal(3, saved.Volume);
        Assert.Equal(20, saved.Area);
    }
}
