using System.IO.Compression;
using Microsoft.Extensions.Logging.Abstractions;
using PdmLite.Core.Documents;
using PdmLite.Data.Library;
using PdmLite.Library.Backup;
using PdmLite.Library.Catalog;
using PdmLite.Library.Checks;
using PdmLite.Library.Import;
using PdmLite.Library.Items;
using PdmLite.Library.Rules;
using PdmLite.Library.Storage;

namespace PdmLite.Library.Tests;

/// <summary>Третий проход: подбор замены по обозначению.</summary>
public class LibraryMatcherTheoryTests
{
    private static LibraryItemSummary Item(string designation, string ext = ".ipt") =>
        new(Guid.NewGuid(), designation, LibraryItemKind.Standard, LibraryDocumentType.Part, null, null, 1, "Крепёж/Болты",
            $"Крепёж/Болты/{designation}{ext}", 1, DateTime.UtcNow, "test");

    [Theory]
    [InlineData("Болт M8-6gx30.109 ГОСТ 7798-70")]
    [InlineData("болт m8-6gx30.109 гост 7798-70")]
    [InlineData("Болт  M8-6gx30.109  ГОСТ 7798-70")]
    [InlineData("Болт М8-6gх30.109 ГОСТ 7798-70")] // кириллические М и х
    public void Exact_IgnoresCaseSpacesAndLookalikes(string query)
    {
        var bolt = Item("Болт M8-6gx30.109 ГОСТ 7798-70");
        var found = new LibraryMatcher([bolt]).Find(query);
        Assert.True(found[0].Exact);
        Assert.Same(bolt, found[0].Item);
    }

    [Fact]
    public void EmptyLibrary_NothingFound() => Assert.Empty(new LibraryMatcher([]).Find("Болт M8 ГОСТ 7798-70"));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("12345")] // без наименования
    public void NoNameOrEmpty_NothingSimilar(string query) =>
        Assert.Empty(new LibraryMatcher([Item("Болт M8 ГОСТ 7798-70")]).Find(query));

    [Fact]
    public void Max_LimitsResults()
    {
        var items = Enumerable.Range(1, 20).Select(i => Item($"Болт M8-6gx30.{i} ГОСТ 7798-70")).ToList();
        var matcher = new LibraryMatcher(items);
        Assert.Equal(10, matcher.Find("Болт M8x30 ГОСТ 7798-70").Count);
        Assert.Equal(3, matcher.Find("Болт M8x30 ГОСТ 7798-70", max: 3).Count);
        Assert.Empty(matcher.Find("Болт M8x30 ГОСТ 7798-70", max: 0));
    }

    [Fact]
    public void SameKeyTwice_FirstIsExact_SecondNotDuplicated()
    {
        var first = Item("Болт M8 ГОСТ 7798-70");
        var second = Item("болт m8 гост 7798-70");
        var found = new LibraryMatcher([first, second]).Find("Болт M8 ГОСТ 7798-70");
        Assert.Same(first, found[0].Item);
        Assert.Equal(found.Count, found.Select(f => f.Item).Distinct().Count());
    }

    [Fact]
    public void LeadingZeros_CompareByValue()
    {
        // В «08» нет числа 1 из «8.01» — не похож; наоборот — похож (лишнее число 1).
        Assert.Empty(new LibraryMatcher([Item("Шайба 08 ГОСТ 11371-78")]).Find("Шайба 8.01 ГОСТ 11371-78"));
        var found = new LibraryMatcher([Item("Шайба 8.01 ГОСТ 11371-78")]).Find("Шайба 08 ГОСТ 11371-78");
        Assert.Single(found);
        Assert.Equal(1, found[0].Extra);
    }

    [Fact]
    public void SimilarSortedByExtraThenNaturally()
    {
        var a = Item("Болт M8-6gx30.88 ГОСТ 7798-70");
        var b = Item("Болт M8-6gx30.109 ГОСТ 7798-70");
        var c = Item("Болт M8-6gx30.109.40X ГОСТ 7798-70");
        var found = new LibraryMatcher([c, b, a]).Find("Болт M8x30 ГОСТ 7798-70");
        Assert.Equal([a, b, c], found.Select(f => f.Item));
    }

    [Fact]
    public void WithoutStandard_MatchesOnlyItemsWithoutStandard()
    {
        var withStandard = Item("Кнопка 22 ГОСТ 1234-80");
        var plain = Item("Кнопка 22 красная 1");
        var found = new LibraryMatcher([withStandard, plain]).Find("Кнопка 22");
        Assert.Equal([plain], found.Select(f => f.Item));
    }
}

/// <summary>Третий проход: правила замены.</summary>
public class DesignationRuleSetTheoryTests
{
    private static DesignationRule Rule(string pattern, string? replacement, bool regex = false, int order = 0, int id = 0, bool enabled = true) =>
        new() { Id = id, Pattern = pattern, Replacement = replacement!, IsRegex = regex, SortOrder = order, Enabled = enabled };

