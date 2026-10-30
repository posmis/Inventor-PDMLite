using Microsoft.Extensions.Logging.Abstractions;
using PdmLite.Data.Library;
using PdmLite.Library.Import;
using PdmLite.Library.Items;
using PdmLite.Library.Storage;

namespace PdmLite.Library.Tests;

/// <summary>Подстановка ненайденных ссылок сборок: текущие и прежние пути компонентов.</summary>
public class PathIndexTests
{
    private static readonly Guid Ring = Guid.NewGuid();
    private static readonly Guid Bolt = Guid.NewGuid();

    private static LibraryPathIndex Index() => new(
        [
            (Ring, "Крепёж/Кольца стопорные/DIN 471/Стопорное кольцо DIN 471-15x1.ipt", "Стопорное кольцо DIN 471-15x1"),
            (Bolt, "Крепёж/Болты/ГОСТ 7798-70/Болт M8-6gx20.109 ГОСТ 7798-70.ipt", "Болт M8-6gx20.109 ГОСТ 7798-70"),
        ],
        [
            ("Крепёж/Кольца стопорные/DIN 471-15/Стопорное кольцо DIN 471-15x1.ipt", Ring),
            ("Крепёж/Болты/ГОСТ 7798-70/Болт M8-6gx20.88 ГОСТ 7798-70.ipt", Bolt),
            ("Крепёж/Болты/удалённый.ipt", Guid.NewGuid()),
        ]);

    [Theory]
    [InlineData(@"C:\Users\u\Documents\PdmLite\Library\Крепёж\Кольца стопорные\DIN 471-15\Стопорное кольцо DIN 471-15x1.ipt")]
    [InlineData("Крепёж/Кольца стопорные/DIN 471-15/Стопорное кольцо DIN 471-15x1.ipt")]
    [InlineData(@"Крепёж\Кольца стопорные\DIN 471-15\Стопорное кольцо DIN 471-15x1.ipt")]
    public void OldPath_FoundEvenIfExistenceUnknown(string reference) =>
        Assert.Equal("Крепёж/Кольца стопорные/DIN 471/Стопорное кольцо DIN 471-15x1.ipt", Index().Find(reference, missing: false));

    [Fact]
    public void RenamedDesignation_FoundByOldPath() =>
        Assert.EndsWith("Болт M8-6gx20.109 ГОСТ 7798-70.ipt", Index().Find(@"D:\Lib\Крепёж\Болты\ГОСТ 7798-70\Болт M8-6gx20.88 ГОСТ 7798-70.ipt", false));

    [Fact]
    public void MissingFile_FoundByCurrentTail_FileName_OrDesignation()
    {
        var index = Index();
        // Папку библиотеки перенесли.
        Assert.NotNull(index.Find(@"E:\Старая\Крепёж\Болты\ГОСТ 7798-70\Болт M8-6gx20.109 ГОСТ 7798-70.ipt", missing: true));
        // Файл лежал где-то ещё (копия до внесения в библиотеку).
        Assert.NotNull(index.Find(@"C:\Проект\Стандартные\Стопорное кольцо DIN 471-15x1.ipt", missing: true));
        // Имя файла записано по-другому, но обозначение то же (кириллица, пробелы).
        Assert.NotNull(index.Find(@"C:\Проект\Болт  М8-6gх20.109 ГОСТ 7798-70.ipt", missing: true));
    }

    [Fact]
    public void ProjectFiles_AreNotTouched()
    {
        var index = Index();
        Assert.Null(index.Find(@"C:\Проект\Рама.ipt", missing: true));
        Assert.Null(index.Find(@"C:\Проект\Стопорное кольцо DIN 471-15x1.ipt", missing: false)); // файл есть — не подменяем
        Assert.Null(index.Find("Крепёж/Болты/удалённый.ipt", missing: true)); // компонент удалён
    }

    [PostgresFact]
    public async Task Index_FromDatabase_KnowsPathBeforeEdit()
    {
        await using var db = await TestDatabase.CreateAsync();
        using var folders = new TempFolders();
        var store = new FakePropertyStore();
        LibraryStorage Storage() => new(folders.Library);
        var import = new LibraryImportService(db.Database, store, Storage, NullLogger<LibraryImportService>.Instance);
        var items = new LibraryItemService(db.Database, store, Storage, NullLogger<LibraryItemService>.Instance);

        var session = await import.AnalyzeAsync([folders.AddFile("Стопорное кольцо DIN 471-15x1.ipt", "r")]);
        session.Candidates[0].ManualStandard = "DIN 471-15"; // как было до исправления распознавания
        import.Validate(session);
        Assert.Equal(1, (await import.CommitAsync(session)).Added);

        var catalog = await items.LoadCatalogAsync();
        var item = Assert.Single(await items.ListAsync(catalog));
        var oldPath = item.RelativePath;
        await items.ApplyEditAsync(catalog, item.Id, new LibraryItemEdit(item.Designation, LibraryItemKind.Standard, null, item.CategoryId));
        var newPath = Assert.Single(await items.ListAsync(catalog)).RelativePath;
        Assert.NotEqual(oldPath, newPath);

        var index = await items.LoadPathIndexAsync();
        Assert.Equal(newPath, index.Find(Path.Combine(folders.Library, oldPath), missing: false));
    }
}
