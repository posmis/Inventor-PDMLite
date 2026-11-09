using Microsoft.Extensions.DependencyInjection;

namespace PdmLite.Core.Modularity;

/// <summary>
/// Подключаемый модуль. Модули не ссылаются друг на друга: общаются через
/// контракты ядра и <see cref="Events.IEventBus"/>.
/// </summary>
/// <remarks>
/// Порядок вызовов: <see cref="ConfigureServices"/> → <see cref="Initialize"/> → … → <see cref="Shutdown"/>.
/// Класс модуля должен иметь конструктор без параметров.
/// </remarks>
public interface IPdmModule
{
    ModuleInfo Info { get; }

    /// <summary>Регистрация сервисов модуля в контейнере.</summary>
    void ConfigureServices(IServiceCollection services, HostKind host);

    /// <summary>Подписки на события, регистрация панелей и команд.</summary>
    void Initialize(IModuleContext context);

    /// <summary>Must be safe after a partially failed Initialize and release resources owned by the module.</summary>
    void Shutdown();

    ValueTask ShutdownAsync() { Shutdown(); return ValueTask.CompletedTask; }
}
