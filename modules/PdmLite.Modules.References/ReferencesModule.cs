using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using PdmLite.Core.Events;
using PdmLite.Core.Documents;
using PdmLite.Core.Projects;
using PdmLite.Core.Modularity;
using PdmLite.Core.Threading;
using PdmLite.Core.UI;
using PdmLite.Data;
using PdmLite.Index;

namespace PdmLite.Modules.References;

/// <summary>
/// Связи документов: панель «Связи» (где используется, из чего состоит) — в Inventor и Explorer;
/// обновление индекса проекта и битые ссылки — в Explorer (документы читаются через Apprentice).
/// </summary>
public sealed class ReferencesModule : IPdmModule
{
    private ReferencesPanelViewModel? _panel;
    private IDisposable? _projectOpened;

    public ModuleInfo Info { get; } = new("References", "Связи документов", new Version(0, 1, 0));

    public void ConfigureServices(IServiceCollection services, HostKind host)
    {
        services.TryAddSingleton<ProjectSearchPaths>();
        services.TryAddSingleton<ReferenceIndexService>();
        services.AddSingleton<ReferencesPanelViewModel>();
        services.AddTransient<IndexViewModel>();
        services.AddSingleton(sp => new ProjectHealthService(
            sp.GetRequiredService<IDocumentPropertyStore>(),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PdmLite", "ProjectHealth"),
            (project, path, usedIn) => sp.GetRequiredService<ReferenceIndexService>().Locate(path, usedIn, project)));
    }

    public void Initialize(IModuleContext context)
    {
        var services = context.Services;
        _panel = services.GetRequiredService<ReferencesPanelViewModel>();
        _panel.Attach();
        context.Ui.RegisterPanel(new PanelDescriptor(
            "References.Panel", "Связи", PanelPlacement.Details,
            sp => new ReferencesPanelView { DataContext = sp.GetRequiredService<ReferencesPanelViewModel>() },
            Order: 10, Icon: IconKeys.ProjectTree));

        if (context.Host != HostKind.Explorer)
            return;

        context.Ui.RegisterCommand(new CommandDescriptor(
            "References.Index", "Индекс ссылок проекта…", CommandPlacement.Toolbar,
            _ =>
            {
                new IndexWindow(services.GetRequiredService<IndexViewModel>()) { Owner = Application.Current?.MainWindow }.Show();
                return Task.CompletedTask;
            },
            Order: 0, Group: "2-references", Icon: IconKeys.ProjectTree));

        var session = services.GetRequiredService<IProjectSession>();
        void ShowHealth()
        {
            if (session.Project is not { } project) return;
            new ProjectHealthWindow(services.GetRequiredService<ProjectHealthService>(), project)
                { Owner = Application.Current?.MainWindow }.Show();
        }
        context.Ui.RegisterCommand(new CommandDescriptor(
            "References.ProjectHealth", "Карточка проекта…", CommandPlacement.Toolbar,
            _ => { ShowHealth(); return Task.CompletedTask; },
            _ => session.Project is not null, Order: 0, Group: "0-project-health", Icon: IconKeys.Info));

        // Проект уже индексировался — при открытии тихо дочитываем изменения в фоне.
        var events = services.GetRequiredService<IEventBus>();
        var index = services.GetRequiredService<ReferenceIndexService>();
        var database = services.GetRequiredService<IPdmDatabase>();
        var logger = services.GetRequiredService<ILogger<ReferencesModule>>();
        _projectOpened = events.Subscribe<ProjectOpened>(e => Task.Run(async () =>
        {
            try
            {
                if (!database.IsConfigured || !await index.IsIndexedAsync(e.Project.RootFolder))
                    return;
                await index.ScanAsync(e.Project.RootFolder);
                events.Publish(new ReferenceIndexUpdated(e.Project.RootFolder));
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Фоновое обновление индекса {Root} не выполнено", e.Project.RootFolder);
            }
        }).Forget());
    }

    public void Shutdown()
    {
        _panel?.Detach();
        _projectOpened?.Dispose();
    }
}
