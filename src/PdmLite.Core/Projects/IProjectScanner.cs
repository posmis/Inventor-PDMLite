using PdmLite.Core.Domain;

namespace PdmLite.Core.Projects;

/// <summary>Построение дерева проекта.</summary>
public interface IProjectScanner
{
    Task<ProjectItem> ScanAsync(ProjectInfo project, CancellationToken cancellationToken = default);
}
