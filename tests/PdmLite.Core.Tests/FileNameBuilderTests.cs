using PdmLite.Core.Naming;

namespace PdmLite.Core.Tests;

/// <summary>Правило имени файла: «{Обозначение}{Код} {Наименование}.расширение».</summary>
public class FileNameBuilderTests
{
    [Theory]
    [InlineData("АБВГ.00.00.000", "Жгут", "СБ", ".iam", "АБВГ.00.00.000СБ Жгут.iam")]
    [InlineData("АБВГ.00.00.000", "Жгут", "СБ", "dwg", "АБВГ.00.00.000СБ Жгут.dwg")]
    [InlineData("АБВГ.00.00.000", "Жгут", "МЭ", ".DWG", "АБВГ.00.00.000МЭ Жгут.dwg")]
    [InlineData("АБВГ.301.001", "Рама   сварная ", null, ".iam", "АБВГ.301.001 Рама сварная.iam")]
    [InlineData("АБВГ.301.001", "", null, ".ipt", "АБВГ.301.001.ipt")]
    [InlineData("АБВГ.301.001", "Кронштейн-левый", null, ".ipt", "АБВГ.301.001 Кронштейн-левый.ipt")]
    public void Build_FollowsNamingRule(string designation, string name, string? code, string ext, string expected) =>
        Assert.Equal(expected, FileNameBuilder.Build(designation, name, code, ext));

    [Theory]
    [InlineData("Болт \"М10\"", "Болт М10")]
    [InlineData("Втулка 1/2", "Втулка 1-2")]
    [InlineData("Крышка: верх?", "Крышка верх")]
    [InlineData("Корпус.", "Корпус")]
    [InlineData("  - Вал -  ", "Вал")]
    public void Normalize_RemovesForbiddenCharacters(string input, string expected) =>
        Assert.Equal(expected, FileNameBuilder.Normalize(input));

    [Fact]
    public void Build_EmptyDesignation_Throws() =>
        Assert.Throws<ArgumentException>(() => FileNameBuilder.Build("  ", "Рама", null, ".iam"));
}

public class FileNameParseTests
{
    [Theory]
    [InlineData("АБВГ.00.00.000СБ Жгут", "АБВГ.00.00.000", "СБ", "Жгут")]
    [InlineData("АБВГ.00.00.000МЭ Жгут", "АБВГ.00.00.000", "МЭ", "Жгут")]
    [InlineData("АБВГ.00.00.000 Жгут питания", "АБВГ.00.00.000", "", "Жгут питания")]
    [InlineData("АБВГ.00.00.000", "АБВГ.00.00.000", "", "")]
    [InlineData("АБВГ.00.00.000СБ", "АБВГ.00.00.000", "СБ", "")]
    [InlineData("АБВГ.00.00.000Э3 Схема", "АБВГ.00.00.000", "Э3", "Схема")]
    [InlineData("АБВГ.00.00.000ПЭ3 Перечень", "АБВГ.00.00.000", "ПЭ3", "Перечень")]
    [InlineData("АБВГ.00.00.000-01СБ Жгут", "АБВГ.00.00.000-01", "СБ", "Жгут")]
    [InlineData("АБВГ.301.001  Рама   сварная ", "АБВГ.301.001", "", "Рама сварная")]
    [InlineData("10СБ Узел", "10", "СБ", "Узел")]
    [InlineData("M8x20", "M8x20", "", "")]
    public void TryParse(string stem, string designation, string code, string name) =>
        Assert.Equal(new DocumentName(designation, code, name), FileNameBuilder.TryParse(stem));

    [Theory]
    [InlineData("Кронштейн")]
    [InlineData("Кронштейн левый 2")]
    [InlineData("")]
    [InlineData("   ")]
    public void TryParse_NotByRule_Null(string stem) => Assert.Null(FileNameBuilder.TryParse(stem));

    [Theory]
    [InlineData("АБВГ.00.00.000", "СБ", "Жгут", ".iam")]
    [InlineData("АБВГ.00.00.000-01", "МЭ", "Жгут питания", ".dwg")]
    [InlineData("АБВГ.00.00.000", "", "Рама", ".ipt")]
    public void Compose_ThenParse_RoundTrip(string designation, string code, string name, string ext)
    {
        var file = FileNameBuilder.Compose(designation, code, name, ext);
        Assert.Equal(new DocumentName(designation, code, name), FileNameBuilder.TryParse(Path.GetFileNameWithoutExtension(file)));
    }

    [Theory]
    [InlineData(null, "СБ", "Кронштейн", ".ipt", "Кронштейн.ipt")] // без обозначения код не пишется
    [InlineData("АБВГ.1", null, null, ".ipt", "АБВГ.1.ipt")]
    [InlineData("АБВГ.1", " С Б ", "Узел", "IAM", "АБВГ.1СБ Узел.IAM")] // расширение — как есть
    [InlineData("АБВГ.1", "", "Узел", "", "АБВГ.1 Узел")]
    public void Compose(string? designation, string? code, string? name, string ext, string expected) =>
        Assert.Equal(expected, FileNameBuilder.Compose(designation, code, name, ext));

    [Fact]
    public void Compose_Nothing_Throws() => Assert.Throws<ArgumentException>(() => FileNameBuilder.Compose(" ", "СБ", "**", ".ipt"));
}
