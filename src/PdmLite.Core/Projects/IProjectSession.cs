using PdmLite.Core.Domain;

namespace PdmLite.Core.Projects;

/// <summary>Открытый в хосте проект и его дерево.</summary>
public interface IProjectSession
{
    ProjectInfo? Project { get; }
    ProjectItem? Root { get; }

    /// <summary>Открывает проект, строит дерево и публикует <see cref="Events.ProjectOpened"/>.</summary>
    Task OpenAsync(ProjectInfo project, CancellationToken cancellationToken = default);

    /// <summary>Перестраивает дерево текущего проекта.</summary>
    Task RefreshAsync(CancellationToken cancellationToken = default);

    /// <summary>Ищет файл в дереве текущего проекта.</summary>
    ProjectItem? Find(string path);
}
