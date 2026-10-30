using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using PdmLite.Core.Documents;
using PdmLite.Core.Events;
using PdmLite.Core.Modularity;
using PdmLite.Core.Projects;
using PdmLite.Core.UI;
using Serilog;

namespace PdmLite.Hosting;

/// <summary>Параметры хоста.</summary>
public sealed class PdmHostOptions
{
    public required HostKind Host { get; init; }

    /// <summary>Папка, в которой ищутся модули и <c>modules.json</c>.</summary>
    public string ModulesFolder { get; init; } = AppContext.BaseDirectory;

    /// <summary>Дополнительные модули, переданные хостом явно (например, в тестах).</summary>
    public IReadOnlyList<IPdmModule> ExtraModules { get; init; } = [];

    /// <summary>Сервисы хоста. Регистрируются до модулей; модуль может заменить их.</summary>
    public Action<IServiceCollection>? ConfigureServices { get; init; }

    /// <summary>Писать журнал в файл (<see cref="PdmLitePaths.LogsFolder"/>).</summary>
    public bool FileLogging { get; init; } = true;
}

/// <summary>
/// Хост модулей: общий для плагина Inventor и Explorer. Собирает контейнер,
/// загружает модули в порядке зависимостей и управляет их жизненным циклом.
/// </summary>
public sealed class PdmHost : IDisposable
{
    private readonly IReadOnlyList<IPdmModule> _modules;
    private readonly ServiceProvider _services;
    private readonly Microsoft.Extensions.Logging.ILogger _logger;
    private readonly List<IPdmModule> _initialized = [];

    private PdmHost(HostKind host, IReadOnlyList<IPdmModule> modules, IReadOnlyList<ModuleStatus> statuses, ServiceProvider services)
    {
        Host = host;
        _modules = modules;
        Modules = statuses;
        _services = services;
        _logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("PdmLite.Host");
    }

    public HostKind Host { get; }
    public IServiceProvider Services => _services;
    public IReadOnlyList<ModuleStatus> Modules { get; }
    public IUiRegistry Ui => _services.GetRequiredService<IUiRegistry>();
    public IEventBus Events => _services.GetRequiredService<IEventBus>();

    public static PdmHost Create(PdmHostOptions options)
    {
        var loggerFactory = CreateLoggerFactory(options);
        var loaderLogger = loggerFactory.CreateLogger("PdmLite.Modules");
        var loader = new ModuleLoader(loaderLogger);

        var config = ModulesConfig.Load(Path.Combine(options.ModulesFolder, ModulesConfig.FileName));
        var candidates = loader.Discover(options.ModulesFolder).Concat(options.ExtraModules);
        var (ordered, statuses) = loader.Resolve(candidates, options.Host, config);

        var services = new ServiceCollection();
        services.AddLogging();
        services.Replace(ServiceDescriptor.Singleton(loggerFactory));

        services.AddSingleton<IEventBus, EventBus>();
        services.AddSingleton<IUiRegistry, UiRegistry>();
        services.AddSingleton<IProjectScanner, FileSystemProjectScanner>();
        services.AddSingleton<IProjectSession, ProjectSession>();
        services.TryAddSingleton<IDocumentLauncher, ShellDocumentLauncher>();

        options.ConfigureServices?.Invoke(services);

        var loaded = new List<IPdmModule>();
        var finalStatuses = statuses.ToList();
        foreach (var module in ordered)
        {
            try
            {
                module.ConfigureServices(services, options.Host);
                loaded.Add(module);
            }
            catch (Exception ex)
            {
                loaderLogger.LogError(ex, "Ошибка регистрации сервисов модуля {Id}", module.Info.Id);
                Fail(finalStatuses, module, ex);
            }
        }

        var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = false });
        loaderLogger.LogInformation("Хост {Host}: загружено модулей {Count}", options.Host, loaded.Count);
        return new PdmHost(options.Host, loaded, finalStatuses, provider);
    }

    /// <summary>Инициализирует модули. Вызывается хостом в потоке интерфейса.</summary>
    public void Start()
    {
        var context = new ModuleContext(Host, _services, Events, Ui);
        foreach (var module in _modules)
        {
            try
            {
                module.Initialize(context);
                _initialized.Add(module);
                _logger.LogInformation("Модуль {Id} {Version} запущен", module.Info.Id, module.Info.Version);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка инициализации модуля {Id}", module.Info.Id);
            }
        }
    }

    public void Dispose()
    {
        for (var i = _initialized.Count - 1; i >= 0; i--)
        {
            try
            {
                _initialized[i].Shutdown();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка остановки модуля {Id}", _initialized[i].Info.Id);
            }
        }
        _initialized.Clear();
        var loggerFactory = _services.GetRequiredService<ILoggerFactory>();
        _services.Dispose();
        loggerFactory.Dispose(); // зарегистрирован экземпляром — контейнер его не освобождает
    }

    private static void Fail(List<ModuleStatus> statuses, IPdmModule module, Exception ex)
    {
        var index = statuses.FindIndex(s => s.Info.Id == module.Info.Id);
        if (index >= 0)
            statuses[index] = new ModuleStatus(module.Info, ModuleState.Failed, ex.Message);
    }

    private static ILoggerFactory CreateLoggerFactory(PdmHostOptions options)
    {
        if (!options.FileLogging)
            return LoggerFactory.Create(_ => { });

        // Логгер не делаем глобальным (Log.Logger): внутри Inventor могут работать
        // другие плагины со своим Serilog. Логгер освобождается вместе с фабрикой.
        var logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(
                Path.Combine(PdmLitePaths.LogsFolder, $"{options.Host.ToString().ToLowerInvariant()}-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                shared: true,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        return LoggerFactory.Create(builder => builder.AddSerilog(logger, dispose: true));
    }

    private sealed record ModuleContext(HostKind Host, IServiceProvider Services, IEventBus Events, IUiRegistry Ui) : IModuleContext;
}