    [Theory]
    [InlineData("Болт_M8", "_", " ", false, "Болт M8")]
    [InlineData("БОЛТ_M8", "болт", "Болт", false, "БОЛТ_M8")] // обычный текст — с учётом регистра
    [InlineData("M8х20", @"(\d)х(\d)", "$1x$2", true, "M8x20")]
    [InlineData("M8  x  20", @"\s+", " ", true, "M8 x 20")]
    [InlineData("abc", "b", "", false, "ac")]
    [InlineData("a.c", ".", "-", false, "a-c")] // точка в обычном тексте — точка
    [InlineData("abc", ".", "-", true, "---")] // в регулярном — любой символ
    [InlineData("Болт", "$", "!", false, "Болт")]
    [InlineData("цена $5", "$5", "5$", false, "цена 5$")]
    public void Apply(string input, string pattern, string replacement, bool regex, string expected) =>
        Assert.Equal(expected, new DesignationRuleSet([Rule(pattern, replacement, regex)]).ApplyReplacements(input).Result);

    [Fact]
    public void NullReplacement_Deletes()
    {
        Assert.Equal("ac", new DesignationRuleSet([Rule("b", null)]).ApplyReplacements("abc").Result);
        Assert.Equal("ac", new DesignationRuleSet([Rule("b", null, regex: true)]).ApplyReplacements("abc").Result);
    }

    [Fact]
    public void SameSortOrder_ById()
    {
        var set = new DesignationRuleSet([Rule("b", "c", id: 2), Rule("a", "b", id: 1)]);
        Assert.Equal("c", set.ApplyReplacements("a").Result); // сначала a→b (Id 1), затем b→c
    }

    [Fact]
    public void DisabledInvalidRule_NotReportedAsInvalid()
    {
        var set = new DesignationRuleSet([Rule("(", "", regex: true, enabled: false)]);
        Assert.Empty(set.Invalid);
        Assert.False(set.HasReplacements);
    }

    [Fact]
    public void Empty_HasNoReplacements() => Assert.False(DesignationRuleSet.Empty.HasReplacements);

    [Fact]
    public void CatastrophicRegex_TimesOut_RuleSkipped_NextApplied()
    {
        var set = new DesignationRuleSet([Rule("(a+)+$", "x", regex: true, order: 0), Rule("b", "c", order: 1)]);
        var input = new string('a', 40) + "b";
        var (result, applied) = set.ApplyReplacements(input);
        Assert.Equal(new string('a', 40) + "c", result);
        Assert.Single(applied);
    }

    [Theory]
    [InlineData("Комментарий", "a", "b", "Комментарий")]
    [InlineData("  ", "a", "b", "«a» → «b»")]
    [InlineData(null, "a", "", "«a» → «»")]
    public void Describe(string? comment, string pattern, string replacement, string expected) =>
        Assert.Equal(expected, DesignationRuleSet.Describe(new DesignationRule { Pattern = pattern, Replacement = replacement, Comment = comment }));

    [Theory]
    [InlineData("[", true)]
    [InlineData("a{2", false)] // .NET допускает как обычный текст
    [InlineData("(?<x>a)", false)]
    [InlineData("*", true)]
    public void Validate_Regex(string pattern, bool error) =>
        Assert.Equal(error, DesignationRuleSet.Validate(new DesignationRule { Pattern = pattern, IsRegex = true }) is not null);
}

/// <summary>Третий проход: индекс путей библиотеки и подстановка ссылок.</summary>
public class PathIndexTheoryTests
{
    private static readonly Guid Part = Guid.NewGuid();
    private static readonly Guid Assembly = Guid.NewGuid();

    private static LibraryPathIndex Index() => new(
        [
            (Part, "Электротехника/Разъёмы/ABB/Разъём X1.ipt", "Разъём X1"),
            (Assembly, "Электротехника/Разъёмы/ABB/Разъём X2.iam", "Разъём X2"),
            (Guid.NewGuid(), "А/Гайка M8.ipt", "Гайка M8 ГОСТ 5915-70"),
            (Guid.NewGuid(), "Б/Гайка M8.ipt", "Гайка M8 ГОСТ 5927-70"),
        ], []);

    [Fact]
    public void ByDesignation_OnlySameExtension()
    {
        var index = Index();
        Assert.Equal("Электротехника/Разъёмы/ABB/Разъём X1.ipt", index.Find(@"D:\old\разъём  x1.ipt", missing: true));
        Assert.Null(index.Find(@"D:\old\Разъём X1.iam", missing: true)); // сборку деталью не подменяем
        Assert.Null(index.Find(@"D:\old\Разъём X2.ipt", missing: true));
    }

    [Fact]
    public void SameFileNameTwice_Ambiguous_NotGuessed() =>
        Assert.Null(Index().Find(@"D:\old\Гайка M8.ipt", missing: true));

    [Fact]
    public void NotMissing_NoGuessByName() =>
        Assert.Null(Index().Find(@"D:\old\Разъём X1.ipt", missing: false));

