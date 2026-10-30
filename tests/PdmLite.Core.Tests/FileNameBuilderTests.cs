using PdmLite.Core.Naming;

namespace PdmLite.Core.Tests;

public class FileNameBuilderTests
{
    [Theory]
    [InlineData("АБВГ.301.001", "Рама сварная", null, ".iam", "АБВГ.301.001_Рама-сварная.iam")]
    [InlineData("АБВГ.301.001", "Рама сварная", "СБ", "dwg", "АБВГ.301.001СБ_Рама-сварная.dwg")]
    [InlineData("АБВГ.301.001", "Рама   сварная ", "МЭ", ".DWG", "АБВГ.301.001МЭ_Рама-сварная.dwg")]
    [InlineData("АБВГ.301.001", "", null, ".ipt", "АБВГ.301.001.ipt")]
    [InlineData("АБВГ.301.001", "Кронштейн-левый", null, ".ipt", "АБВГ.301.001_Кронштейн-левый.ipt")]
    public void Build_FollowsNamingRule(string designation, string name, string? code, string ext, string expected) =>
        Assert.Equal(expected, FileNameBuilder.Build(designation, name, code, ext));

    [Theory]
    [InlineData("Болт \"М10\"", "Болт-М10")]
    [InlineData("Втулка 1/2", "Втулка-1-2")]
    [InlineData("Крышка: верх?", "Крышка-верх")]
    [InlineData("Корпус.", "Корпус")]
    [InlineData("  - Вал -  ", "Вал")]
    public void Normalize_RemovesForbiddenCharacters(string input, string expected) =>
        Assert.Equal(expected, FileNameBuilder.Normalize(input));

    [Fact]
    public void Build_EmptyDesignation_Throws() =>
        Assert.Throws<ArgumentException>(() => FileNameBuilder.Build("  ", "Рама", null, ".iam"));
}
