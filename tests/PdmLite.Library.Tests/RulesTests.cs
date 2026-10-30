using PdmLite.Data.Library;
using PdmLite.Library.Catalog;
using PdmLite.Library.Designations;
using PdmLite.Library.Import;
using PdmLite.Library.Storage;

namespace PdmLite.Library.Tests;

/// <summary>Правила размещения, классификатор и хранилище — без базы.</summary>
public sealed class RulesTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("pdmlite-rules-").FullName;

    private static LibraryCatalog Catalog(params LibraryCategoryRule[] rules) => LibraryCatalog.Create(
    [
        new LibraryCategory { Id = 1, Name = "Крепёж" },
        new LibraryCategory { Id = 2, Name = "Болты", ParentId = 1 },
        new LibraryCategory { Id = 3, Name = "Гайки", ParentId = 1 },
        new LibraryCategory { Id = 4, Name = "Уплотнения" },
        new LibraryCategory { Id = 5, Name = "Кольца уплотнительные", ParentId = 4 },
        new LibraryCategory { Id = 6, Name = "Электротехника" },
        new LibraryCategory { Id = 7, Name = "Разъёмы", ParentId = 6 },
    ], rules);

    private static LibraryCategoryRule Word(string pattern, int category, bool learned = false) =>
        new() { Kind = LibraryRuleKind.FirstWord, Pattern = pattern, CategoryId = category, IsLearned = learned };

    private static LibraryCategoryRule Std(string standard, int category, bool learned = true) =>
        new() { Kind = LibraryRuleKind.Standard, Pattern = DesignationNormalizer.ToKey(standard), CategoryId = category, IsLearned = learned };

    [Fact]
    public void Catalog_BuildsPaths_AndMarksLeaves()
    {
        var catalog = Catalog();
        Assert.Equal("Крепёж/Болты", catalog.Get(2)!.Path);
        Assert.False(catalog.Get(1)!.IsLeaf);
        Assert.True(catalog.Get(2)!.IsLeaf);
        Assert.Equal([2, 3, 5, 7], catalog.Leaves.Select(c => c.Id).Order());
        Assert.Null(catalog.Get(99));
    }

    [Fact]
    public void Suggest_PrefersStandardRule_ThenLongestLeadingWords()
    {
        var catalog = Catalog(Word("кольцо", 5), Word("кольцо уплотнительное", 5), Word("болт", 2), Std("ГОСТ 13943-86", 3));

        Assert.Equal(2, catalog.Suggest("Болт M8-6gx20 ГОСТ 7798-70", "ГОСТ 7798-70")!.Id);
        Assert.Equal(3, catalog.Suggest("Кольцо 42 ГОСТ 13943-86", "ГОСТ 13943-86")!.Id); // правило стандарта важнее слова
        Assert.Equal(5, catalog.Suggest("Кольцо уплотнительное 011-014-19 ГОСТ 9833-73", "ГОСТ 9833-73")!.Id);
        Assert.Null(catalog.Suggest("Неизвестная штука 42", null));
    }

    [Fact]
    public void Place_StandardItem_BuildsPathByStandard()
    {
        var placement = LibraryRules.Place(Catalog(), new LibraryStorage(_root), " Болт М8-6gх20.109  ГОСТ 7798-70 ",
            LibraryItemKind.Standard, null, 2, ".ipt");

        Assert.False(placement.HasErrors);
        Assert.Equal("Болт M8-6gx20.109 ГОСТ 7798-70", placement.Designation);
        Assert.Equal("ГОСТ 7798-70", placement.Standard);
        Assert.Equal("Крепёж/Болты/ГОСТ 7798-70/Болт M8-6gx20.109 ГОСТ 7798-70.ipt", placement.RelativePath);
    }

    [Fact]
    public void Place_PurchasedWithoutManufacturer_WarnsAndUsesFallbackFolder()
    {
        var placement = LibraryRules.Place(Catalog(), new LibraryStorage(_root), "Разъём X1", LibraryItemKind.Purchased, "  ", 7, ".iam");

        Assert.False(placement.HasErrors);
        Assert.Null(placement.Standard);
        Assert.Null(placement.Manufacturer);
        Assert.Contains(placement.Issues, i => i.Severity == ImportSeverity.Warning);
        Assert.Equal($"Электротехника/Разъёмы/{LibraryStorage.NoManufacturerFolder}/Разъём X1.iam", placement.RelativePath);
    }

    [Theory]
    [InlineData("", LibraryItemKind.Standard, 2, "Не указано обозначение")]
    [InlineData("Болт без стандарта", LibraryItemKind.Standard, 2, "не найден стандарт")]
    [InlineData("Болт M8 ГОСТ 7798-70", LibraryItemKind.Standard, null, "Не выбран раздел")]
    [InlineData("Болт M8 ГОСТ 7798-70", LibraryItemKind.Standard, 1, "выберите группу")]
    public void Place_ReportsErrors(string designation, LibraryItemKind kind, int? category, string expected)
    {
        var placement = LibraryRules.Place(Catalog(), new LibraryStorage(_root), designation, kind, null, category, ".ipt");
        Assert.True(placement.HasErrors);
        Assert.Contains(placement.Issues, i => i.Severity == ImportSeverity.Error && i.Message.Contains(expected));
    }

    [Fact]
    public void Place_TooLongPath_IsError()
    {
        var placement = LibraryRules.Place(Catalog(), new LibraryStorage(_root), "Разъём " + new string('X', 250), LibraryItemKind.Purchased,
            "Harting", 7, ".ipt");
        Assert.Contains(placement.Issues, i => i.Message.Contains("Слишком длинный путь"));
    }

    [Fact]
    public void Storage_ContainsOnlyFilesInsideRoot()
    {
        var storage = new LibraryStorage(_root);
        Assert.True(storage.Contains(Path.Combine(_root, "a", "b.ipt")));
        Assert.False(storage.Contains(_root));
        Assert.False(storage.Contains(_root + "-other" + Path.DirectorySeparatorChar + "b.ipt"));
    }

    [Fact]
    public void Storage_RemoveEmptyFolders_StopsAtRoot_AndKeepsNonEmpty()
    {
        var storage = new LibraryStorage(_root);
        var deep = Directory.CreateDirectory(Path.Combine(_root, "A", "B", "C")).FullName;
        File.WriteAllText(Path.Combine(_root, "A", "keep.txt"), "");

        storage.RemoveEmptyFolders(deep);

        Assert.False(Directory.Exists(Path.Combine(_root, "A", "B")));
        Assert.True(Directory.Exists(Path.Combine(_root, "A")));
        Assert.True(Directory.Exists(_root));
    }

    [Fact]
    public void Storage_BackupIsWritable_AndRestoreMakesReadOnly()
    {
        var file = Path.Combine(_root, "x.ipt");
        File.WriteAllText(file, "original");
        LibraryStorage.MakeReadOnly(file);

        var backup = LibraryStorage.Backup(file);
        Assert.False(File.GetAttributes(backup).HasFlag(FileAttributes.ReadOnly));

        LibraryStorage.ClearReadOnly(file);
        File.WriteAllText(file, "changed");
        LibraryStorage.Restore(backup, file);

        Assert.Equal("original", File.ReadAllText(file));
        Assert.True(File.GetAttributes(file).HasFlag(FileAttributes.ReadOnly));
        LibraryStorage.Delete(backup);
        Assert.False(File.Exists(backup));
    }

    [Fact]
    public void Storage_CopyIn_DoesNotOverwrite_AndClearsReadOnly()
    {
        var storage = new LibraryStorage(Path.Combine(_root, "lib"));
        var source = Path.Combine(_root, "src.ipt");
        File.WriteAllText(source, "data");
        LibraryStorage.MakeReadOnly(source);

        var copy = storage.CopyIn(source, "A/B/c.ipt");
        Assert.False(File.GetAttributes(copy).HasFlag(FileAttributes.ReadOnly));
        Assert.Throws<IOException>(() => storage.CopyIn(source, "A/B/c.ipt"));
    }

    [Fact]
    public void LibraryProperties_WriteDesignationEverywhere()
    {
        var id = Guid.NewGuid();
        var changes = LibraryProperties.For("Болт M8 ГОСТ 7798-70", LibraryItemKind.Standard, "ГОСТ 7798-70", null, "Крепёж/Болты", id);
        Assert.Equal("Болт M8 ГОСТ 7798-70", changes.PartNumber);
        Assert.Equal(changes.PartNumber, changes.Description);
        Assert.Equal(changes.PartNumber, changes.Title);
        Assert.Equal("", changes.Custom[LibraryProperties.Manufacturer]);
        Assert.Equal(id.ToString(), changes.Custom[LibraryProperties.LibraryId]);
        Assert.Equal("StandardProducts", changes.Custom[LibraryProperties.SpecSection]);
        Assert.Equal("OtherProducts", LibraryProperties.For("Ввод Lapp", LibraryItemKind.Purchased, null, "Lapp", "Э").Custom["SPEC_Section"]);
        Assert.False(LibraryProperties.For("x", LibraryItemKind.Standard, null, null, "y").Custom.ContainsKey(LibraryProperties.LibraryId));
    }

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(_root, true);
    }
}