    [Fact]
    public void Empty_FindsNothing()
    {
        Assert.Null(LibraryPathIndex.Empty.Find("a.ipt", true));
        Assert.Equal(0, LibraryPathIndex.Empty.Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData("/")]
    [InlineData(@"\\")]
    [InlineData("C:")]
    public void Degenerate_References_NoThrow(string reference) => Assert.Null(Index().Find(reference, true));

    [Fact]
    public void CurrentPath_UnderOtherRoot_Found()
    {
        Assert.Equal("Электротехника/Разъёмы/ABB/Разъём X2.iam",
            Index().Find(@"E:\Другая библиотека\Электротехника\Разъёмы\ABB\Разъём X2.iam", missing: true));
    }

    private static LibraryLogEntry Log(string action, Guid? id, string? details, int minute) =>
        new() { User = "u", Action = action, ItemId = id, Details = details, At = new DateTime(2026, 1, 1, 0, minute, 0, DateTimeKind.Utc) };

    [Fact]
    public void MovesFromLog_SkipsBrokenAndIrrelevant_OrdersByTime()
    {
        var id = Guid.NewGuid();
        var moves = LibraryPathIndex.MovesFromLog(
        [
            Log("edit", id, """{"before":{"RelativePath":"b.ipt"},"after":{"RelativePath":"c.ipt"}}""", 2),
            Log("edit", id, """{"before":{"RelativePath":"a.ipt"},"after":{"RelativePath":"b.ipt"}}""", 1),
            Log("edit", id, """{"before":{"RelativePath":"same.ipt"},"after":{"RelativePath":"SAME.ipt"}}""", 3), // регистр — не переезд
            Log("edit", id, "не json", 4),
            Log("edit", id, """{"before":{"RelativePath":5}}""", 5),
            Log("edit", null, """{"before":{"RelativePath":"x.ipt"}}""", 6),
            Log("add", id, """{"RelativePath":"y.ipt"}""", 7),
            Log("merge", id, """{"RelativePath":"dup.ipt"}""", 8),
            Log("merge", id, """{"RelativePath":null}""", 9),
            Log("edit", id, null, 10),
        ]).ToList();

        Assert.Equal([("a.ipt", id), ("b.ipt", id), ("dup.ipt", id)], moves);
    }

    [Fact]
    public void Moves_ChainResolvesToCurrent_OldPathTakenByOther_Ignored()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var index = new LibraryPathIndex(
            [(a, "new/a.ipt", "A 1"), (b, "old/a.ipt", "B 1")],
            [("old/a.ipt", a), ("older/a.ipt", a)]);
        Assert.Equal("new/a.ipt", index.Find("older/a.ipt", false));
        Assert.Null(index.Find("other/q.ipt", false));
        Assert.Equal("old/a.ipt", index.Find(@"X:\lib\old\a.ipt", true)); // на старом пути теперь другой компонент
    }

    [Fact]
    public void Resolver_OtherProjectLibrary_NotTouched()
    {
        var index = new LibraryPathIndex([(Guid.NewGuid(), "Крепёж/a.ipt", "A 1")], [("old/a.ipt", Guid.NewGuid())]);
        var root = Path.Combine(Path.GetTempPath(), "lib");
        Assert.Null(LibraryReferenceResolver.Resolve(index, root, "Крепёж/a.ipt", "Чужая", "PdmLite", _ => true));
    }

    [Fact]
    public void Resolver_ExistingFile_NotReplaced()
    {
        var index = new LibraryPathIndex([(Guid.NewGuid(), "Крепёж/a.ipt", "A 1")], []);
        var root = Path.Combine(Path.GetTempPath(), "lib");
        var reference = Path.Combine(Path.GetTempPath(), "proj", "a.ipt");
        Assert.Null(LibraryReferenceResolver.Resolve(index, root, reference, null, "PdmLite", _ => true));
    }

    [Fact]
    public void Resolver_TargetFileGone_Null()
    {
        var index = new LibraryPathIndex([(Guid.NewGuid(), "Крепёж/a.ipt", "A 1")], []);
        var root = Path.Combine(Path.GetTempPath(), "lib");
        var reference = Path.Combine(Path.GetTempPath(), "old", "a.ipt");
        Assert.Null(LibraryReferenceResolver.Resolve(index, root, reference, null, "PdmLite", _ => false));
    }

    [Fact]
    public void Resolver_MissingRooted_ResolvedToFull()
    {
        var index = new LibraryPathIndex([(Guid.NewGuid(), "Крепёж/a.ipt", "A 1")], []);
        var root = Path.Combine(Path.GetTempPath(), "lib");
        var full = Path.Combine(root, "Крепёж", "a.ipt");
        var reference = Path.Combine(Path.GetTempPath(), "old", "a.ipt");
        Assert.Equal(full, LibraryReferenceResolver.Resolve(index, root, reference, null, "PdmLite", p => p == full));
    }

    [Theory]
    [InlineData("a.ipt", null, false)]
    [InlineData("a.ipt", "PdmLite", true)]
    [InlineData("a.ipt", "pdmlite", true)]
    [InlineData("a.ipt", "Чужая", false)]
    public void ShouldReload(string reference, string? library, bool expected) =>
        Assert.Equal(expected, LibraryReferenceResolver.ShouldReload(reference, library, "PdmLite"));

