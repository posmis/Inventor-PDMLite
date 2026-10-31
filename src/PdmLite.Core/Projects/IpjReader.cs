using System.Xml;
using System.Xml.Linq;

namespace PdmLite.Core.Projects;

/// <summary>Минимальное чтение файла проекта Inventor (.ipj — XML).</summary>
public static class IpjReader
{
    /// <summary>
    /// Возвращает папку рабочего пространства (Workspace) проекта или <c>null</c>,
    /// если её не удалось определить. Относительный путь считается от папки .ipj.
    /// </summary>
    public static string? TryGetWorkspaceFolder(string ipjPath)
    {
        try
        {
            var doc = XDocument.Load(ipjPath);
            var value = doc.Descendants()
                .Where(e => e.Name.LocalName is "WorkspacePath" or "Workspace")
                .Select(e => e.Value.Trim())
                .FirstOrDefault(v => v.Length > 0);

            if (value is null)
                return null;

            var baseDir = Path.GetDirectoryName(Path.GetFullPath(ipjPath))!;
            var folder = Path.GetFullPath(Path.Combine(baseDir, value.Replace('\\', Path.DirectorySeparatorChar)));
            return Directory.Exists(folder) ? Path.TrimEndingDirectorySeparator(folder) : null;
        }
        catch (Exception ex) when (ex is IOException or XmlException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Папки поиска проекта Inventor — как Inventor ищет файлы по ссылкам: рабочее пространство, пути поиска,
    /// библиотеки, папка Content Center (все элементы <c>…Path</c> файла проекта). Относительные — от папки .ipj;
    /// несуществующие пропускаются.
    /// </summary>
    public static IReadOnlyList<string> SearchFolders(string ipjPath)
    {
        try
        {
            var doc = XDocument.Load(ipjPath);
            var baseDir = Path.GetDirectoryName(Path.GetFullPath(ipjPath))!;
            var result = new List<string>();
            foreach (var element in doc.Descendants().Where(e => !e.HasElements && e.Name.LocalName.EndsWith("Path", StringComparison.OrdinalIgnoreCase)))
            {
                var value = element.Value.Trim();
                if (value.Length == 0 || value.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
                    continue;
                string folder;
                try
                {
                    folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(baseDir, value.Replace('\\', Path.DirectorySeparatorChar))));
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
                {
                    continue;
                }
                if (Directory.Exists(folder) && !result.Contains(folder, StringComparer.OrdinalIgnoreCase))
                    result.Add(folder);
            }
            return result;
        }
        catch (Exception ex) when (ex is IOException or XmlException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
