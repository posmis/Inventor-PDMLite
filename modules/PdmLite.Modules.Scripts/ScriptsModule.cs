using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using PdmLite.Core.Modularity;
using PdmLite.Core.Scripts;
using PdmLite.Core.UI;

namespace PdmLite.Modules.Scripts;

/// <summary>
/// «Утилиты»: внешние скрипты и программы из папки утилит — в Explorer и на ленте Inventor. Модуль только
/// запускает их: ничего не передаёт, не ждёт, не читает результат и не даёт доступа к базе или библиотеке.
/// </summary>
public sealed class ScriptsModule : IPdmModule
{
    public ModuleInfo Info { get; } = new("Scripts", "Утилиты", new Version(0, 1, 0));

    public void ConfigureServices(IServiceCollection services, HostKind host)
    {
    }

    public void Initialize(IModuleContext context)
    {
        context.Ui.RegisterCommand(new CommandDescriptor(
            "Scripts.Open", "Утилиты…", CommandPlacement.Toolbar,
            _ =>
            {
                var folder = ScriptCatalog.DefaultFolder;
                // Утилиты, поставляемые с PdmLite, — при первом открытии (лежат рядом с модулем).
                var bundled = Path.Combine(Path.GetDirectoryName(typeof(ScriptsModule).Assembly.Location)!, "Scripts");
                ScriptCatalog.Seed(folder, bundled);
                new ScriptsWindow(folder) { Owner = context.Host == HostKind.Explorer ? Application.Current?.MainWindow : null }.Show();
                return Task.CompletedTask;
            },
            Order: 0, Group: "9-scripts", Icon: IconKeys.Modules, Panel: "Утилиты"));
    }

    public void Shutdown()
    {
    }
}