    [Fact]
    public void ShouldReload_Rooted() =>
        Assert.True(LibraryReferenceResolver.ShouldReload(Path.Combine(Path.GetTempPath(), "a.ipt"), null, "PdmLite"));
}

/// <summary>Третий проход: классификатор, размещение, свойства файла.</summary>
public sealed class PlacementTheoryTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("pdmlite-place3-").FullName;

    private static LibraryCatalog Catalog(params LibraryCategoryRule[] rules) => LibraryCatalog.Create(
    [
        new LibraryCategory { Id = 1, Name = "Крепёж" },
        new LibraryCategory { Id = 2, Name = "Болты", ParentId = 1 },
        new LibraryCategory { Id = 3, Name = "Электротехника" },
        new LibraryCategory { Id = 4, Name = "Разъёмы", ParentId = 3 },
    ], rules);

    private LibraryStorage Storage => new(Path.Combine(_root, "Library"));

    [Fact]
    public void Catalog_ParentCycle_DoesNotOverflow()
    {
        var catalog = LibraryCatalog.Create(
        [
            new LibraryCategory { Id = 1, Name = "А", ParentId = 2 },
            new LibraryCategory { Id = 2, Name = "Б", ParentId = 1 },
            new LibraryCategory { Id = 3, Name = "В", ParentId = 3 },
        ], []);
        Assert.Equal("Б/А", catalog.Get(1)!.Path);
        Assert.Equal("В", catalog.Get(3)!.Path);
    }

    [Fact]
    public void Catalog_UnknownParent_TreatedAsRoot() =>
        Assert.Equal("Сирота", LibraryCatalog.Create([new LibraryCategory { Id = 5, Name = "Сирота", ParentId = 99 }], []).Get(5)!.Path);

    [Fact]
    public void Catalog_DuplicateRules_LearnedWins()
    {
        var catalog = Catalog(
            new LibraryCategoryRule { Kind = LibraryRuleKind.FirstWord, Pattern = "болт", CategoryId = 4, IsLearned = true },
            new LibraryCategoryRule { Kind = LibraryRuleKind.FirstWord, Pattern = "болт", CategoryId = 2 });
        Assert.Equal(4, catalog.Suggest("Болт M8", null)!.Id);
    }

    [Fact]
    public void Catalog_RuleToDeletedCategory_SuggestsNothing() =>
        Assert.Null(Catalog(new LibraryCategoryRule { Kind = LibraryRuleKind.FirstWord, Pattern = "болт", CategoryId = 77 }).Suggest("Болт M8", null));

    [Fact]
    public void Catalog_FindByPath()
    {
        var catalog = Catalog();
        Assert.Equal(2, catalog.FindByPath("Крепёж/Болты")!.Id);
        Assert.Null(catalog.FindByPath("крепёж/болты")); // путь — точный
        Assert.Null(catalog.FindByPath(""));
        Assert.Null(catalog.Get(null));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Place_NoDesignation_Error(string? designation)
    {
        var place = LibraryRules.Place(Catalog(), Storage, designation, LibraryItemKind.Standard, null, 2, ".ipt");
        Assert.Contains(place.Issues, i => i.Severity == ImportSeverity.Error && i.Message.Contains("обозначение"));
        Assert.Null(place.RelativePath);
    }

    [Fact]
    public void Place_StandardWithoutStandard_Error_ButPathBuilt()
    {
        var place = LibraryRules.Place(Catalog(), Storage, "Болт M8", LibraryItemKind.Standard, null, 2, ".ipt");
        Assert.Contains(place.Issues, i => i.Severity == ImportSeverity.Error && i.Message.Contains("стандарт"));
    }

    [Fact]
    public void Place_ManualStandard_UsedAsFolder_AndInfo()
    {
        var place = LibraryRules.Place(Catalog(), Storage, "Кольцо A15 DIN 471", LibraryItemKind.Standard, null, 2, ".ipt", "  DIN   471  ");
        Assert.Equal("DIN 471", place.Standard);
        Assert.Equal("Крепёж/Болты/DIN 471/Кольцо A15 DIN 471.ipt", place.RelativePath);
        Assert.DoesNotContain(place.Issues, i => i.Severity == ImportSeverity.Info); // совпадает с распознанным
        var other = LibraryRules.Place(Catalog(), Storage, "Кольцо A15 DIN 471", LibraryItemKind.Standard, null, 2, ".ipt", "ГОСТ 13942-86");
        Assert.Contains(other.Issues, i => i.Severity == ImportSeverity.Info && i.Message.Contains("ГОСТ 13942-86"));
    }

    [Fact]
    public void Place_Purchased_IgnoresStandard_WarnsWithoutMaker()
    {
        var place = LibraryRules.Place(Catalog(), Storage, "Разъём X1 ГОСТ 1-80", LibraryItemKind.Purchased, "  ", 4, ".ipt", "ГОСТ 1-80");
        Assert.Null(place.Standard);
        Assert.Null(place.Manufacturer);
        Assert.Contains(LibraryStorage.NoManufacturerFolder, place.RelativePath);
        Assert.Contains(place.Issues, i => i.Severity == ImportSeverity.Warning);
    }

    [Fact]
    public void Place_MakerTrimmed()
    {
        var place = LibraryRules.Place(Catalog(), Storage, "Разъём X1", LibraryItemKind.Purchased, "  ABB ", 4, ".ipt");
        Assert.Equal("ABB", place.Manufacturer);
        Assert.Equal("Электротехника/Разъёмы/ABB/Разъём X1.ipt", place.RelativePath);
        Assert.Empty(place.Issues);
    }

    [Theory]
    [InlineData(null, "Не выбран")]
    [InlineData(99, "Не выбран")]
    [InlineData(1, "раздел; выберите группу")]
    public void Place_BadCategory_Error(int? category, string message)
    {
        var place = LibraryRules.Place(Catalog(), Storage, "Болт M8 ГОСТ 7798-70", LibraryItemKind.Standard, null, category, ".ipt");
        Assert.Contains(place.Issues, i => i.Severity == ImportSeverity.Error && i.Message.Contains(message));
        Assert.Null(place.RelativePath);
    }

    [Fact]
    public void Place_TooLongPath_Error()
    {
        var place = LibraryRules.Place(Catalog(), Storage, "Болт " + new string('x', 260) + " ГОСТ 7798-70", LibraryItemKind.Standard, null, 2, ".ipt");
        Assert.Contains(place.Issues, i => i.Message.Contains("Слишком длинный путь"));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("ГОСТ   7798-70", "ГОСТ 7798-70")]
    [InlineData(" DIN 471 ", "DIN 471")]
    public void NormalizeStandard(string? input, string? expected) => Assert.Equal(expected, LibraryRules.NormalizeStandard(input));

    [Theory]
    [InlineData(LibraryItemKind.Standard, "StandardProducts")]
    [InlineData(LibraryItemKind.Purchased, "OtherProducts")]
    public void SpecSectionFor(LibraryItemKind kind, string expected) => Assert.Equal(expected, LibraryProperties.SpecSectionFor(kind));

    [Theory]
    [InlineData("StandardProducts", LibraryItemKind.Standard)]
    [InlineData(" standardproducts ", LibraryItemKind.Standard)]
    [InlineData("OtherProducts", LibraryItemKind.Purchased)]
    [InlineData("OTHERPRODUCTS", LibraryItemKind.Purchased)]
    [InlineData("Details", null)]
    [InlineData("", null)]
    public void KindFromSpecSection(string value, LibraryItemKind? expected) =>
        Assert.Equal(expected, LibraryProperties.KindFromSpecSection(new Dictionary<string, string> { [LibraryProperties.SpecSection] = value }));

    [Fact]
    public void KindFromSpecSection_NoProperty_Null() => Assert.Null(LibraryProperties.KindFromSpecSection(new Dictionary<string, string>()));

    [Fact]
    public void For_WritesAllFields_IdOptional()
    {
        var id = Guid.NewGuid();
        var changes = LibraryProperties.For("Болт M8", LibraryItemKind.Standard, null, null, "Крепёж/Болты", id);
        Assert.Equal("Болт M8", changes.PartNumber);
        Assert.Equal("Болт M8", changes.Description);
        Assert.Equal("Болт M8", changes.Title);
        Assert.True(changes.RemoveContentCenterLink);
        Assert.Equal("", changes.Custom[LibraryProperties.Standard]);
        Assert.Equal("", changes.Custom[LibraryProperties.Manufacturer]);
        Assert.Equal(id.ToString(), changes.Custom[LibraryProperties.LibraryId]);
        Assert.False(LibraryProperties.For("a", LibraryItemKind.Purchased, null, "ABB", "x").Custom.ContainsKey(LibraryProperties.LibraryId));
    }

    public void Dispose() => Directory.Delete(_root, true);
}

