using Microsoft.Extensions.Logging;
using PdmLite.Core.Domain;
using PdmLite.Core.Events;

namespace PdmLite.Core.Projects;

public sealed class ProjectSession(IProjectScanner scanner, IEventBus events, ILogger<ProjectSession> logger) : IProjectSession
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Dictionary<string, ProjectItem> _index = new(StringComparer.OrdinalIgnoreCase);

    public ProjectInfo? Project { get; private set; }
    public ProjectItem? Root { get; private set; }

    public async Task OpenAsync(ProjectInfo project, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            logger.LogInformation("Открытие проекта {Project} ({Root})", project.Name, project.RootFolder);
            var root = await scanner.ScanAsync(project, cancellationToken).ConfigureAwait(false);

            Project = project;
            Root = root;
            _index = root.Descendants().ToDictionary(i => i.Path, StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            _gate.Release();
        }

        events.Publish(new ProjectOpened(project, Root));
    }

    public Task RefreshAsync(CancellationToken cancellationToken = default) =>
        Project is { } project ? OpenAsync(project, cancellationToken) : Task.CompletedTask;

    public ProjectItem? Find(string path) =>
        _index.TryGetValue(Path.GetFullPath(path), out var item) ? item : null;
}
