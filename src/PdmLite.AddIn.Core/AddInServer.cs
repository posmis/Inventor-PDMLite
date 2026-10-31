using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PdmLite.Core.Documents;
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
    private PdmHost? _host;
    private ILogger? _logger;
    private InventorLinkServer? _link;
    private InventorEventsBridge? _events;
    private AddInUi? _ui;
    private ContentCenterWatcher? _contentCenter;
    private FileResolutionHook? _fileResolution;

    public void Activate(Inv.ApplicationAddInSite addInSiteObject, bool firstTime)
    {
        _inventor = addInSiteObject.Application;
        var inventor = _inventor;

        try
        {
            // Activate вызывается в главном потоке Inventor — запоминаем его диспетчер.
            var dispatcher = new WpfUiDispatcher();
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
                    services.AddSingleton<IDocumentPropertyStore, InventorPropertyStore>();
                    services.AddSingleton<InventorProjects>();
                    services.AddSingleton<ExplorerLauncher>();
                },
            });
            _logger = _host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("PdmLite.AddIn");
            WatchErrors(_logger);
            _logger.LogInformation("Плагин запускается: Inventor {Version}, firstTime={FirstTime}",
                inventor.SoftwareVersion.DisplayVersion, firstTime);

            _host.Start();

            _link = new InventorLinkServer(_host.Services, _logger);
            _link.Start();

            _events = new InventorEventsBridge(_host.Services, _link, _logger);
            _contentCenter = new ContentCenterWatcher(inventor, _host.Events);
            _fileResolution = new FileResolutionHook(inventor, _host.Services, _host.Events, _logger);
            _ui = new AddInUi(_host, ClientId, _logger);
            _ui.Create(firstTime);

            _ = _events.SyncActiveProjectAsync();
        }
        catch (Exception ex)
        {
            _logger?.LogCritical(ex, "Ошибка запуска плагина");
            System.Windows.MessageBox.Show($"PdmLite: ошибка запуска плагина.\n\n{ex.Message}", "PdmLite",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
    }

    public void Deactivate()
    {
        _logger?.LogInformation("Плагин выгружается");

        Safe(() => _ui?.Dispose());
        Safe(() => _events?.Dispose());
        Safe(() => _contentCenter?.Dispose());
        Safe(() => _fileResolution?.Dispose());
        if (_link is { } link)
            Safe(() => SyncBridge.Run(() => link.DisposeAsync().AsTask(), TimeSpan.FromSeconds(3)));
        Safe(() => _host?.Dispose());

        _ui = null;
        _events = null;
        _contentCenter = null;
        _fileResolution = null;
        _link = null;
        _host = null;

        if (_inventor is not null)
        {
            Marshal.ReleaseComObject(_inventor);
            _inventor = null;
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
    }

    public void ExecuteCommand(int commandID)
    {
        // Устаревший механизм Inventor; команды создаются через ControlDefinitions.
    }

    public object? Automation => null;

    private void Safe(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Ошибка при выгрузке плагина");
        }
    }

    /// <summary>
    /// Диагностика сбоев: каждая ошибка Inventor API (COM) в коде PdmLite пишется в журнал с местом вызова,
    /// необработанная ошибка — перед завершением процесса. Так причина падения Inventor видна в логе PdmLite.
    /// </summary>
    private static void WatchErrors(ILogger logger)
    {
        AppDomain.CurrentDomain.FirstChanceException += (_, e) =>
        {
            if (e.Exception is not COMException com)
                return;
            var stack = new System.Diagnostics.StackTrace(1, false).ToString();
            if (!stack.Contains("PdmLite", StringComparison.Ordinal))
                return; // ошибки самого Inventor и других плагинов — не наши
            logger.LogWarning("Ошибка Inventor API (0x{Code:X8}): {Message}{NewLine}{Stack}",
                com.HResult, com.Message, Environment.NewLine, stack);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            logger.LogCritical(e.ExceptionObject as Exception, "Необработанная ошибка в плагине (процесс завершается: {Terminating})", e.IsTerminating);
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            logger.LogError(e.Exception, "Необработанная ошибка фоновой задачи");
            e.SetObserved();
        };
    }
}
