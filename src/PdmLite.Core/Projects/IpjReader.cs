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
}
