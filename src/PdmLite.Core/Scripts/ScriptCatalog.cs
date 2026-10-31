using System.Diagnostics;
using System.Text.Json;
using PdmLite.Core.Naming;

namespace PdmLite.Core.Scripts;

/// <summary>Внешняя утилита: файл, который PDM только запускает.</summary>
/// <param name="Title">Название (из описания <c>&lt;имя&gt;.json</c> или имя файла).</param>
/// <param name="Description">Пояснение из описания, если есть.</param>
public sealed record ScriptInfo(string Path, string Title, string? Description)
{
    public string FileName => System.IO.Path.GetFileName(Path);
}

/// <summary>
/// Внешние утилиты (скрипты): список из папки и запуск отдельным процессом. Утилиты не связаны с PDM:
/// PDM ничего им не передаёт (ни параметров, ни данных, ни подключения к базе), не ждёт и не читает
/// результат — у них свои поля ввода.
/// </summary>
public static class ScriptCatalog
{
    private static readonly string[] Launchable = [".cmd", ".bat", ".exe", ".ps1"];

    /// <summary>Папка утилит по умолчанию: «Документы\PdmLite\Scripts».</summary>
    public static string DefaultFolder =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PdmLite", "Scripts");

    /// <summary>
    /// Утилиты в папке и подпапках. Если рядом <c>.ps1</c> и <c>.cmd</c> (<c>.bat</c>) с одним именем, показывается
    /// только запускалка <c>.cmd</c>. Название и пояснение — из <c>&lt;имя&gt;.json</c> ({"Title": …, "Description": …}).
    /// </summary>
    public static IReadOnlyList<ScriptInfo> List(string folder)
    {
        if (!Directory.Exists(folder))
            return [];
        var files = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Where(f => Launchable.Contains(System.IO.Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .ToList();
        var launchers = files.Where(f => System.IO.Path.GetExtension(f).ToLowerInvariant() is ".cmd" or ".bat")
            .Select(WithoutExtension).ToHashSet(StringComparer.OrdinalIgnoreCase);

        return files
            .Where(f => !(System.IO.Path.GetExtension(f).Equals(".ps1", StringComparison.OrdinalIgnoreCase) && launchers.Contains(WithoutExtension(f))))
            .Select(Describe)
            .OrderBy(s => s.Title, NaturalComparer.Instance)
            .ToList();
    }

    /// <summary>
    /// Запуск отдельным процессом в папке утилиты, без параметров. PowerShell-скрипт — через powershell.exe
    /// (без профиля, с разрешением на запуск этого файла); остальное — как двойной щелчок в проводнике.
    /// </summary>
    public static void Start(ScriptInfo script)
    {
        var folder = System.IO.Path.GetDirectoryName(script.Path)!;
        ProcessStartInfo start;
        if (System.IO.Path.GetExtension(script.Path).Equals(".ps1", StringComparison.OrdinalIgnoreCase))
        {
            start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, WorkingDirectory = folder };
            foreach (var argument in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-NoExit", "-File", script.Path })
                start.ArgumentList.Add(argument);
        }
        else
        {
            start = new ProcessStartInfo(script.Path) { UseShellExecute = true, WorkingDirectory = folder };
        }
        Process.Start(start)?.Dispose();
    }

    /// <summary>
    /// Первый запуск: в пустую (или новую) папку утилит копируются утилиты, поставляемые с PdmLite
    /// (<paramref name="bundled"/>). Существующие файлы пользователя не перезаписываются.
    /// </summary>
    public static void Seed(string folder, string bundled)
    {
        if (!Directory.Exists(bundled) || (Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any()))
            return;
        foreach (var file in Directory.EnumerateFiles(bundled, "*", SearchOption.AllDirectories))
        {
            var target = System.IO.Path.Combine(folder, System.IO.Path.GetRelativePath(bundled, file));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
            if (!File.Exists(target))
                File.Copy(file, target);
        }
    }

    private static ScriptInfo Describe(string path)
    {
        string? title = null, description = null;
        var manifest = WithoutExtension(path) + ".json";
        if (File.Exists(manifest))
        {
            try
            {
                using var json = JsonDocument.Parse(File.ReadAllText(manifest));
                title = json.RootElement.TryGetProperty("Title", out var t) ? t.GetString() : null;
                description = json.RootElement.TryGetProperty("Description", out var d) ? d.GetString() : null;
            }
            catch (JsonException)
            {
                // Описание с ошибкой — показываем имя файла.
            }
        }
        return new ScriptInfo(path, string.IsNullOrWhiteSpace(title) ? System.IO.Path.GetFileNameWithoutExtension(path) : title!, description);
    }

    private static string WithoutExtension(string path) =>
        System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path)!, System.IO.Path.GetFileNameWithoutExtension(path));
}
