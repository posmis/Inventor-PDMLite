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

    /// <summary>Учёт установленных поставляемых утилит: путь → контрольная сумма установленной версии.</summary>
    private const string BundledManifest = ".bundled.json";

    /// <summary>
    /// Поставляемые с PdmLite утилиты (<paramref name="bundled"/>) — в папку утилит пользователя:
    /// новые копируются; обновлённые заменяют старые, если пользователь их не менял; изменённые пользователем
    /// не трогаются; удалённые пользователем не возвращаются; больше не поставляемые — убираются, если их не меняли.
    /// Учёт — в <c>.bundled.json</c> в папке утилит.
    /// </summary>
    public static void Seed(string folder, string bundled)
    {
        if (!Directory.Exists(bundled))
            return;
        Directory.CreateDirectory(folder);
        var manifestPath = System.IO.Path.Combine(folder, BundledManifest);
        // Папка заполнена ещё до учёта (первые версии PdmLite): лежащие там поставляемые утилиты считаем нашими.
        var legacy = !File.Exists(manifestPath);
        var installed = ReadManifest(manifestPath);

        foreach (var file in Directory.EnumerateFiles(bundled, "*", SearchOption.AllDirectories))
        {
            var relative = System.IO.Path.GetRelativePath(bundled, file).Replace('\\', '/');
            var target = System.IO.Path.Combine(folder, relative);
            var shipped = Hash(file);

            if (File.Exists(target))
            {
                // Обновляем, только если на месте — ровно то, что ставили мы (пользователь не менял).
                if (installed.TryGetValue(relative, out var previous) && previous == Hash(target) && previous != shipped)
                {
                    File.SetAttributes(target, FileAttributes.Normal);
                    File.Copy(file, target, overwrite: true);
                    installed[relative] = shipped;
                }
                else if (!installed.ContainsKey(relative) && (legacy || Hash(target) == shipped))
                {
                    if (Hash(target) != shipped)
                    {
                        File.SetAttributes(target, FileAttributes.Normal);
                        File.Copy(file, target, overwrite: true);
                    }
                    installed[relative] = shipped;
                }
            }
            else if (!installed.ContainsKey(relative))
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
                File.Copy(file, target);
                installed[relative] = shipped;
            }
            // Есть в учёте, но файла нет — пользователь удалил: не возвращаем.
        }

        // Утилита больше не поставляется (заменена) — убираем её файлы, если пользователь их не менял.
        var shippedNow = Directory.EnumerateFiles(bundled, "*", SearchOption.AllDirectories)
            .Select(f => System.IO.Path.GetRelativePath(bundled, f).Replace('\\', '/')).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (relative, hash) in installed.Where(i => !shippedNow.Contains(i.Key)).ToList())
        {
            var target = System.IO.Path.Combine(folder, relative);
            if (File.Exists(target) && Hash(target) == hash)
            {
                File.SetAttributes(target, FileAttributes.Normal);
                File.Delete(target);
            }
            installed.Remove(relative);
        }

        // Скрытый файл в Windows нельзя перезаписать (доступ запрещён) — сначала снимаем атрибуты.
        if (File.Exists(manifestPath))
            File.SetAttributes(manifestPath, FileAttributes.Normal);
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(installed, new JsonSerializerOptions { WriteIndented = true }));
        File.SetAttributes(manifestPath, FileAttributes.Hidden);
    }

    private static Dictionary<string, string> ReadManifest(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? [];
        }
        catch (JsonException)
        {
        }
        return [];
    }

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
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
