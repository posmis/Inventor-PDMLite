using PdmLite.Core.Projects;

namespace PdmLite.Core.Domain;

/// <summary>Проект: корневая папка и (необязательно) файл проекта Inventor.</summary>
public sealed record ProjectInfo(string Name, string RootFolder, string? ProjectFile = null)
{
    /// <summary>Создаёт проект по пути к папке или к файлу <c>.ipj</c>.</summary>
    public static ProjectInfo FromPath(string path)
    {
        var fullPath = Path.GetFullPath(path);

        if (File.Exists(fullPath) && DocumentKinds.FromPath(fullPath) == DocumentKind.Project)
        {
            var root = IpjReader.TryGetWorkspaceFolder(fullPath) ?? Path.GetDirectoryName(fullPath)!;
            return new ProjectInfo(Path.GetFileNameWithoutExtension(fullPath), root, fullPath);
        }

        if (Directory.Exists(fullPath))
        {
            var trimmed = Path.TrimEndingDirectorySeparator(fullPath);
            return new ProjectInfo(Path.GetFileName(trimmed) is { Length: > 0 } name ? name : trimmed, trimmed);
        }

        throw new FileNotFoundException("Папка или файл проекта не найдены.", fullPath);
    }
}
