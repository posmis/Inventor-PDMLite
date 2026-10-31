using PdmLite.Core.Projects;

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
    private (string? Project, DateTime At, IReadOnlyList<string> Folders) _cache = (null, DateTime.MinValue, []);

    /// <summary>Папки поиска текущего проекта (проект, папки из .ipj, стандартная папка Content Center).</summary>
    public IReadOnlyList<string> Folders()
    {
        if (session.Project is not { } project)
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
        folders.AddRange(DefaultContentCenterFolders());
        var result = folders.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        lock (_gate)
            _cache = (key, DateTime.UtcNow, result);
        return result;
    }

    /// <summary>
    /// Где файл на самом деле: самый длинный «хвост» записанного пути, который есть в одной из папок поиска
    /// (Inventor ищет по относительному пути, а не по имени по всем подпапкам — так же и здесь).
    /// </summary>
    public string? Locate(string reference) => Locate(reference, Folders());

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
