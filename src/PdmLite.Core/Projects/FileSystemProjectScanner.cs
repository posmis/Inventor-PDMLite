using Microsoft.Extensions.Logging;
using PdmLite.Core.Domain;

namespace PdmLite.Core.Projects;

/// <summary>
/// Строит дерево проекта по файловой системе: только файлы Inventor
/// и папки, в которых они есть.
/// </summary>
public sealed class FileSystemProjectScanner(ILogger<FileSystemProjectScanner> logger) : IProjectScanner
{
    /// <summary>Служебные папки, которые не показываются в дереве.</summary>
    private static readonly HashSet<string> IgnoredFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "OldVersions", ".git", ".vs", "bin", "obj",
    };

    public Task<ProjectItem> ScanAsync(ProjectInfo project, CancellationToken cancellationToken = default) =>
        Task.Run(() => ScanFolder(new DirectoryInfo(project.RootFolder), cancellationToken) ?? EmptyFolder(project.RootFolder), cancellationToken);

    private ProjectItem? ScanFolder(DirectoryInfo dir, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var children = new List<ProjectItem>();
        try
        {
            foreach (var sub in dir.EnumerateDirectories().OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                // Ярлыки на папки (junction, symlink) пропускаем: они могут зациклить обход.
                if (IgnoredFolders.Contains(sub.Name)
                    || (sub.Attributes & (FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint)) != 0)
                    continue;
                if (ScanFolder(sub, ct) is { } folder)
                    children.Add(folder);
            }

            foreach (var file in dir.EnumerateFiles().OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                if (!DocumentKinds.IsKnown(file.Name) || file.Attributes.HasFlag(FileAttributes.Hidden))
                    continue;
                children.Add(new ProjectItem(file.FullName, DocumentKinds.FromPath(file.Name), file.Length, file.LastWriteTimeUtc));
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            logger.LogWarning(ex, "Не удалось прочитать папку {Folder}", dir.FullName);
        }

        return children.Count == 0
            ? null
            : new ProjectItem(dir.FullName, DocumentKind.Folder, 0, dir.LastWriteTimeUtc, children);
    }

    private static ProjectItem EmptyFolder(string path) =>
        new(Path.GetFullPath(path), DocumentKind.Folder, 0, DateTime.MinValue);
}