/// <summary>Третий проход: проверка сборки.</summary>
public sealed class AssemblyCheckTheoryTests
{
    private static readonly string LibRoot = Path.Combine(Path.GetTempPath(), "pdmlite-check3-lib");
    private static readonly LibraryStorage Storage = new(LibRoot);

    private static LibraryItemSummary Item(string designation) =>
        new(Guid.NewGuid(), designation, LibraryItemKind.Standard, LibraryDocumentType.Part, null, null, 1, "Крепёж/Болты",
            $"Крепёж/Болты/{designation}.ipt", 1, DateTime.UtcNow, "test");

    private static AssemblyComponentInfo Component(string name, string? partNumber = null, bool cc = false, bool missing = false, string? folder = null) =>
        new(Path.Combine(folder ?? Path.Combine(Path.GetTempPath(), "proj"), name), partNumber, cc, 1, missing);

    [Fact]
    public void InLibrary_Counted_NotReported()
    {
        var result = AssemblyCheck.Run([Component("Болт.ipt", folder: Path.Combine(LibRoot, "Крепёж")), Component("Своя.ipt")], [], Storage);
        Assert.Equal(1, result.InLibrary);
        Assert.Equal(2, result.Total);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public void OwnPartMissing_StillReported()
    {
        var check = Assert.Single(AssemblyCheck.Run([Component("Своя.ipt", missing: true)], [], Storage).Findings);
        Assert.Equal(ComponentFinding.MissingFile, check.Finding);
        Assert.False(check.CanReplace);
        Assert.Contains("Замена ссылок", check.Message);
    }

    [Fact]
    public void CopyOfLibraryItem_ByFileNameWhenNoPartNumber()
    {
        var bolt = Item("Болт M8 ГОСТ 7798-70");
        var check = Assert.Single(AssemblyCheck.Run([Component("Болт M8 ГОСТ 7798-70.ipt", partNumber: "  ")], [bolt], Storage).Findings);
        Assert.Equal(ComponentFinding.CopyOfLibraryItem, check.Finding);
        Assert.True(check.IsExact);
        Assert.Same(bolt, check.Replacement);
    }

    [Fact]
    public void WindowsPath_FileNameTakenOnAnyPlatform()
    {
        var component = new AssemblyComponentInfo(@"C:\Проект\Болт M8 ГОСТ 7798-70.ipt", null, false, 1);
        var check = Assert.Single(AssemblyCheck.Run([component], [], Storage).Findings);
        Assert.Equal("Болт M8 ГОСТ 7798-70", check.Designation);
        Assert.Equal(ComponentFinding.StandardNotInLibrary, check.Finding);
    }

    [Fact]
    public void Findings_OrderedByKindThenNaturally()
    {
        var result = AssemblyCheck.Run(
        [
            Component("x1.ipt", "Болт M10 ГОСТ 7798-70"),
            Component("x2.ipt", "Болт M8 ГОСТ 7798-70"),
            Component("x3.ipt", "Шайба 8 ГОСТ 11371-78", cc: true),
            Component("x4.ipt", missing: true),
        ], [], Storage);
        Assert.Equal([ComponentFinding.MissingFile, ComponentFinding.ContentCenter, ComponentFinding.StandardNotInLibrary, ComponentFinding.StandardNotInLibrary],
            result.Findings.Select(f => f.Finding));
        Assert.Equal("Болт M8 ГОСТ 7798-70", result.Findings[2].Designation);
    }

    [Theory]
    [InlineData(ComponentFinding.ContentCenter, false, "нет")]
    [InlineData(ComponentFinding.StandardNotInLibrary, false, "Добавьте")]
    [InlineData(ComponentFinding.CopyOfLibraryItem, false, "Копия")]
    public void Message_WithoutCandidates(ComponentFinding finding, bool _, string text) =>
        Assert.Contains(text, new ComponentCheck(Component("a.ipt"), "a", finding, []).Message);

    [Fact]
    public void Empty_Assembly()
    {
        var result = AssemblyCheck.Run([], [Item("Болт M8 ГОСТ 7798-70")], Storage);
        Assert.Equal(0, result.Total);
        Assert.Empty(result.Findings);
    }
}

/// <summary>Третий проход: список подготовки и хранилище.</summary>
public sealed class ImportListAndStorageTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("pdmlite-list3-").FullName;

