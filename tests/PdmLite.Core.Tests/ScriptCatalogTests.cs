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
    public void Seed_UpdatesUnchanged_KeepsUserChanges_DoesNotRestoreDeleted()
    {
        var bundled = Path.Combine(_root, "bundled");
        var folder = Path.Combine(_root, "Scripts");
        Add("bundled/collect/Collect.ps1", "версия 1");
        Add("bundled/collect/Collect.cmd", "запуск 1");
        Add("bundled/other.cmd", "другая 1");
        string Target(string relative) => Path.Combine(folder, relative);

        ScriptCatalog.Seed(folder, bundled);
        Assert.Equal("версия 1", File.ReadAllText(Target("collect/Collect.ps1")));

        // Пользователь поправил запуск и удалил «другую»; вышла новая версия всех трёх.
        File.WriteAllText(Target("collect/Collect.cmd"), "мой запуск");
        File.Delete(Target("other.cmd"));
        File.WriteAllText(Path.Combine(bundled, "collect", "Collect.ps1"), "версия 2");
        File.WriteAllText(Path.Combine(bundled, "collect", "Collect.cmd"), "запуск 2");
        File.WriteAllText(Path.Combine(bundled, "other.cmd"), "другая 2");
        Add("bundled/new.cmd", "новая");

        ScriptCatalog.Seed(folder, bundled);

        Assert.Equal("версия 2", File.ReadAllText(Target("collect/Collect.ps1"))); // не меняли — обновлена
        Assert.Equal("мой запуск", File.ReadAllText(Target("collect/Collect.cmd"))); // правка пользователя сохранена
        Assert.False(File.Exists(Target("other.cmd"))); // удалённую не возвращаем
        Assert.Equal("новая", File.ReadAllText(Target("new.cmd"))); // новая утилита — добавлена
        Assert.DoesNotContain(ScriptCatalog.List(folder), s => s.FileName.StartsWith('.'));
    }

    [Fact]
    public void Seed_FolderFromFirstVersion_WithoutManifest_GetsUpdated()
    {
        var bundled = Path.Combine(_root, "bundled");
        var folder = Path.Combine(_root, "Scripts");
        Add("Scripts/collect/Collect.ps1", "версия 1"); // скопировано первой версией, без учёта
        Add("bundled/collect/Collect.ps1", "версия 2");

        ScriptCatalog.Seed(folder, bundled);

        Assert.Equal("версия 2", File.ReadAllText(Path.Combine(folder, "collect", "Collect.ps1")));
    }

    public void Dispose() => Directory.Delete(_root, true);
}
