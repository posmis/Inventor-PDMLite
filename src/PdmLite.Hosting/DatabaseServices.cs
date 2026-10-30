using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using PdmLite.Data;

namespace PdmLite.Hosting;

public static class DatabaseServices
{
    /// <summary>
    /// База PdmLite: настройки подключения (<c>%AppData%\PdmLite\database.json</c>) и <see cref="IPdmDatabase"/>.
    /// Повторный вызов ничего не меняет.
    /// </summary>
    public static IServiceCollection AddPdmDatabase(this IServiceCollection services)
    {
        services.TryAddSingleton(_ => new JsonSettings<DatabaseSettings>(DatabaseSettings.Name));
        services.TryAddSingleton<IPdmDatabase>(sp =>
            new PdmDatabase(sp.GetRequiredService<JsonSettings<DatabaseSettings>>().Value, sp.GetRequiredService<ILoggerFactory>()));
        return services;
    }
}
