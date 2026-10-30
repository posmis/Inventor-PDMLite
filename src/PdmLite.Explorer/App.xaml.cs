using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PdmLite.Apprentice;
using PdmLite.Core.Documents;
using PdmLite.Core.Domain;
using PdmLite.Core.Modularity;
using PdmLite.Core.Projects;
using PdmLite.Core.Threading;
using PdmLite.Hosting;
using PdmLite.Link;
using PdmLite.UI.Shared;

namespace PdmLite.Explorer;

public partial class App : Application
{
    private PdmHost? _host;
    private ExplorerLinkServer? _linkServer;
    private ILogger? _logger;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var args = StartupArgs.Parse(e.Args);

        // Второй экземпляр передаёт аргументы уже запущенному Explorer и завершается.
        if (await ForwardToRunningInstanceAsync(args))
        {
            Shutdown();
            return;
        }

        var settings = new JsonSettings<ExplorerSettings>(ExplorerSettings.Name);
        var dispatcher = new WpfUiDispatcher(Dispatcher);

        _host = PdmHost.Create(new PdmHostOptions
        {
            Host = HostKind.Explorer,
            ConfigureServices = services =>
            {
                services.AddSingleton<IUiDispatcher>(dispatcher);
                services.AddSingleton(settings);
                services.AddSingleton<IDocumentPropertyStore, ApprenticePropertyStore>();
            },
        });
        _logger = _host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("PdmLite.Explorer");
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        RegisterExplorerPath();
        _host.Start();

        var window = new MainWindow(new MainViewModel(_host, settings));
        MainWindow = window;
        window.Show();

        _linkServer = new ExplorerLinkServer(
            _host.Services.GetRequiredService<IProjectSession>(), _host.Events, dispatcher, window.BringToFront, _logger);
        _linkServer.Start();

        await OpenInitialProjectAsync(args, settings.Value);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Страховка: если остановка модуля зависнет, процесс всё равно завершится
        // и не будет держать файлы (иначе следующая сборка не сможет их перезаписать).
        var watchdog = new Thread(() =>
        {
            Thread.Sleep(TimeSpan.FromSeconds(5));
            _logger?.LogWarning("Завершение заняло больше 5 с — процесс завершается принудительно");
            Environment.Exit(e.ApplicationExitCode);
        }) { IsBackground = true, Name = "PdmLite exit watchdog" };
        watchdog.Start();

        if (_linkServer is { } server)
            SyncBridge.Run(() => server.DisposeAsync().AsTask(), TimeSpan.FromSeconds(2));
        _host?.Dispose();
        base.OnExit(e);
    }

    private async Task OpenInitialProjectAsync(StartupArgs args, ExplorerSettings settings)
    {
        var path = args.ProjectPath
                   ?? (args.RevealPath is { } reveal ? Path.GetDirectoryName(reveal) : null)
                   ?? settings.LastProject;
        if (path is null || (!Directory.Exists(path) && !File.Exists(path)))
            return;

        var session = _host!.Services.GetRequiredService<IProjectSession>();
        try
        {
            await session.OpenAsync(ProjectInfo.FromPath(path));
            if (args.RevealPath is not null)
                _host.Events.Publish(new Core.Events.RevealDocumentRequested(args.RevealPath));
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Не удалось открыть проект {Path}", path);
            UiMessages.Error($"Не удалось открыть проект:\n{path}", ex);
        }
    }

    private static async Task<bool> ForwardToRunningInstanceAsync(StartupArgs args)
    {
        await using var client = new LinkClient(LinkEndpoints.Explorer);
        if (!await client.TryConnectAsync(TimeSpan.FromMilliseconds(300)))
            return false;

        try
        {
            if (args.RevealPath is not null)
                await client.RequestAsync(LinkProtocol.Requests.RevealDocument, new RevealArgs(args.RevealPath, args.ProjectPath));
            else if (args.ProjectPath is not null)
                await client.RequestAsync(LinkProtocol.Requests.OpenProject, new ProjectArgs(args.ProjectPath));
            else
                await client.RequestAsync(LinkProtocol.Requests.Ping);
            return true;
        }
        catch (Exception)
        {
            // Запущенный экземпляр не отвечает — работаем как отдельное приложение.
            return false;
        }
    }

    /// <summary>Сообщает плагину Inventor, где находится Explorer.</summary>
    private void RegisterExplorerPath()
    {
        try
        {
            var shared = new JsonSettings<SharedSettings>(SharedSettings.Name);
            if (Environment.ProcessPath is { } exe && !string.Equals(shared.Value.ExplorerPath, exe, StringComparison.OrdinalIgnoreCase))
            {
                shared.Value.ExplorerPath = exe;
                shared.Save();
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Не удалось сохранить путь к Explorer");
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _logger?.LogError(e.Exception, "Необработанная ошибка");
        UiMessages.Error("Произошла ошибка.", e.Exception);
        e.Handled = true;
    }
}