    private string Write(string name, string content)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Theory]
    [InlineData("")]
    [InlineData("не json")]
    [InlineData("[]")]
    [InlineData("""{"Version":1,"Items":[{"Include":true}]}""")] // нет обязательного Path
    [InlineData("""{"Version":1,"Items":[{"Path":"a","Kind":"Неизвестно"}]}""")]
    public void Load_Broken_InvalidData(string content) =>
        Assert.Throws<InvalidDataException>(() => ImportList.Load(Write("a.pdmlist", content)));

    [Fact]
    public void Load_Null_Empty() =>
        Assert.Contains("пуст", Assert.Throws<InvalidDataException>(() => ImportList.Load(Write("a.pdmlist", "null"))).Message);

    [Fact]
    public void Load_OtherVersion_Rejected() =>
        Assert.Contains("Версия", Assert.Throws<InvalidDataException>(() => ImportList.Load(Write("a.pdmlist", """{"Version":2}"""))).Message);

    [Fact]
    public void Load_NullItemsOrEntries_Tolerated()
    {
        Assert.Empty(ImportList.Load(Write("a.pdmlist", """{"Version":1,"Items":null}""")).Items);
        var list = ImportList.Load(Write("b.pdmlist", """{"Version":1,"Items":[null,{"Path":" "},{"Path":"x.ipt"}]}"""));
        Assert.Equal(["x.ipt"], list.Items.Select(i => i.Path));
    }

    [Fact]
    public void Load_DefaultsForMissingFields()
    {
        var entry = Assert.Single(ImportList.Load(Write("a.pdmlist", """{"Items":[{"Path":"x.ipt"}]}""")).Items);
        Assert.True(entry.Include);
        Assert.Equal("", entry.Designation);
        Assert.False(entry.CategoryChosen);
    }

    [Fact]
    public void Save_Overwrites_NoTempLeft()
    {
        var path = Path.Combine(_root, "a.pdmlist");
        new ImportList { Items = [new ImportListEntry { Path = "1.ipt" }] }.Save(path);
        new ImportList { Items = [new ImportListEntry { Path = "2.ipt", Kind = LibraryItemKind.Purchased }] }.Save(path);
        var loaded = ImportList.Load(path);
        Assert.Equal("2.ipt", Assert.Single(loaded.Items).Path);
        Assert.Equal(LibraryItemKind.Purchased, loaded.Items[0].Kind);
        Assert.Contains("\"Purchased\"", File.ReadAllText(path)); // перечисления — словами
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void Storage_Contains_PrefixOfOtherFolder_False()
    {
        var storage = new LibraryStorage(Path.Combine(_root, "Lib"));
        Assert.True(storage.Contains(Path.Combine(_root, "Lib", "a.ipt")));
        Assert.True(storage.Contains(Path.Combine(_root, "LIB", "a.ipt")));
        Assert.False(storage.Contains(Path.Combine(_root, "Lib2", "a.ipt")));
        Assert.False(storage.Contains(Path.Combine(_root, "Lib")));
        Assert.False(storage.Contains(Path.Combine(_root, "Lib", "..", "a.ipt")));
    }

    [Fact]
    public void Storage_RootTrailingSeparator_Trimmed() =>
        Assert.Equal(Path.Combine(_root, "Lib"), new LibraryStorage(Path.Combine(_root, "Lib") + Path.DirectorySeparatorChar).Root);

    [Fact]
    public void Storage_Delete_Missing_NoThrow_ReadOnly_Deleted()
    {
        LibraryStorage.Delete(Path.Combine(_root, "нет.ipt"));
        var file = Write("a.ipt", "x");
        LibraryStorage.MakeReadOnly(file);
        LibraryStorage.Delete(file);
        Assert.False(File.Exists(file));
    }

    [Fact]
    public void Storage_BackupRestore_KeepsContent_RestoredReadOnly()
    {
        var file = Write("a.ipt", "1");
        LibraryStorage.MakeReadOnly(file);
        var backup = LibraryStorage.Backup(file);
        try
        {
            Assert.False(File.GetAttributes(backup).HasFlag(FileAttributes.ReadOnly));
            LibraryStorage.ClearReadOnly(file);
            File.WriteAllText(file, "2");
            LibraryStorage.Restore(backup, file);
            Assert.Equal("1", File.ReadAllText(file));
            Assert.True(File.GetAttributes(file).HasFlag(FileAttributes.ReadOnly));

            var elsewhere = Path.Combine(_root, "new", "deep", "b.ipt");
            LibraryStorage.Restore(backup, elsewhere); // папки создаются
            Assert.Equal("1", File.ReadAllText(elsewhere));
        }
        finally
        {
            File.Delete(backup);
        }
    }

    [Fact]
    public void Storage_RemoveEmptyFolders_StopsAtRootAndAtNonEmpty()
    {
        var storage = new LibraryStorage(Path.Combine(_root, "Lib"));
        var deep = Path.Combine(storage.Root, "a", "b", "c");
        Directory.CreateDirectory(deep);
        File.WriteAllText(Path.Combine(storage.Root, "a", "keep.txt"), "x");

        storage.RemoveEmptyFolders(deep);

        Assert.False(Directory.Exists(Path.Combine(storage.Root, "a", "b")));
        Assert.True(Directory.Exists(Path.Combine(storage.Root, "a")));

        File.Delete(Path.Combine(storage.Root, "a", "keep.txt"));
        storage.RemoveEmptyFolders(Path.Combine(storage.Root, "a"));
        Assert.True(Directory.Exists(storage.Root)); // корень остаётся
        storage.RemoveEmptyFolders(Path.Combine(_root, "вне")); // вне библиотеки — ничего
    }

    [Fact]
    public async Task Storage_Hash_StableAndSized()
    {
        var file = Write("a.bin", "abc");
        var (hash, size) = await LibraryStorage.HashAsync(file);
        Assert.Equal("BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD", hash);
        Assert.Equal(3, size);
    }

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(_root, true);
    }
}

