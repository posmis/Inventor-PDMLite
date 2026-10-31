using System.Text;

namespace CollectComponents;

/// <summary>Ссылка документа: имя файла и путь, по которому её ждал документ (если известен).</summary>
internal sealed record DocumentReference(string Name, string? ExpectedPath);

/// <summary>Чтение ссылок документа (в программе — Apprentice, в тестах — подмена).</summary>
internal interface IReferenceReader
{
    IReadOnlyList<DocumentReference> Read(string path);
}

/// <summary>Найденный файл; <paramref name="Reason"/> — чья зависимость (<c>null</c> — из списка).</summary>
internal sealed record FoundFile(string Path, string? Reason);

internal sealed record CollectResult(IReadOnlyList<FoundFile> Found, IReadOnlyList<string> Missing, IReadOnlyList<string> Warnings);

/// <summary>
/// Поиск по имени только в папке компонентов (с подпапками, без OldVersions) и обход зависимостей сборок.
/// </summary>
internal sealed class Collector
{
    private static readonly string[] InventorExtensions = [".ipt", ".iam", ".idw", ".dwg", ".ipn"];

    private readonly string _source;
    private readonly Dictionary<string, List<FileInfo>> _byName = new(StringComparer.OrdinalIgnoreCase);

    public Collector(string source)
    {
        // Нормальный вид пути (без двойных обратных косых и косой в конце) — и в поиске, и в отчёте.
        source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source));
        _source = source;
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var file in new DirectoryInfo(source).EnumerateFiles("*", options))
        {
            if (file.FullName.Split(Path.DirectorySeparatorChar).Any(p => p.Equals("OldVersions", StringComparison.OrdinalIgnoreCase)))
                continue;
            if (!_byName.TryGetValue(file.Name, out var list))
                _byName[file.Name] = list = [];
            list.Add(file);
        }
    }

    public int FileCount => _byName.Values.Sum(l => l.Count);

    /// <summary>Строки списка → имена файлов: без пустых и повторов; путь или таблица (табуляция) → имя из первого поля.</summary>
    public static List<string> ParseNames(IEnumerable<string> lines)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new List<string>();
        foreach (var line in lines)
        {
            var text = line.Split('\t')[0].Trim().Trim('"');
            if (text.Length == 0)
                continue;
            var name = Path.GetFileName(text.Replace('/', '\\').Split('\\')[^1]);
            if (name.Length > 0 && seen.Add(name))
                names.Add(name);
        }
        return names;
    }

    /// <summary>
    /// Файлы списка и все зависимости найденных документов (на всех уровнях) — только из папки компонентов.
    /// Несколько файлов с одним именем — берётся самый новый (с предупреждением). Ход — в <paramref name="log"/>.
    /// </summary>
    public CollectResult Collect(IReadOnlyList<string> names, IReferenceReader? references, Action<string> log)
    {
        var found = new List<FoundFile>();
        var foundNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var missing = new List<string>();
        var missingNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var warnings = new List<string>();
        var queue = new Queue<(string Name, string? Reason, string? Expected)>(names.Select(n => (n, (string?)null, (string?)null)));

        while (queue.Count > 0)
        {
            var (name, reason, expected) = queue.Dequeue();
            if (foundNames.Contains(name) || missingNames.Contains(name))
                continue;

            if (!_byName.TryGetValue(name, out var candidates))
            {
                missingNames.Add(name);
                missing.Add(reason is null ? $"{name} (из списка)" : $"{name} (зависимость {reason}" + (expected is null ? ")" : $"; сборка ждала её в {expected})"));
                continue;
            }

            var file = candidates.OrderByDescending(f => f.LastWriteTimeUtc).First();
            if (candidates.Count > 1)
                warnings.Add($"{name}: найдено {candidates.Count} файлов, взят самый новый — {file.FullName}");
            foundNames.Add(name);
            found.Add(new FoundFile(file.FullName, reason));

            if (references is null || !IsInventorFile(file.FullName))
                continue;
            try
            {
                var dependencies = references.Read(file.FullName);
                log(dependencies.Count == 0
                    ? $"{file.Name}: зависимостей нет"
                    : $"{file.Name}: зависимостей {dependencies.Count}");
                foreach (var dependency in dependencies)
                {
                    var here = _byName.ContainsKey(dependency.Name);
                    log($"    {(here ? "+" : "-")} {dependency.Name}{(here ? "" : "  (нет в папке)")}");
                    queue.Enqueue((dependency.Name, file.Name, dependency.ExpectedPath));
                }
            }
            catch (Exception ex)
            {
                warnings.Add($"{file.Name}: зависимости не прочитаны — {ex.Message}");
                log($"{file.Name}: зависимости НЕ прочитаны — {ex.Message}");
            }
        }

        return new CollectResult(found, missing, warnings);
    }

    public static bool IsInventorFile(string path) =>
        InventorExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>Копирует найденное в одну папку (снимая «только чтение» у копий) и пишет отчёт. Возвращает путь отчёта.</summary>
    public string Copy(CollectResult result, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in result.Found)
        {
            var destination = Path.Combine(target, Path.GetFileName(file.Path));
            File.Copy(file.Path, destination, overwrite: true);
            File.SetAttributes(destination, File.GetAttributes(destination) & ~FileAttributes.ReadOnly);
        }

        var report = new StringBuilder()
            .AppendLine($"Сбор файлов: {DateTime.Now:dd.MM.yyyy HH:mm}")
            .AppendLine($"Где искали: {_source}")
            .AppendLine($"Куда скопировано: {target}")
            .AppendLine()
            .AppendLine($"СКОПИРОВАНО ({result.Found.Count}):");
        foreach (var file in result.Found)
            report.AppendLine($"  {Path.GetFileName(file.Path)}  ←  {file.Path}" + (file.Reason is null ? "" : $"  [зависимость {file.Reason}]"));
        report.AppendLine().AppendLine($"НЕ НАЙДЕНО ({result.Missing.Count}):");
        foreach (var line in result.Missing)
            report.AppendLine($"  {line}");
        if (result.Warnings.Count > 0)
        {
            report.AppendLine().AppendLine($"ЗАМЕЧАНИЯ ({result.Warnings.Count}):");
            foreach (var line in result.Warnings)
                report.AppendLine($"  {line}");
        }

        var path = Path.Combine(target, "_отчёт.txt");
        File.WriteAllText(path, report.ToString(), new UTF8Encoding(true));
        return path;
    }
}
