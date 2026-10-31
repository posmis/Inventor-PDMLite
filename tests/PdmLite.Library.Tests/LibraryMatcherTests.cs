using PdmLite.Core.Documents;
using PdmLite.Data.Library;
using PdmLite.Library.Checks;
using PdmLite.Library.Items;
using PdmLite.Library.Storage;

namespace PdmLite.Library.Tests;

/// <summary>Подбор замены: обозначения Content Center записаны иначе, чем в библиотеке.</summary>
public class LibraryMatcherTests
{
    private static LibraryItemSummary Item(string designation) =>
        new(Guid.NewGuid(), designation, LibraryItemKind.Standard, LibraryDocumentType.Part, null, null, 1, "Крепёж/Болты",
            $"Крепёж/Болты/{designation}.ipt", 1, DateTime.UtcNow, "test");

    private static readonly LibraryItemSummary Bolt109 = Item("Болт M8-6gx30.109(S13) ГОСТ 7798-70");
    private static readonly LibraryItemSummary Bolt88 = Item("Болт M8-6gx30.88(S13) ГОСТ 7798-70");
    private static readonly LibraryItemSummary BoltCoated = Item("Болт M8-6gx30.109.05(S13) ГОСТ 7798-70");
    private static readonly LibraryItemSummary Bolt35 = Item("Болт M8-6gx35.109(S13) ГОСТ 7798-70");
    private static readonly LibraryItemSummary Screw = Item("Винт M8-6gx30.109 ГОСТ 1491-80");
    private static readonly LibraryMatcher Matcher = new([Bolt109, Bolt88, BoltCoated, Bolt35, Screw]);

    [Fact]
    public void ContentCenterDesignation_FindsSameSize_ClosestFirst()
    {
        var found = Matcher.Find("Болт ГОСТ 7798-70 M8-6g×30(S13)");

        Assert.All(found, m => Assert.False(m.Exact));
        Assert.DoesNotContain(found, m => m.Item == Bolt35); // другая длина
        Assert.DoesNotContain(found, m => m.Item == Screw); // другое наименование и стандарт
        Assert.Equal([Bolt109, Bolt88, BoltCoated], found.Select(m => m.Item).OrderBy(i => i == BoltCoated).ThenBy(i => i == Bolt88));
        Assert.Equal(2, found.Last().Extra); // с покрытием — дальше всех
    }

    [Fact]
    public void ExactDesignation_IsFirst_AndMarked()
    {
        var found = Matcher.Find("болт м8-6gх30.109(S13) ГОСТ 7798-70");
        Assert.True(found[0].Exact);
        Assert.Same(Bolt109, found[0].Item);
        Assert.Contains(found, m => m.Item == BoltCoated && !m.Exact); // .109 + покрытие — тоже похож
    }

    [Theory]
    [InlineData("Болт ГОСТ 7798-70 M30-6gx8")] // числа в другом порядке
    [InlineData("Болт M8-6gx30 ГОСТ 7805-70")] // другой стандарт
    [InlineData("Шайба 8 ГОСТ 11371-78")]
    [InlineData("Болт")] // без чисел — не угадываем
    public void Unrelated_NotFound(string designation) => Assert.Empty(Matcher.Find(designation));

    [Fact]
    public void AssemblyCheck_OffersSimilar_ForContentCenterPart()
    {
        var storage = new LibraryStorage(Path.Combine(Path.GetTempPath(), "pdmlite-match-lib"));
        var cc = new AssemblyComponentInfo(Path.Combine(Path.GetTempPath(), "CC", "Болт.ipt"), "Болт ГОСТ 7798-70 M8-6g×30(S13)", true, 9);

        var check = Assert.Single(AssemblyCheck.Run([cc], [Bolt109, Bolt88, Bolt35], storage).Findings);

        Assert.True(check.CanReplace);
        Assert.False(check.IsExact);
        Assert.Contains("похожие", check.Message);
        Assert.Equal(2, check.Candidates.Count);
    }

    [Fact]
    public void AssemblyCheck_MissingFiles_AreListedFirst_WithLibraryCandidates()
    {
        var storage = new LibraryStorage(Path.Combine(Path.GetTempPath(), "pdmlite-match-lib"));
        var dir = Path.Combine(Path.GetTempPath(), "Проект");
        AssemblyComponentInfo Missing(string name) => new(Path.Combine(dir, name), null, false, 1, IsMissing: true);

        var result = AssemblyCheck.Run(
        [
            new AssemblyComponentInfo(Path.Combine(dir, "Болт ГОСТ 7798-70 M8-6g×30(S13).ipt"), "Болт ГОСТ 7798-70 M8-6g×30(S13)", true, 2),
            Missing("Болт ГОСТ 7798-70 M8-6g×35(S13).ipt"), // потерянный болт Content Center — по имени файла
            Missing("АБВГ.001_Пластина.ipt"),              // своя деталь — тоже в списке
        ], [Bolt109, Bolt35], storage);

        Assert.Equal(3, result.Findings.Count);
        var missing = result.Findings.Take(2).ToList();
        Assert.All(missing, f => Assert.Equal(ComponentFinding.MissingFile, f.Finding));
        var bolt = missing.Single(f => f.Designation.StartsWith("Болт"));
        Assert.Same(Bolt35, bolt.Replacement);
        Assert.Contains("не найден", bolt.Message);
        Assert.False(missing.Single(f => f.Designation.StartsWith("АБВГ")).CanReplace);
    }
}
