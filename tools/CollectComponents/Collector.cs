using System.Text;

namespace CollectComponents;

/// <summary>Ссылка документа: имя файла и путь, по которому её ждал документ (если известен).</summary>
internal sealed record DocumentReference(string Name, string? ExpectedPath);

/// <summary>Чтение ссылок документа (в программе — Apprentice, в тестах — подмена).</summary>
internal interface IReferenceReader
{
    IReadOnlyList<DocumentReference> Read(string path);
}

/// <summary>Найденный файл.</summary>
/// <param name="Reason">Чья зависимость (<c>null</c> — из списка).</param>
/// <param name="Root">Компонент из списка, в состав которого входит (<c>null</c> — сам из списка).</param>
internal sealed record FoundFile(string Path, string? Reason, string? Root = null)
{
    /// <summary>Куда копировать относительно папки результата: из списка — в неё, состав — в папку компонента.</summary>
    public string RelativeTarget => Root is null
        ? System.IO.Path.GetFileName(Path)
        : System.IO.Path.Combine(System.IO.Path.GetFileNameWithoutExtension(Root), System.IO.Path.GetFileName(Path));
}

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
    /// Файлы списка и состав каждого из них (все уровни) — только из папки компонентов. Каждый файл — один раз:
    /// файл из списка — только наверху; общая деталь — в папке первого компонента списка, которому она нужна.
    /// Несколько файлов с одним именем — берётся самый новый (с предупреждением). Ссылки каждого документа читаются один раз.
    /// </summary>
    public CollectResult Collect(IReadOnlyList<string> names, IReferenceReader? references, Action<string> log)
    {
        var found = new List<FoundFile>();
        var missing = new List<string>();
        var missingKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var warnings = new List<string>();
        var warned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var cache = new Dictionary<string, IReadOnlyList<DocumentReference>?>(StringComparer.OrdinalIgnoreCase);

        FileInfo? Find(string name)
        {
            if (!_byName.TryGetValue(name, out var candidates))
                return null;
            var file = candidates.OrderByDescending(f => f.LastWriteTimeUtc).First();
            if (candidates.Count > 1 && warned.Add(name))
                warnings.Add($"{name}: найдено {candidates.Count} файлов, взят самый новый — {file.FullName}");
            return file;
        }

        void Missing(string name, string text)
        {
            if (missingKeys.Add(name + "|" + text))
                missing.Add(text);
        }

        // Ссылки документа — один раз за запуск (общие детали читаются один раз, а копируются в каждую папку).
        IReadOnlyList<DocumentReference>? Read(FileInfo file)
        {
            if (references is null || !IsInventorFile(file.FullName))
                return [];
            if (cache.TryGetValue(file.FullName, out var known))
                return known;
            IReadOnlyList<DocumentReference>? result = null;
            try
            {
                result = references.Read(file.FullName);
                log(result.Count == 0 ? $"{file.Name}: зависимостей нет" : $"{file.Name}: зависимостей {result.Count}");
                foreach (var dependency in result)
                {
                    var here = _byName.ContainsKey(dependency.Name);
                    log($"    {(here ? "+" : "-")} {dependency.Name}{(here ? "" : "  (нет в папке)")}");
                }
            }
            catch (Exception ex)
            {
                warnings.Add($"{file.Name}: зависимости не прочитаны — {ex.Message}");
                log($"{file.Name}: зависимости НЕ прочитаны — {ex.Message}");
            }
            cache[file.FullName] = result;
            return result;
        }

        // Сначала все компоненты списка — наверх; каждый файл копируется один раз (учёт по имени).
        var placed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tops = new List<FileInfo>();
        foreach (var name in names)
        {
            var top = Find(name);
            if (top is null)
            {
                Missing(name, $"{name} (из списка)");
                continue;
            }
            if (!placed.Add(top.Name))
                continue;
            found.Add(new FoundFile(top.FullName, null));
            tops.Add(top);
        }

        foreach (var top in tops)
        {
            // Состав компонента — обход вширь. Уже размещённое (из списка или в составе предыдущего) не повторяется,
            // и его состав здесь не обходится — он уже лежит там, где этот файл.
            var queue = new Queue<FileInfo>([top]);
            while (queue.Count > 0)
            {
                var document = queue.Dequeue();
                foreach (var dependency in Read(document) ?? [])
                {
                    if (!placed.Add(dependency.Name))
                        continue;
                    var file = Find(dependency.Name);
                    if (file is null)
                    {
                        Missing(dependency.Name, $"{dependency.Name} (зависимость {document.Name}"
                                                 + (dependency.ExpectedPath is null ? ")" : $"; сборка ждала её в {dependency.ExpectedPath})"));
                        continue;
                    }
                    found.Add(new FoundFile(file.FullName, document.Name, top.Name));
                    queue.Enqueue(file);
                }
            }
        }

        return new CollectResult(found, missing, warnings);
    }

    /// <summary>Папка результата внутри папки поиска: при следующем запуске копии найдутся как дубликаты.</summary>
    public bool IsInsideSource(string target)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(target));
        return full.Equals(_source, StringComparison.OrdinalIgnoreCase)
               || full.StartsWith(_source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsInventorFile(string path) =>
        InventorExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Копирует: компоненты списка — в папку результата, их состав — в подпапки с именами компонентов
    /// (снимая «только чтение» у копий). Пишет отчёт; возвращает его путь.
    /// </summary>
    public string Copy(CollectResult result, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in result.Found)
        {
            var destination = Path.Combine(target, file.RelativeTarget);
            if (string.Equals(Path.GetFullPath(destination), Path.GetFullPath(file.Path), StringComparison.OrdinalIgnoreCase))
                continue; // папка результата совпала с местом файла — он уже там
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if (File.Exists(destination))
                File.SetAttributes(destination, FileAttributes.Normal); // копия прошлого запуска могла остаться «только чтение»
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
            report.AppendLine($"  {file.RelativeTarget}  ←  {file.Path}" + (file.Reason is null ? "" : $"  [зависимость {file.Reason}]"));
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
