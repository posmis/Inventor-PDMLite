using PdmLite.Core.Projects;
using PdmLite.Core.Threading;

namespace PdmLite.AddIn.Core;

/// <summary>Библиотека PdmLite в разделе «Библиотеки» активного проекта Inventor (.ipj).</summary>
public sealed class InventorProjectLibraryBinding(Inv.Application inventor, IUiDispatcher ui) : IProjectLibraryBinding
{
    public Task<LibraryBinding> CheckAsync(string libraryName, string folder, CancellationToken cancellationToken = default) =>
        ui.InvokeAsync(() =>
        {
            var project = inventor.DesignProjectManager.ActiveDesignProject;
            if (project is null)
                return new LibraryBinding(LibraryBindingState.NoProject, null);

            var path = Find(project, libraryName);
            if (path is null)
                return new LibraryBinding(LibraryBindingState.Missing, project.Name);
            return SameFolder(path.Path, folder)
                ? new LibraryBinding(LibraryBindingState.Connected, project.Name, path.Path)
                : new LibraryBinding(LibraryBindingState.DifferentFolder, project.Name, path.Path);
        });

    public Task ConnectAsync(string libraryName, string folder, CancellationToken cancellationToken = default) =>
        ui.InvokeAsync(() =>
        {
            var project = inventor.DesignProjectManager.ActiveDesignProject
                          ?? throw new InvalidOperationException("В Inventor не выбран проект.");

            // Inventor не меняет активный проект, пока открыты документы — как и в своём окне «Проекты».
            if (inventor.Documents.VisibleDocuments.Count > 0)
                throw new InvalidOperationException("Закройте все документы Inventor и повторите: проект нельзя менять, пока в нём открыты файлы.");

            Directory.CreateDirectory(folder);
            Find(project, libraryName)?.Delete();
            project.LibraryPaths.Add(libraryName, folder);

            if (Find(project, libraryName) is not { } added || !SameFolder(added.Path, folder))
                throw new InvalidOperationException("Inventor не сохранил библиотеку в проекте. Добавьте её вручную: Проекты → Библиотеки.");
        });

    private static Inv.ProjectPath? Find(Inv.DesignProject project, string name)
    {
        foreach (Inv.ProjectPath path in project.LibraryPaths)
            if (string.Equals(path.Name, name, StringComparison.OrdinalIgnoreCase))
                return path;
        return null;
    }

    private static bool SameFolder(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
            StringComparison.OrdinalIgnoreCase);
}
