using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PdmLite.Core.Documents;
using PdmLite.Core.Domain;
using PdmLite.Core.Modularity;
using PdmLite.Core.Projects;
using PdmLite.Core.UI;

namespace PdmLite.Modules.InventorLink;

/// <summary>
/// Связь Explorer с плагином в Inventor. Только для Explorer: внутри Inventor
/// плагин работает с Inventor API напрямую.
/// </summary>
public sealed class InventorLinkModule : IPdmModule
{
    private InventorConnection? _connection;

    public ModuleInfo Info { get; } = new("InventorLink", "Связь с Inventor", new Version(0, 1, 0), HostKind.Explorer);

    public void ConfigureServices(IServiceCollection services, HostKind host)
    {
        services.AddSingleton<InventorConnection>();
        services.AddSingleton<IInventorConnection>(sp => sp.GetRequiredService<InventorConnection>());
        // Двойной щелчок в дереве открывает документ в уже запущенном Inventor.
        services.Replace(ServiceDescriptor.Singleton<IDocumentLauncher, LinkedDocumentLauncher>());
    }

    public void Initialize(IModuleContext context)
    {
        _connection = context.Services.GetRequiredService<InventorConnection>();
        _connection.Start();

        var session = context.Services.GetRequiredService<IProjectSession>();
        var connection = _connection;

        context.Ui.RegisterCommand(new CommandDescriptor(
            "InventorLink.OpenActiveProject", "Открыть активный проект Inventor", CommandPlacement.Toolbar,
            async _ =>
            {
                var status = await connection.GetStatusAsync()
                             ?? throw new InvalidOperationException("Нет связи с Inventor.");
                var path = status.ProjectFile ?? status.WorkspaceFolder
                           ?? throw new InvalidOperationException("В Inventor не выбран проект.");
                await session.OpenAsync(ProjectInfo.FromPath(path));
            },
            _ => connection.IsConnected,
            Order: 0, Group: "0-inventor"));
    }

    public void Shutdown()
    {
        _connection?.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
