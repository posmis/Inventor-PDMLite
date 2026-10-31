using Microsoft.Extensions.Logging.Abstractions;
using PdmLite.Core.Documents;
using PdmLite.Data.Library;
using PdmLite.Index;
using PdmLite.Library.Backup;
using PdmLite.Library.Checks;
using PdmLite.Library.Import;
using PdmLite.Library.Items;
using PdmLite.Library.Storage;

namespace PdmLite.Library.Tests;

/// <summary>Ошибки, найденные при втором разборе кода, и граничные случаи.</summary>
public class SecondReviewTests
{
    private static ReferenceIndexService Index(TestDatabase db, FakePropertyStore store) =>
        new(db.Database, store, NullLogger<ReferenceIndexService>.Instance);

    [PostgresFact]
    public async Task Index_NestedProjects_DoNotConflict()
    {
        await using var db = await TestDatabase.CreateAsync();
        using var folders = new TempFolders();
        var store = new FakePropertyStore();
        var part = folders.AddFile("Узел/Деталь.ipt", "p");
        var asm = folders.AddFile("Узел/Узел.iam", "a");
        var top = folders.AddFile("Изделие.iam", "t");
        store.Files[asm] = new DocumentProperties { References = [part] };
        store.Files[top] = new DocumentProperties { References = [asm] };
        var index = Index(db, store);
        var child = Path.Combine(folders.Source, "Узел");

        await index.ScanAsync(folders.Source); // весь проект
        Assert.True(await index.IsIndexedAsync(child)); // вложенная папка — уже в индексе

        var nested = await index.ScanAsync(child); // открыли подпапку как отдельный проект — без ошибки уникальности
        Assert.Equal(2, nested.Files);
        Assert.Equal(0, nested.Read);
        File.AppendAllText(part, "изменена");
        Assert.Equal(1, (await index.ScanAsync(child)).Read);
        Assert.Equal(0, (await index.ScanAsync(folders.Source)).Read); // родитель видит, что деталь уже перечитана

        var used = await index.WhereUsedAsync(part, allLevels: true);
        Assert.Equal([asm, top], used.Select(u => u.Path));
    }

    [PostgresFact]
    public async Task Index_ParallelScans_AreSerialized()
    {
        await using var db = await TestDatabase.CreateAsync();
        using var folders = new TempFolders();
        var store = new FakePropertyStore();
        for (var i = 0; i < 30; i++)
            folders.AddFile($"Детали/Деталь {i}.ipt", i.ToString());
        var index = Index(db, store);

        var results = await Task.WhenAll(index.ScanAsync(folders.Source), index.ScanAsync(folders.Source));

        Assert.Equal(30, results.Sum(r => r.Read)); // каждый файл прочитан один раз
        Assert.All(results, r => Assert.Empty(r.Errors));
    }

    [PostgresFact]
    public async Task Index_ReadsWithoutGeometry_ImportWithGeometry()
    {
        await using var db = await TestDatabase.CreateAsync();
        using var folders = new TempFolders();
        var store = new FakePropertyStore();
        var bolt = folders.AddFile("Болт M8x20 ГОСТ 7798-70.ipt", "b");

        await Index(db, store).ScanAsync(folders.Source);
        Assert.Equal(0, store.GeometryReads); // масс-свойства при сканировании проекта не считаются

        var import = new LibraryImportService(db.Database, store, () => new LibraryStorage(folders.Library), NullLogger<LibraryImportService>.Instance);
        await import.AnalyzeAsync([bolt]);
        Assert.Equal(1, store.GeometryReads); // а при добавлении в библиотеку — нужны для поиска дубликатов
    }

    [Theory]
    [InlineData("a\0b.ipt")]
    [InlineData("")]
    public void Index_Key_DoesNotThrowOnStrangePaths(string path) => _ = ReferenceIndexService.Key(path);

    [Theory]
    [InlineData("items", "\"items\"")]
    [InlineData("we\"ird", "\"we\"\"ird\"")]
    public void Backup_QuoteIdentifier(string name, string expected) =>
        Assert.Equal(expected, LibraryBackupService.QuoteIdentifier(name));

    [PostgresFact]
    public async Task Backup_EmptyLibrary_CreateAndRestore()
    {
        await using var db = await TestDatabase.CreateAsync();
        using var folders = new TempFolders();
        var backups = new LibraryBackupService(db.Database, () => new LibraryStorage(folders.Library),
            () => new BackupOptions(Path.Combine(folders.Root, "Backups"), 10, 7), NullLogger<LibraryBackupService>.Instance);
        Assert.False(Directory.Exists(folders.Library)); // папки библиотеки ещё нет

        var empty = await backups.CreateAsync(BackupReason.Manual);
        Assert.Equal(0, empty.Manifest.Items);

        await backups.RestoreAsync(empty.Path);
        Assert.True(Directory.Exists(folders.Library));
        Assert.Equal(2, backups.List().Count); // ручная и «перед восстановлением»
    }

    [Theory]
    [InlineData("Болт M8-6gx30.109(S13) ГОСТ 7798-70:1", "Болт M8-6gx30.109(S13) ГОСТ 7798-70", true)]
    [InlineData("Болт M8-6gx30.109(S13) ГОСТ 7798-70:1", "Болт M8-6gx30.109(S1) ГОСТ 7798-70", false)] // скобки — не шаблон
    [InlineData("Винт [c]+:2", "Винт [c]+", true)]
    public void OccurrenceNaming_SpecialCharacters(string name, string baseName, bool expected) =>
        Assert.Equal(expected, PdmLite.Core.Documents.OccurrenceNaming.IsDefault(name, baseName));

    [Fact]
    public void LibraryMatcher_CompoundStandardNames()
    {
        static LibraryItemSummary Item(string designation) =>
            new(Guid.NewGuid(), designation, LibraryItemKind.Standard, LibraryDocumentType.Part, null, null, 1, "Крепёж/Винты",
                $"{designation}.ipt", 1, DateTime.UtcNow, "test");
        var screw = Item("Винт M6x20 ГОСТ Р ИСО 4762-2012");
        var matcher = new LibraryMatcher([screw, Item("Винт M6x20 ГОСТ 11738-84")]);

        var found = matcher.Find("Винт ГОСТ Р ИСО 4762-2012 M6x20");

        Assert.Same(screw, Assert.Single(found).Item); // стандарт с пробелами и годом — тот же; другой ГОСТ — нет
    }
}
