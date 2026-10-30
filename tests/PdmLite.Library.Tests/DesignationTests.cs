using PdmLite.Library.Designations;

namespace PdmLite.Library.Tests;

public class DesignationTests
{
    [Theory]
    // Кириллические М и х в кодах → латиница
    [InlineData("Болт М10-6gх25.109 ГОСТ 7798-70", "Болт M10-6gx25.109 ГОСТ 7798-70")]
    [InlineData("Болт M14-6g×140 ГОСТ 7798-70", "Болт M14-6gx140 ГОСТ 7798-70")]
    [InlineData("Шайба А.10.01.08кп.016 ГОСТ 11371-78", "Шайба A.10.01.08кп.016 ГОСТ 11371-78")]
    [InlineData("Шайба А 8.01.08кп.016 ГОСТ 6958-78", "Шайба A 8.01.08кп.016 ГОСТ 6958-78")]
    [InlineData("Винт ГОСТ Р ИСО 4017 -М5 х 12-8.8-A2U", "Винт ГОСТ Р ИСО 4017 -M5 x 12-8.8-A2U")]
    // Марки материалов, слова и стандарты не трогаются
    [InlineData("Ось 6-10 h11x30.Ст3сп ГОСТ 9650-80", "Ось 6-10 h11x30.Ст3сп ГОСТ 9650-80")]
    [InlineData("Шайба 10 65Г 019 ГОСТ 6402-70", "Шайба 10 65Г 019 ГОСТ 6402-70")]
    [InlineData("Подшипник 2880086 ГОСТ Р 71547-2024", "Подшипник 2880086 ГОСТ Р 71547-2024")]
    [InlineData("Пробка М12x1.25 исп.3 ОСТ 26-04-2000-77", "Пробка M12x1.25 исп.3 ОСТ 26-04-2000-77")]
    [InlineData("  Гайка   М8  DIN 6334 ", "Гайка M8 DIN 6334")]
    public void ToCanonical_UsesLatinInCodesOnly(string input, string expected) =>
        Assert.Equal(expected, DesignationNormalizer.ToCanonical(input));

    [Fact]
    public void ToKey_IgnoresLookalikesSpacesCaseAndDecimalSeparator()
    {
        var a = DesignationNormalizer.ToKey("Винт ГОСТ ISO 7380-1 -М6 x 10 -10.9 -A2U");
        var b = DesignationNormalizer.ToKey("винт гост ISO 7380-1-M6х10-10,9-А2U");
        Assert.Equal(a, b);
        Assert.NotEqual(DesignationNormalizer.ToKey("Болт M5-6gx20.109"), DesignationNormalizer.ToKey("Болт M5-6gx20.109.016"));
    }

    [Theory]
    [InlineData("Болт M10-6gx25.109 ГОСТ 7798-70", "ГОСТ 7798-70")]
    [InlineData("Винт с полукруглой головкой ГОСТ ISO 7380-1-M3x10-010.9-A2U", "ГОСТ ISO 7380-1")]
    [InlineData("Гайка шестигранная ГОСТ ISO 7040-M6-10-A2U", "ГОСТ ISO 7040")]
    [InlineData("Винт ГОСТ Р ИСО 10642 -М3 x 10 -8.8", "ГОСТ Р ИСО 10642")]
    [InlineData("Подшипник 2880086 ГОСТ Р 71547-2024", "ГОСТ Р 71547-2024")]
    [InlineData("Гайка самоконтрящаяся DIN 985 - M6 - 10", "DIN 985")]
    [InlineData("Пробка M12x1.25 исп.3 ОСТ 26-04-2000-77", "ОСТ 26-04-2000-77")]
    [InlineData("Стопорное кольцо DIN 471-15x1", "DIN 471")]
    [InlineData("Шайба DIN 6798-8,4", "DIN 6798")]
    [InlineData("Болт M12-6gx60.58 (S18) ГОСТ 7798-70", "ГОСТ 7798-70")]
    [InlineData("Кольцо ГОСТ 13942-86-А.40", "ГОСТ 13942-86")]
    [InlineData("Цепь привода левая 428 - 15 - 25", null)]
    [InlineData("Кабельный ввод SKINTOP MS-M 20x1,5 Lapp", null)]
    public void StandardParser_FindsStandard(string designation, string? expected) =>
        Assert.Equal(expected, StandardParser.Find(designation));

