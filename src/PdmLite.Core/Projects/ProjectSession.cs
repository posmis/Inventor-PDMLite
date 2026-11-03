using Microsoft.Extensions.Logging;
using PdmLite.Core.Domain;
using PdmLite.Core.Events;

namespace PdmLite.Core.Projects;

public sealed class ProjectSession(IProjectScanner scanner, IEventBus events, ILogger<ProjectSession> logger) : IProjectSession
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private sealed record State(ProjectInfo? Project, ProjectItem? Root, Dictionary<string, ProjectItem> Index);
    private State _state = new(null, null, new(StringComparer.OrdinalIgnoreCase));

    public ProjectInfo? Project => Volatile.Read(ref _state).Project;
    public ProjectItem? Root => Volatile.Read(ref _state).Root;

    public async Task OpenAsync(ProjectInfo project, CancellationToken cancellationToken = default)
    {
        ProjectItem root;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            logger.LogInformation("Открытие проекта {Project} ({Root})", project.Name, project.RootFolder);
            root = await scanner.ScanAsync(project, cancellationToken).ConfigureAwait(false);

            var index = root.Descendants().ToDictionary(i => i.Path, StringComparer.OrdinalIgnoreCase);
            cancellationToken.ThrowIfCancellationRequested();
            Volatile.Write(ref _state, new State(project, root, index));
        }
        finally
        {
            _gate.Release();
        }

        events.Publish(new ProjectOpened(project, root));
    }

    public Task RefreshAsync(CancellationToken cancellationToken = default) =>
        Project is { } project ? OpenAsync(project, cancellationToken) : Task.CompletedTask;

    public ProjectItem? Find(string path) =>
        Volatile.Read(ref _state).Index.TryGetValue(Path.GetFullPath(path), out var item) ? item : null;
}
