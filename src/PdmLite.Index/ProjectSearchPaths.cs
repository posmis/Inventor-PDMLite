using PdmLite.Core.Projects;
using PdmLite.Core.Domain;

namespace PdmLite.Index;

/// <summary>
/// Поиск файла по ссылке так, как его ищет Inventor: не по записанному полному пути, а по относительному пути
/// внутри папок поиска проекта (рабочее пространство, библиотеки, Content Center). Например, деталь Content Center
/// записана в сборке как «C:\Users\…\Documents\…\Content Center Files\ru-RU\…\Заклёпка M5.ipt», а у проекта папка
/// Content Center другая — Inventor её находит, и индекс не должен считать её потерянной.
/// </summary>
public sealed class ProjectSearchPaths(IProjectSession session)
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(30);

    private readonly object _gate = new();
    private HashSet<string> _defaultContentCenter = new(StringComparer.OrdinalIgnoreCase);
    private (string? Project, DateTime At, IReadOnlyList<string> Folders) _cache = (null, DateTime.MinValue, []);

    /// <summary>Папки поиска текущего проекта (проект, папки из .ipj, стандартная папка Content Center).</summary>
    public IReadOnlyList<string> Folders(ProjectInfo? project = null)
    {
        project ??= session.Project;
        if (project is null)
            return [];
        var key = project.ProjectFile ?? project.RootFolder;
        lock (_gate)
        {
            if (string.Equals(_cache.Project, key, StringComparison.OrdinalIgnoreCase) && DateTime.UtcNow - _cache.At < MaxAge)
                return _cache.Folders;
        }

        var folders = new List<string> { Path.TrimEndingDirectorySeparator(project.RootFolder) };
        var ipj = project.ProjectFile;
        if (ipj is null && Directory.Exists(project.RootFolder))
            ipj = Directory.EnumerateFiles(project.RootFolder, "*.ipj").FirstOrDefault();
        if (ipj is not null)
            folders.AddRange(IpjReader.SearchFolders(ipj));
        var defaults = DefaultContentCenterFolders().ToList();
        lock (_gate)
            _defaultContentCenter = defaults.ToHashSet(StringComparer.OrdinalIgnoreCase);
        folders.AddRange(defaults);
        var result = folders.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        lock (_gate)
            _cache = (key, DateTime.UtcNow, result);
        return result;
    }

    /// <summary>
    /// Где файл на самом деле, по порядку: 1) самый длинный «хвост» записанного пути в одной из папок поиска
    /// (относительный путь, как ищет Inventor); 2) папка документа, который на него ссылается (Inventor смотрит
    /// и её); 3) файл с тем же именем в папках поиска проекта с подпапками — если такой один.
    /// </summary>
    /// <param name="usedIn">Документы со ссылкой на файл (для поиска рядом с ними).</param>
    public string? Locate(string reference, IEnumerable<string>? usedIn = null, ProjectInfo? project = null)
    {
        var folders = Folders(project);
        if (Locate(reference, folders) is { } byTail)
            return byTail;
        var name = FileName(reference);
        if (name.Length == 0)
            return null;
        foreach (var document in usedIn ?? [])
        {
            var nearby = Path.Combine(Path.GetDirectoryName(document) ?? "", name);
            if (File.Exists(nearby))
                return nearby;
        }
        return Names(folders).TryGetValue(name, out var same) && same.Count == 1 ? same[0] : null;
    }

    private (string? Key, DateTime At, Dictionary<string, List<string>> Names) _names = (null, DateTime.MinValue, []);

    /// <summary>Документы Inventor папок поиска проекта (с подпапками, без OldVersions) по имени файла.</summary>
    private Dictionary<string, List<string>> Names(IReadOnlyList<string> folders)
    {
        var key = string.Join("|", folders);
        lock (_gate)
        {
            if (_names.Key == key && DateTime.UtcNow - _names.At < MaxAge)
                return _names.Names;
        }
        var names = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        // Стандартные папки Content Center огромные — по имени в них не ищем (там хватает относительного пути).
        foreach (var folder in folders.Where(f => !_defaultContentCenter.Contains(f)))
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(folder, "*", options))
                {
                    if (!Core.Domain.DocumentKinds.IsKnown(file)
                        || file.Split(Path.DirectorySeparatorChar).Any(p => p.Equals("OldVersions", StringComparison.OrdinalIgnoreCase)))
                        continue;
                    var fileName = Path.GetFileName(file);
                    if (!names.TryGetValue(fileName, out var list))
                        names[fileName] = list = [];
                    if (!list.Contains(file, StringComparer.OrdinalIgnoreCase))
                        list.Add(file);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Недоступная папка — ищем в остальных.
            }
        }
        lock (_gate)
            _names = (key, DateTime.UtcNow, names);
        return names;
    }

    private static string FileName(string reference) => reference.Replace('/', '\\').Split('\\')[^1].Trim();

    /// <param name="folders">Первая — папка проекта (рабочее пространство), дальше — библиотеки, Content Center.</param>
    public static string? Locate(string reference, IReadOnlyList<string> folders)
    {
        if (folders.Count == 0)
            return null;
        var parts = reference.Replace('/', '\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
        // Без первого элемента (диск или корень) — от длинного хвоста к короткому.
        for (var start = 1; start < parts.Length; start++)
        {
            var tail = Path.Combine(parts[start..]);
            // Одно имя файла в корне самой папки проекта — не повод считать файл найденным: Inventor ищет
            // по относительному пути, а рабочее пространство в ссылках почти всегда с подпапками.
            foreach (var folder in start == parts.Length - 1 ? folders.Skip(1) : folders)
            {
                string candidate;
                try
                {
                    candidate = Path.GetFullPath(Path.Combine(folder, tail));
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
                {
                    continue;
                }
                if (File.Exists(candidate))
                    return candidate;
            }
        }
        return null;
    }

    /// <summary>Папки Content Center по умолчанию (если в проекте не указана своя): «Общие документы\Autodesk\Inventor …\Content Center Files».</summary>
    private static IEnumerable<string> DefaultContentCenterFolders()
    {
        var autodesk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments), "Autodesk");
        if (!Directory.Exists(autodesk))
            return [];
        try
        {
            return Directory.EnumerateDirectories(autodesk, "Inventor*")
                .Select(d => Path.Combine(d, "Content Center Files"))
                .Where(Directory.Exists)
                .OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
