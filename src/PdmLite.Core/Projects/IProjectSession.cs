using PdmLite.Core.Domain;

namespace PdmLite.Core.Projects;

/// <summary>Открытый в хосте проект и его дерево.</summary>
public interface IProjectSession
{
    ProjectInfo? Project { get; }
    ProjectItem? Root { get; }

    /// <summary>
    /// Открывает проект и строит дерево. События ProjectOpened доставляются в порядке фиксации:
    /// при конкурентном или повторном открытии доставка может завершиться после возврата метода.
    /// </summary>
    Task OpenAsync(ProjectInfo project, CancellationToken cancellationToken = default);

    /// <summary>Перестраивает дерево текущего проекта.</summary>
    Task RefreshAsync(CancellationToken cancellationToken = default);

    /// <summary>Ищет файл в дереве текущего проекта.</summary>
    ProjectItem? Find(string path);
}
