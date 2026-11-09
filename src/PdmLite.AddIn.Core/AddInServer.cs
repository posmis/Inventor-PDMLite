using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PdmLite.Core.Documents;
using PdmLite.Core.Lifetime;
using PdmLite.Core.Modularity;
using PdmLite.Core.Projects;
using PdmLite.Core.Threading;
using PdmLite.Hosting;
using PdmLite.UI.Shared;

namespace PdmLite.AddIn.Core;

/// <summary>
/// Плагин PdmLite. Создаётся загрузчиком (PdmLite.AddIn) в изолированном контексте
/// загрузки, который вызывает его методы от имени Inventor.
/// </summary>
public sealed class AddInServer : Inv.ApplicationAddInServer
{
    /// <summary>ClientId плагина: совпадает с ClientId в PdmLite.addin.</summary>
    public const string ClientId = "{daf7f48d-0f53-40ea-8861-0d3fe0627c14}";

    private Inv.Application? _inventor;
    private ResourceLifetime? _lifetime;
    private Task? _cleanup;
    private PdmHost? _host;
    private ILogger? _logger;
    private InventorLinkServer? _link;
    private InventorEventsBridge? _events;
    private AddInUi? _ui;
    private ContentCenterWatcher? _contentCenter;
    private FileResolutionHook? _fileResolution;

    public void Activate(Inv.ApplicationAddInSite addInSiteObject, bool firstTime)
    {
        if (_lifetime is not null) return;
        if (_cleanup is { IsCompletedSuccessfully: false })
        {
            System.Windows.MessageBox.Show("PdmLite: предыдущая остановка ещё не завершена или завершилась ошибкой. Перезапустите Inventor.", "PdmLite");
            return;
        }
        ILogger? cleanupLogger = null;
        var lifetime = _lifetime = new ResourceLifetime(error =>
        {
            System.Diagnostics.Trace.TraceError("PdmLite cleanup: {0}", error);
            cleanupLogger?.LogError(error, "Ошибка очистки ресурсов плагина");
        });
        try
        {
            // Activate вызывается в главном потоке Inventor — запоминаем его диспетчер.
            var dispatcher = new WpfUiDispatcher();
            var inventor = _inventor = addInSiteObject.Application;
            lifetime.Add(() => dispatcher.InvokeAsync(() => Marshal.ReleaseComObject(inventor)));
            var folder = Path.GetDirectoryName(typeof(AddInServer).Assembly.Location)!;

            _host = PdmHost.Create(new PdmHostOptions
            {
                Host = HostKind.Inventor,
                ModulesFolder = folder,
                ConfigureServices = services =>
                {
                    services.AddSingleton(inventor);
                    services.AddSingleton<IUiDispatcher>(dispatcher);
                    services.AddSingleton<IDocumentLauncher, InventorDocumentLauncher>();
                    services.AddSingleton<IComponentPlacer, InventorComponentPlacer>();
                    services.AddSingleton<IProjectLibraryBinding, InventorProjectLibraryBinding>();
                    services.AddSingleton<IAssemblyComponents, InventorAssemblyComponents>();
                    services.AddSingleton<IOpenDocuments, InventorOpenDocuments>();
                    services.AddSingleton<ApprenticeHost>();
                    services.AddSingleton<IDocumentPropertyStore, InventorPropertyStore>();
                    services.AddSingleton<InventorProjects>();
                    services.AddSingleton<ExplorerLauncher>();
                },
            });
            var host = _host;
            lifetime.Add(async () =>
            {
                Task stop = Task.CompletedTask;
                await dispatcher.InvokeAsync(() => { stop = host.DisposeAsync().AsTask(); });
                await stop.ConfigureAwait(false);
            });
            cleanupLogger = _logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("PdmLite.AddIn");
            var monitor = new ErrorMonitor(_logger);
            lifetime.Add(() => { monitor.Dispose(); return Task.CompletedTask; });
            _logger.LogInformation("Плагин запускается: Inventor {Version}, firstTime={FirstTime}",
                inventor.SoftwareVersion.DisplayVersion, firstTime);

            _host.Start();

            var link = _link = new InventorLinkServer(_host.Services, _logger);
            lifetime.Add(() => link.StopAsync());
            link.Start();

            var events = _events = new InventorEventsBridge(_host.Services, _link, _logger);
            lifetime.Add(async () =>
            {
                Task stop = Task.CompletedTask;
                await dispatcher.InvokeAsync(() => { stop = events.StopAsync(); });
                await stop.ConfigureAwait(false);
            });
            events.Attach();
            var contentCenter = _contentCenter = new ContentCenterWatcher(inventor, _host.Events, _logger);
            lifetime.Add(() => dispatcher.InvokeAsync(contentCenter.Dispose));
            contentCenter.Attach();
            var resolution = _fileResolution = new FileResolutionHook(inventor, _host.Services, _host.Events, _logger);
            lifetime.Add(() => dispatcher.InvokeAsync(resolution.Dispose));
            resolution.Attach();
            var ui = _ui = new AddInUi(_host, ClientId, _logger);
            lifetime.Add(() => dispatcher.InvokeAsync(ui.Dispose));
            ui.Create(firstTime);

            _ = _events.SyncActiveProjectAsync();
        }
        catch (Exception ex)
        {
            _logger?.LogCritical(ex, "Ошибка запуска плагина");
            BeginCleanup();
            var cleanupState = _cleanup?.IsFaulted == true ? "\nОчистка завершена не полностью. Перезапустите Inventor."
                : _cleanup?.IsCompleted == false ? "\nОчистка ещё выполняется." : "";
            System.Windows.MessageBox.Show($"PdmLite: ошибка запуска плагина.\n\n{ex.Message}{cleanupState}", "PdmLite",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
    }

    public void Deactivate()
    {
        _logger?.LogInformation("Плагин выгружается");
        BeginCleanup();
    }

    private void BeginCleanup()
    {
        _ui?.StopAcceptingCommands();
        var requests = _link?.StopAsync(); // Native operations must drain before any UI/COM teardown.
        if (_lifetime is { } lifetime)
        {
            _cleanup = DisposeAfterRequestsAsync(lifetime, requests);
            _lifetime = null;
            _ = _cleanup.ContinueWith(t => System.Diagnostics.Trace.TraceError("PdmLite cleanup: {0}", t.Exception),
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        }
        if (_cleanup?.IsCompleted == false)
            _logger?.LogInformation("Остановка выполняется асинхронно. Сервисы и COM будут освобождены после завершения запросов.");
        _ui = null; _events = null; _contentCenter = null; _fileResolution = null;
        _link = null; _host = null; _inventor = null; _logger = null;
    }

    private static async Task DisposeAfterRequestsAsync(ResourceLifetime lifetime, Task? requests)
    {
        if (requests is not null)
        {
            try { await requests; }
            catch { /* The same task is owned by lifetime and its error is reported during unwind. */ }
        }
        await lifetime.DisposeAsync();
    }

    public void ExecuteCommand(int commandID)
    {
        // Устаревший механизм Inventor; команды создаются через ControlDefinitions.
    }

    public object? Automation => null;

}
