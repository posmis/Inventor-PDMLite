using PdmLite.Index;
using PdmLite.Modules.References;

namespace PdmLite.Library.Tests;

public class ProjectHealthPresentationTests
{
    private const string Root = @"C:\Проекты\Изделие";
    private const string Path = @"C:\Проекты\Изделие\Детали\Крепёж\Очень длинное обозначение Болт М12.ipt";

    [Fact]
    public void LongPath_IsSplitIntoFileAndRelativeFolder_OriginalIsPreserved()
    {
        var row = Row("Нет наименования", "Не заполнено свойство Title.");
        Assert.Equal("Очень длинное обозначение Болт М12.ipt", row.Name);
        Assert.Equal("Детали/Крепёж", row.Folder);
        Assert.Equal(Path, row.Path);
        Assert.DoesNotContain(@"C:\", row.Preview);
        Assert.Equal("Замечание", row.Level);
    }

    [Theory]
    [InlineData(@"C:\Проекты\Изделие\a.ipt", "Корень проекта")]
    [InlineData("/workspace/project/sub/a.ipt", "sub")]
    public void Folder_IsReadableOnBothPlatforms(string path, string expected)
    {
        var root = path.StartsWith('/') ? "/workspace/project" : Root;
        Assert.Equal(expected, new ProjectHealthRow(new("Ошибка", "Не прочитан", path, "message"), root).Folder);
    }

    [Fact]
    public void VeryLongNames_PreserveBeginningAndExtension_InCompactLabel()
    {
        var name = "АБВГ.01.02.000 " + new string('я', 160) + ".ipt";
        var row = new ProjectHealthRow(new("Ошибка", "Не прочитан", Root + "\\" + name, "message"), Root);
        Assert.Equal(name, row.Name);
        Assert.Equal(64, row.DisplayName.Length);
        Assert.StartsWith("АБВГ.01.02.000", row.DisplayName);
        Assert.EndsWith(".ipt", row.DisplayName);
        Assert.Contains("…", row.DisplayName);
    }

    [Fact]
    public void ExternalFolder_IsNotMistakenForProjectSubfolder()
    {
        var row = new ProjectHealthRow(new("Ошибка", "Не прочитан", @"C:\Проекты\Изделие-архив\a.ipt", "message"), Root);
        Assert.Equal("C:/Проекты/Изделие-архив", row.Folder);
    }

    [Fact]
    public void LinkPreviews_HideDirectoryNoise_ButKeepFullDetails()
    {
        var source = @"C:\old\library\Болт.ipt";
        var found = @"D:\PdmLite\Library\Крепёж\Болт.ipt";
        var missing = Row("Ненайденная ссылка", source);
        var resolved = Row("Ссылка по другому пути", source + " → " + found);
        Assert.Equal("Не найден: Болт.ipt", missing.Preview);
        Assert.Equal("Найден: Болт.ipt", resolved.Preview);
        Assert.Equal(source + " → " + found, resolved.Details);
        Assert.True(resolved.Matches(@"D:\PdmLite\Library", null, null));
    }

    [Fact]
    public void DuplicatePreview_ShowsCount_InsteadOfSeveralLongPaths()
    {
        var row = Row("Повтор обозначения", @"C:\a\one.ipt" + "\r\n" + @"C:\b\two.ipt");
        Assert.Equal("Совпадение с другими документами: 2", row.Preview);
        Assert.Contains("two.ipt", row.Details);
    }

    [Fact]
    public void Search_UsesFullHiddenText_CombinesWithSeverityAndCategory()
    {
        var row = Row("Нет наименования", "Не заполнено свойство Title.");
        Assert.True(row.Matches("  крепёж  ", "Предупреждение", "Нет наименования"));
        Assert.True(row.Matches("TITLE", null, null));
        Assert.True(row.Matches("  ", null, null));
        Assert.False(row.Matches("title", "Ошибка", null));
        Assert.False(row.Matches("title", null, "Повтор обозначения"));
        Assert.False(row.Matches("нет такого документа", null, null));
    }

    private static ProjectHealthRow Row(string category, string details) =>
        new(new ProjectHealthIssue("Предупреждение", category, Path, details), Root);
}
