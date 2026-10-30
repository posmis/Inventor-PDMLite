using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PdmLite.Core.Documents;
using PdmLite.Core.Threading;
using PdmLite.Link;

namespace PdmLite.AddIn.Core;

/// <summary>
/// Сервер связи в Inventor: отвечает Explorer на запросы и рассылает события Inventor.
/// Inventor API вызывается только через <see cref="IUiDispatcher"/> (главный поток).
/// </summary>
public sealed class InventorLinkServer : IAsyncDisposable
{
    private readonly LinkServer _server;
    private readonly ILogger _logger;

    public InventorLinkServer(IServiceProvider services, ILogger logger)
    {
        _logger = logger;
        var inventor = services.GetRequiredService<Inv.Application>();
        var ui = services.GetRequiredService<IUiDispatcher>();
        var projects = services.GetRequiredService<InventorProjects>();
        var launcher = services.GetRequiredService<IDocumentLauncher>();
        var version = ui.InvokeAsync(() => inventor.SoftwareVersion.DisplayVersion).GetAwaiter().GetResult();

        _server = new LinkServer(LinkEndpoints.Inventor, logger);

        _server.Handle(LinkProtocol.Requests.Ping, (_, _) =>
            Task.FromResult<object?>(new PingResult("Inventor", version, LinkProtocol.Version)));

        _server.Handle(LinkProtocol.Requests.GetStatus, async (_, _) => await ui.InvokeAsync(() =>
        {
            var project = projects.GetActive();
            return (object?)new InventorStatus(
                version, project?.Name, project?.ProjectFile, project?.RootFolder, ActiveDocumentPath(inventor));
        }));

        _server.Handle<DocumentArgs>(LinkProtocol.Requests.OpenDocument, async (args, ct) =>
        {
            if (string.IsNullOrWhiteSpace(args.Path))
                throw new ArgumentException("Не указан путь к документу.");
            await launcher.OpenAsync(args.Path, ct);
            return null;
        });
    }

    public void Start() => _server.Start();

    /// <summary>Отправляет событие подключённым клиентам, не блокируя главный поток Inventor.</summary>
    public void Broadcast(string eventName, string? path)
    {
        if (_server.ClientCount == 0)
            return;
        _ = Task.Run(async () =>
        {
            try
            {
                await _server.BroadcastAsync(eventName, new DocumentArgs(path));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Не удалось отправить событие {Event}", eventName);
            }
        });
    }

    internal static string? ActiveDocumentPath(Inv.Application inventor)
    {
        var path = inventor.ActiveDocument?.FullFileName;
        return string.IsNullOrEmpty(path) ? null : path; // новый несохранённый документ — без пути
    }

    public ValueTask DisposeAsync() => _server.DisposeAsync();
}