    [Theory]
    [InlineData("Болт M10-6gx25.109 ГОСТ 7798-70", "болт")]
    [InlineData("Гайка шестигранная низкая самостопорящаяся - M3 - 05 - A2U ГОСТ ISO 10511", "гайка шестигранная низкая самостопорящаяся")]
    [InlineData("Стопорное кольцо DIN 471 - 15 x 1", "стопорное кольцо")]
    [InlineData("Кабельный ввод SKINTOP MS-M 20x1,5 Lapp", "кабельный ввод")]
    public void LeadingWords_StopAtFirstCode(string designation, string expected) =>
        Assert.Equal(expected, string.Join(' ', DesignationNormalizer.LeadingWords(designation)));

    [Theory]
    [InlineData("Болт М10-6gх25.109 ГОСТ 7798-70")]
    [InlineData("Шайба А.10.01.08кп.016 ГОСТ 11371-78")]
    [InlineData("Ось 6-10 h11x30.Ст3сп ГОСТ 9650-80")]
    [InlineData("Винт ГОСТ Р ИСО 4017 -М5 х 12-8.8-A2U")]
    public void ToCanonical_IsIdempotent(string input)
    {
        var once = DesignationNormalizer.ToCanonical(input);
        Assert.Equal(once, DesignationNormalizer.ToCanonical(once));
    }

    [Theory]
    [InlineData("Гвоздь К 2,5x50 ГОСТ 4028-63", "Гвоздь K 2,5x50 ГОСТ 4028-63")]
    [InlineData("Рым-болт М12.019 ГОСТ 4751-73", "Рым-болт M12.019 ГОСТ 4751-73")]
    [InlineData("Гайка М5-6Н.10.016 (S8) ГОСТ 5927-70", "Гайка M5-6H.10.016 (S8) ГОСТ 5927-70")]
    [InlineData("Табличка 63x20 ГОСТ 12971-67", "Табличка 63x20 ГОСТ 12971-67")]
    [InlineData("Кабельный ввод SKINTOP MS-M 20x1,5 Lapp", "Кабельный ввод SKINTOP MS-M 20x1,5 Lapp")]
    public void ToCanonical_MoreCases(string input, string expected) =>
        Assert.Equal(expected, DesignationNormalizer.ToCanonical(input));

    [Fact]
    public void ToKey_TreatsDashesAndTimesSignAlike()
    {
        Assert.Equal(DesignationNormalizer.ToKey("Болт M14-6g×140"), DesignationNormalizer.ToKey("Болт M14–6gx140"));
        Assert.Equal(DesignationNormalizer.ToKey("ЗАКЛЕПКА 2 2X4"), DesignationNormalizer.ToKey("заклепка 2 2х4"));
    }

    [Theory]
    [InlineData("Рым-болт M12.019 ГОСТ 4751-73", "ГОСТ 4751-73")]
    [InlineData("Винт с потайной головкой ГОСТ Р ИСО 10642 -M4x35-8.8-A2U", "ГОСТ Р ИСО 10642")]
    [InlineData("Винт DIN EN ISO 4762 M6x20", "DIN EN ISO 4762")]
    [InlineData("Изделие ТУ 3-7-2010", "ТУ 3-7-2010")]
    [InlineData("ГОСТОВСКИЙ узел 12", null)]
    public void StandardParser_MoreCases(string designation, string? expected) =>
        Assert.Equal(expected, StandardParser.Find(designation));
}