/// <summary>Третий проход: резервные копии — испорченные и опасные архивы, расписание.</summary>
public class BackupEdgeTests
{
    private static (LibraryImportService Import, LibraryBackupService Backup, string Folder) Create(TestDatabase db, TempFolders folders, int keep = 10, int interval = 7)
    {
        LibraryStorage Storage() => new(folders.Library);
        var backups = Path.Combine(folders.Root, "Backups");
        return (new LibraryImportService(db.Database, new FakePropertyStore(), Storage, NullLogger<LibraryImportService>.Instance),
            new LibraryBackupService(db.Database, Storage, () => new BackupOptions(backups, keep, interval), NullLogger<LibraryBackupService>.Instance),
            backups);
    }

    [Theory]
    [InlineData(BackupReason.Manual)]
    [InlineData(BackupReason.Scheduled)]
    [InlineData(BackupReason.BeforeMaintenance)]
    [InlineData(BackupReason.BeforeMerge)]
    [InlineData(BackupReason.BeforeRestore)]
    public void ReasonText_ForEveryReason(BackupReason reason) =>
        Assert.False(string.IsNullOrWhiteSpace(LibraryBackupService.ReasonText(reason)));

    [Fact]
    public void List_NoFolder_Empty_IntervalZero_NeverDue()
    {
        using var folders = new TempFolders();
        var service = new LibraryBackupService(null!, () => new LibraryStorage(folders.Library),
            () => new BackupOptions(Path.Combine(folders.Root, "нет"), 3, 0), NullLogger<LibraryBackupService>.Instance);
        Assert.Empty(service.List());
        Assert.False(service.IsScheduledDue());
    }

