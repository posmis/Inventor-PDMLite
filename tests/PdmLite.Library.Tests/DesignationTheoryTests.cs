using PdmLite.Library.Designations;
using PdmLite.Library.Items;
using PdmLite.Library.Storage;

namespace PdmLite.Library.Tests;

/// <summary>Обозначения, стандарты и имена файлов — много вариантов записи (третий разбор).</summary>
public class DesignationTheoryTests
{
    [Theory]
    [InlineData("Болт М10-6gх25 ГОСТ 7798-70", "Болт M10-6gx25 ГОСТ 7798-70")]
    [InlineData("  Гайка   М8  ", "Гайка M8")]
    [InlineData("Шайба А.10.01", "Шайба A.10.01")]
    [InlineData("Кольцо 15×1", "Кольцо 15x1")]
    [InlineData("Винт М6–20", "Винт M6-20")]
    [InlineData("Винт М6—20", "Винт M6-20")]
    [InlineData("Подшипник 180204 ГОСТ 8882-75", "Подшипник 180204 ГОСТ 8882-75")]
    [InlineData("Шайба 8 ГОСТ 11371-78", "Шайба 8 ГОСТ 11371-78")]
    [InlineData("Винт ГОСТ Р ИСО 4762-2012 M6x20", "Винт ГОСТ Р ИСО 4762-2012 M6x20")]
    [InlineData("Гайка M8-6H.10 ОСТ 26-04", "Гайка M8-6H.10 ОСТ 26-04")]
    [InlineData("Болт\tM8", "Болт M8")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    public void ToCanonical_Cases(string input, string expected) =>
        Assert.Equal(expected, DesignationNormalizer.ToCanonical(input));

    [Theory]
    [InlineData("Болт M8-6gx20", "болт м8-6gх20", true)]
    [InlineData("Болт M8-6gx20", "Болт  M8 - 6g x 20", true)]
    [InlineData("Болт M8×20", "Болт M8x20", true)]
    [InlineData("Шайба 8,4", "Шайба 8.4", true)]
    [InlineData("Винт M6–20", "Винт M6-20", true)]
    [InlineData("Болт M8x20", "Болт M8x25", false)]
    [InlineData("Болт M8x20.109", "Болт M8x20.88", false)]
    [InlineData("Гайка M8", "Гайка M10", false)]
    [InlineData("Шайба A.10", "Шайба А.10", true)] // латинская и кириллическая «А»
    public void ToKey_SameOrDifferent(string a, string b, bool same) =>
        Assert.Equal(same, DesignationNormalizer.ToKey(a) == DesignationNormalizer.ToKey(b));

    [Theory]
    [InlineData("Болт M10 ГОСТ 7798-70", "болт")]
    [InlineData("Гайка шестигранная - M3", "гайка шестигранная")]
    [InlineData("Стопорное кольцо DIN 471-15x1", "стопорное кольцо")]
    [InlineData("Кольцо стопорное ГОСТ 13942-86", "кольцо стопорное")]
    [InlineData("Ёлочка зелёная 5", "елочка зеленая")]
    [InlineData("АБВГ.301.001", "")]
    [InlineData("M8x20", "")]
    public void LeadingWords_Cases(string designation, string expected) =>
        Assert.Equal(expected, string.Join(' ', DesignationNormalizer.LeadingWords(designation)));

    [Theory]
    [InlineData("Болт ГОСТ 7798-70 M8-6g×30(S13)", "ГОСТ 7798-70")]
    [InlineData("Винт A2.M2-6gx5.48.016 ГОСТ 17473-80", "ГОСТ 17473-80")]
    [InlineData("Шпонка 8x7x40 ГОСТ 23360-78", "ГОСТ 23360-78")]
    [InlineData("Подшипник 6204 ГОСТ 8338-75", "ГОСТ 8338-75")]
    [InlineData("Винт DIN 912 M6x20", "DIN 912")]
    [InlineData("Винт ISO 7380-1 M4x10", "ISO 7380-1")]
    [InlineData("Шайба DIN EN ISO 7089-8", "DIN EN ISO 7089-8")]
    [InlineData("Кабель ТУ 16-505.437-82", "ТУ 16-505.437-82")]
    [InlineData("Изделие СТО 123-2020", "СТО 123-2020")]
    [InlineData("Гайка M8 IEC 60204", "IEC 60204")]
    [InlineData("Нет стандарта 123", null)]
    [InlineData("ГОСТОВСКИЙ болт", null)]
    [InlineData("", null)]
    public void StandardParser_Cases(string designation, string? expected) =>
        Assert.Equal(expected, StandardParser.Find(designation));

    [Theory]
    [InlineData("Болт M8", "Болт M8")]
    [InlineData("Втулка 1/2\"", "Втулка 1-2-")]
    [InlineData("Крышка: верх?", "Крышка- верх-")]
    [InlineData("Корпус.", "Корпус")]
    [InlineData("  Вал  ", "Вал")]
    [InlineData("...", "_")]
    [InlineData("..", "_")]
    [InlineData("   ", "_")]
    [InlineData("CON", "CON_")]
    [InlineData("nul", "nul_")]
    [InlineData("COM1.ipt", "COM1_.ipt")]
    [InlineData("Console", "Console")]
    [InlineData("a\tb", "a-b")]
    public void SafeName_Cases(string input, string expected) =>
        Assert.Equal(expected, LibraryStorage.SafeName(input));

    [Theory]
    [InlineData(new[] { "Крепёж", "Болты" }, "ГОСТ 7798-70", "Болт M8", ".IPT", "Крепёж/Болты/ГОСТ 7798-70/Болт M8.ipt")]
    [InlineData(new[] { "Электротехника", "Разъёмы" }, null, "Разъём X1", ".iam", "Электротехника/Разъёмы/Разъём X1.iam")]
    [InlineData(new[] { "Прочее" }, "...", "Деталь", ".ipt", "Прочее/_/Деталь.ipt")] // производитель «...» — без «//»
    [InlineData(new[] { "Прочее" }, "A/B", "Деталь 1/2", ".ipt", "Прочее/A-B/Деталь 1-2.ipt")] // «/» не создаёт папок
    [InlineData(new[] { "Прочее" }, "..", "..", ".ipt", "Прочее/_/_.ipt")] // «..» — не выход из папки
    public void BuildRelativePath_Cases(string[] category, string? subfolder, string designation, string extension, string expected) =>
        Assert.Equal(expected, LibraryStorage.BuildRelativePath(category, subfolder, designation, extension));

    [Theory]
    [InlineData("Болт M8-6gx20.109", "болт м8-6gх20.109", true)]
    [InlineData("Болт M8-6gx20.109", "Болт M8-6g x 20 .109", true)]
    [InlineData("Болт M8-6gx20.109", "Болт M8-6gx20-.109", true)]
    [InlineData("Шайба A.1,6.01", "Шайба A.16.01", false)]
    [InlineData("Шайба A.1,6.01", "Шайба A.1.6.01", true)]
    [InlineData("Болт M8-6gx20.109", "Болт M8-6gx2010.9", false)]
    [InlineData("Гайка DIN 6334 M8-10", "Гайка DIN 6334 M8-12", false)]
    [InlineData("Винт (S10)", "Винт S10", true)]
    [InlineData("", "", true)]
    public void LooseKey_Cases(string a, string b, bool same) =>
        Assert.Equal(same, LibraryItemService.LooseKey(a) == LibraryItemService.LooseKey(b));

    [Theory]
    [InlineData(1.0, 2.0, 1.0, 2.0, true)]
    [InlineData(1.0, 2.0, 1.00005, 2.00005, true)] // в пределах 0,01 %
    [InlineData(1.0, 2.0, 1.001, 2.0, false)]
    [InlineData(1.0, 2.0, 1.0, 2.001, false)]
    [InlineData(-1.0, 2.0, -1.0, 2.0, false)] // отрицательное — не прочитано
    [InlineData(1000000.0, 5.0, 1000050.0, 5.0, true)]
    public void Geometry_Same(double v1, double a1, double v2, double a2, bool expected) =>
        Assert.Equal(expected, LibraryGeometry.Same(v1, a1, v2, a2));

    [Fact]
    public void Geometry_NullIsUnknown()
    {
        Assert.False(LibraryGeometry.Known(null, 1));
        Assert.False(LibraryGeometry.Same(null, null, null, null));
        Assert.True(LibraryGeometry.Known(0.1, 0.1));
    }
}
