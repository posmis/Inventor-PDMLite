using PdmLite.Core.Scripts;

namespace PdmLite.Core.Tests;

public sealed class ScriptCatalogTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("pdmlite-scripts-").FullName;

    private string Add(string relative, string content = "x")
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void List_LaunchableFiles_LauncherHidesScript_TitlesFromManifest()
    {
        var folder = Path.Combine(_root, "Scripts");
        Add("Scripts/collect/Collect-Components.ps1");
        var cmd = Add("Scripts/collect/Collect-Components.cmd");
        Add("Scripts/collect/Collect-Components.json", """{"Title":"Сбор файлов по списку","Description":"С зависимостями сборок"}""");
        Add("Scripts/collect/README.md");
        var ps1 = Add("Scripts/Отчёт 10.ps1");
        var exe = Add("Scripts/Отчёт 2.exe");
        Add("Scripts/broken.json", "{не json");
        var broken = Add("Scripts/broken.bat");

        var scripts = ScriptCatalog.List(folder);

        Assert.Equal(["broken", "Отчёт 2", "Отчёт 10", "Сбор файлов по списку"], scripts.Select(s => s.Title)); // числа — по величине
        Assert.Equal([broken, exe, ps1, cmd], scripts.Select(s => s.Path));
        Assert.Equal("С зависимостями сборок", scripts[^1].Description);
        Assert.Empty(ScriptCatalog.List(Path.Combine(_root, "нет такой")));
    }

    [Fact]
    public void Seed_CopiesBundledOnlyIntoEmptyFolder()
    {
        var bundled = Path.Combine(_root, "bundled");
        Add("bundled/collect/Collect-Components.cmd", "новая");
        var folder = Path.Combine(_root, "Scripts");

        ScriptCatalog.Seed(folder, bundled);
        Assert.True(File.Exists(Path.Combine(folder, "collect", "Collect-Components.cmd")));

        // Пользователь что-то поменял — повторный запуск ничего не перезаписывает.
        File.WriteAllText(Path.Combine(folder, "collect", "Collect-Components.cmd"), "моя");
        ScriptCatalog.Seed(folder, bundled);
        Assert.Equal("моя", File.ReadAllText(Path.Combine(folder, "collect", "Collect-Components.cmd")));
    }

    public void Dispose() => Directory.Delete(_root, true);
}
