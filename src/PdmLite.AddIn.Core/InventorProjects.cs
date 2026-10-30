using PdmLite.Core.Domain;

namespace PdmLite.AddIn.Core;

/// <summary>Проекты Inventor (.ipj).</summary>
public sealed class InventorProjects(Inv.Application inventor)
{
    /// <summary>Активный проект Inventor. Вызывать в главном потоке.</summary>
    public ProjectInfo? GetActive()
    {
        var project = inventor.DesignProjectManager.ActiveDesignProject;
        if (project is null)
            return null;

        var file = project.FullFileName;
        var workspace = project.WorkspacePath;

        var root = !string.IsNullOrWhiteSpace(workspace) && Directory.Exists(workspace)
            ? workspace
            : !string.IsNullOrWhiteSpace(file) ? Path.GetDirectoryName(file) : null;

        return root is null
            ? null
            : new ProjectInfo(project.Name, Path.TrimEndingDirectorySeparator(root), string.IsNullOrWhiteSpace(file) ? null : file);
    }
}
