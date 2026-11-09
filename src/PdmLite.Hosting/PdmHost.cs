using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using PdmLite.Core.Documents;
using PdmLite.Core.Events;
using PdmLite.Core.Modularity;
using PdmLite.Core.Projects;
using PdmLite.Core.Threading;
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
public sealed class PdmHost : IDisposable, IAsyncDisposable
{
    private readonly IReadOnlyList<IPdmModule> _modules;
    private readonly ServiceProvider _services;
    private readonly Microsoft.Extensions.Logging.ILogger _logger;
    private readonly List<IPdmModule> _initialized = [];
    private readonly List<ModuleStatus> _statuses;
    private readonly List<ModuleResourceScope> _scopes = [];
    private readonly List<Task> _failedStops = [];
    private readonly object _stopGate = new();
    private Task? _stopTask;
    private bool _started;

    private PdmHost(HostKind host, IReadOnlyList<IPdmModule> modules, IReadOnlyList<ModuleStatus> statuses, ServiceProvider services)
    {
        Host = host;
        _modules = modules;
        _statuses = statuses.ToList();
        Modules = _statuses.AsReadOnly();
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
        try
        {
            var loaderLogger = loggerFactory.CreateLogger("PdmLite.Modules");
            var loader = new ModuleLoader(loaderLogger);

            var config = ModulesConfig.Load(Path.Combine(options.ModulesFolder, ModulesConfig.FileName));
            if (config.LoadError is not null)
                loaderLogger.LogWarning("modules.json не прочитан, включены все модули: {Error}", config.LoadError);
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
            services.AddPdmDatabase();

            options.ConfigureServices?.Invoke(services);

            // Если хост не дал способа читать свойства файлов Inventor — модули получат понятный отказ.
            services.TryAddSingleton<IDocumentPropertyStore>(
                new UnavailablePropertyStore("в этом приложении чтение свойств файлов Inventor не подключено."));
            services.TryAddSingleton<IOpenDocuments, NoOpenDocuments>();
            services.TryAddSingleton<IComponentPlacer>(
                new UnavailableComponentPlacer("Вставка в сборку недоступна: нет связи с Inventor."));

            var loaded = new List<IPdmModule>();
            var finalStatuses = statuses.ToList();
            foreach (var module in ordered)
            {
                var previous = services.ToArray();
                try
                {
                    if (module.Info.Dependencies.Any(id => !loaded.Any(m => string.Equals(m.Info.Id, id, StringComparison.OrdinalIgnoreCase))))
                        throw new InvalidOperationException("Не зарегистрирована обязательная зависимость модуля.");
                    module.ConfigureServices(services, options.Host);
                    loaded.Add(module);
                }
                catch (Exception ex)
                {
                    services.Clear();
                    foreach (var descriptor in previous) services.Add(descriptor);
                    loaderLogger.LogError(ex, "Ошибка регистрации сервисов модуля {Id}", module.Info.Id);
                    Fail(finalStatuses, module, ex);
                }
            }

            var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = false });
            loaderLogger.LogInformation("Хост {Host}: загружено модулей {Count}", options.Host, loaded.Count);
            return new PdmHost(options.Host, loaded, finalStatuses, provider);
        }
        catch { loggerFactory.Dispose(); throw; }
    }

    /// <summary>Инициализирует модули. Вызывается хостом в потоке интерфейса.</summary>
    public void Start()
    {
        lock (_stopGate)
        {
            ObjectDisposedException.ThrowIf(_stopTask is not null, this);
            if (_started) return;
            _started = true;
        }
        foreach (var module in _modules)
        {
            if (module.Info.Dependencies.Any(id => !_initialized.Any(m => string.Equals(m.Info.Id, id, StringComparison.OrdinalIgnoreCase))))
            {
                Fail(_statuses, module, new InvalidOperationException("Не запущена обязательная зависимость модуля."));
                continue;
            }
            var scope = new ModuleResourceScope(Events, Ui);
            try
            {
                module.Initialize(new ModuleContext(Host, new ScopedServices(_services, scope), scope, scope));
                _initialized.Add(module); _scopes.Add(scope);
                _logger.LogInformation("Модуль {Id} {Version} запущен", module.Info.Id, module.Info.Version);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка инициализации модуля {Id}", module.Info.Id);
                scope.Dispose();
                _failedStops.Add(StopModuleAsync(module, scope));
                Fail(_statuses, module, ex);
            }
        }
    }

    public void Dispose()
    {
        var task = DisposeAsync().AsTask();
        if (!SyncBridge.Run(() => task, TimeSpan.FromSeconds(5)))
            _logger.LogWarning("Сервисы ещё останавливаются; logger будет освобождён после завершения.");
        else task.GetAwaiter().GetResult();
    }

    public ValueTask DisposeAsync()
    {
        lock (_stopGate)
        {
            if (_stopTask is not null) return new(_stopTask);
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _stopTask = done.Task;
            _ = CompleteStopAsync(done);
            return new(_stopTask);
        }
    }

    private async Task CompleteStopAsync(TaskCompletionSource done)
    {
        try { await StopAsync().ConfigureAwait(false); done.TrySetResult(); }
        catch (Exception ex) { done.TrySetException(ex); }
    }

    private async Task StopModuleAsync(IPdmModule module, ModuleResourceScope? scope = null)
    {
        async Task StopOnUi()
        {
            Exception? scopeError = null;
            if (scope is not null)
            {
                scope.Dispose();
                try { await scope.Completion; } catch (Exception ex) { scopeError = ex; }
            }
            await module.ShutdownAsync().ConfigureAwait(false);
            if (scopeError is not null) throw scopeError;
        }
        try
        {
            var ui = _services.GetService<IUiDispatcher>();
            if (ui is not null && !ui.CheckAccess())
            {
                Task stop = Task.CompletedTask;
                await ui.InvokeAsync(() => { stop = StopOnUi(); }).ConfigureAwait(false);
                await stop.ConfigureAwait(false);
            }
            else await StopOnUi().ConfigureAwait(false);
        }
        catch (Exception ex) { _logger.LogError(ex, "Ошибка остановки модуля {Id}", module.Info.Id); }
    }

    private async Task StopAsync()
    {
        for (var i = _initialized.Count - 1; i >= 0; i--)
            await StopModuleAsync(_initialized[i], _scopes[i]).ConfigureAwait(false);
        await Task.WhenAll(_failedStops).ConfigureAwait(false);
        _initialized.Clear(); _scopes.Clear(); _failedStops.Clear();
        var loggerFactory = _services.GetRequiredService<ILoggerFactory>();
        try { await _services.DisposeAsync().ConfigureAwait(false); }
        finally { loggerFactory.Dispose(); }
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

    private sealed class ScopedServices(IServiceProvider services, ModuleResourceScope scope) : IServiceProvider
    {
        public object? GetService(Type type) => type == typeof(IUiRegistry) || type == typeof(IEventBus) ? scope : services.GetService(type);
    }

    private sealed record ModuleContext(HostKind Host, IServiceProvider Services, IEventBus Events, IUiRegistry Ui) : IModuleContext;
}
