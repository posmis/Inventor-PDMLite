using Microsoft.Extensions.Logging;
using PdmLite.Core.Domain;
using PdmLite.Core.Events;
using PdmLite.Core.Projects;
using PdmLite.Core.Threading;
using PdmLite.Link;

namespace PdmLite.Explorer;

/// <summary>
/// Сервер связи Explorer: принимает команды от плагина Inventor
/// («Показать в Explorer») и от повторно запущенного Explorer.
/// </summary>
public sealed class ExplorerLinkServer : IAsyncDisposable
{
    private readonly LinkServer _server;

    public ExplorerLinkServer(IProjectSession session, IEventBus events, IUiDispatcher ui, Action activateWindow, ILogger logger)
    {
        _server = new LinkServer(LinkEndpoints.Explorer, logger);

        _server.Handle(LinkProtocol.Requests.Ping, (_, _) =>
            Task.FromResult<object?>(new PingResult("PdmLite.Explorer", typeof(ExplorerLinkServer).Assembly.GetName().Version!.ToString(), LinkProtocol.Version)));

        _server.Handle<ProjectArgs>(LinkProtocol.Requests.OpenProject, async (args, ct) =>
        {
            await OpenProjectIfNeededAsync(session, args.Path, ct);
            await ui.InvokeAsync(activateWindow);
            return null;
        });

        _server.Handle<RevealArgs>(LinkProtocol.Requests.RevealDocument, async (args, ct) =>
        {
            if (args.ProjectPath is not null)
                await OpenProjectIfNeededAsync(session, args.ProjectPath, ct);
            else if (session.Project is null || session.Find(args.Path) is null)
                await OpenProjectIfNeededAsync(session, Path.GetDirectoryName(args.Path)!, ct);

            events.Publish(new RevealDocumentRequested(args.Path));
            await ui.InvokeAsync(activateWindow);
            return null;
        });
    }

    public void Start() => _server.Start();

    internal static async Task OpenProjectIfNeededAsync(IProjectSession session, string path, CancellationToken ct)
    {
        var project = ProjectInfo.FromPath(path);
        var current = session.Project;
        if (current is not null && string.Equals(current.RootFolder, project.RootFolder, StringComparison.OrdinalIgnoreCase)
            && (project.ProjectFile is null || string.Equals(current.ProjectFile, project.ProjectFile, StringComparison.OrdinalIgnoreCase)))
            return;
        await session.OpenAsync(project, ct);
    }

    public ValueTask DisposeAsync() => _server.DisposeAsync();
}