    [Fact]
    public void List_SkipsJunkZipAndNonZip()
    {
        using var folders = new TempFolders();
        var backups = Path.Combine(folders.Root, "Backups");
        Directory.CreateDirectory(backups);
        File.WriteAllText(Path.Combine(backups, "мусор.zip"), "не архив");
        using (var zip = ZipFile.Open(Path.Combine(backups, "чужой.zip"), ZipArchiveMode.Create))
            zip.CreateEntry("readme.txt");
        using (var zip = ZipFile.Open(Path.Combine(backups, "кривой.zip"), ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(zip.CreateEntry("manifest.json").Open());
            writer.Write("{не json");
        }
        var service = new LibraryBackupService(null!, () => new LibraryStorage(folders.Library),
            () => new BackupOptions(backups, 3, 7), NullLogger<LibraryBackupService>.Instance);

        Assert.Empty(service.List());
        Assert.True(service.IsScheduledDue()); // копий нет — пора
        Assert.Null(service.Read(Path.Combine(backups, "нет.zip")));
    }

    [PostgresFact]
    public async Task Restore_ZipSlipEntry_Rejected_LibraryUntouched_NoStagingLeft()
    {
        await using var db = await TestDatabase.CreateAsync();
        using var folders = new TempFolders();
        var (import, backup, _) = Create(db, folders);
        var file = folders.AddFile("Болт M8x20 ГОСТ 7798-70.ipt", "bolt");
        await import.CommitAsync(await import.AnalyzeAsync([file]));
        var created = await backup.CreateAsync(BackupReason.Manual);
        using (var zip = ZipFile.Open(created.Path, ZipArchiveMode.Update))
        {
            using var writer = new StreamWriter(zip.CreateEntry("files/../../evil.txt").Open());
            writer.Write("зло");
        }
        var before = Directory.EnumerateFiles(folders.Library, "*", SearchOption.AllDirectories).Order().ToList();

        await Assert.ThrowsAsync<InvalidDataException>(() => backup.RestoreAsync(created.Path));

        Assert.Equal(before, Directory.EnumerateFiles(folders.Library, "*", SearchOption.AllDirectories).Order().ToList());
        Assert.False(File.Exists(Path.Combine(folders.Root, "evil.txt")));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(folders.Root)!, "evil.txt")));
        Assert.Empty(Directory.EnumerateDirectories(folders.Root, "Library.restore-*"));
    }

    [PostgresFact]
    public async Task Plan_ChangedFile_ListedAsDamaged_RestoreRefused()
    {
        await using var db = await TestDatabase.CreateAsync();
        using var folders = new TempFolders();
        var (import, backup, _) = Create(db, folders);
        await import.CommitAsync(await import.AnalyzeAsync([folders.AddFile("Болт M8x20 ГОСТ 7798-70.ipt", "bolt")]));
        var created = await backup.CreateAsync(BackupReason.Manual);
        using (var zip = ZipFile.Open(created.Path, ZipArchiveMode.Update))
        {
            var entry = zip.Entries.Single(e => e.FullName.StartsWith("files/") && e.Name.Length > 0);
            var name = entry.FullName;
            entry.Delete();
            using var writer = new StreamWriter(zip.CreateEntry(name).Open());
            writer.Write("подменён");
        }

        var plan = await backup.PlanRestoreAsync(created.Path);

        Assert.True(plan.IsDamaged);
        Assert.Contains(plan.MissingFiles, m => m.Contains("контрольная сумма"));
        await Assert.ThrowsAsync<InvalidDataException>(() => backup.RestoreAsync(created.Path));
    }

    [PostgresFact]
    public async Task Prune_KeepZeroMeansOne()
    {
        await using var db = await TestDatabase.CreateAsync();
        using var folders = new TempFolders();
        var (_, backup, _) = Create(db, folders, keep: 0);
        await backup.CreateAsync(BackupReason.Scheduled);
        await Task.Delay(1100); // имя копии — с точностью до секунды
        await backup.CreateAsync(BackupReason.Scheduled);
        Assert.Single(backup.List());
        Assert.False(backup.IsScheduledDue());
    }
}
