using System.Diagnostics;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using PdmLite.Core.Documents;
using PdmLite.Core.Modularity;
using PdmLite.Core.Projects;
using PdmLite.Core.UI;
using PdmLite.Modules.ProjectTree.ViewModels;
using PdmLite.Modules.ProjectTree.Views;

namespace PdmLite.Modules.ProjectTree;

public sealed class ProjectTreeModule : IPdmModule
{
    public const string ModuleId = "ProjectTree";

    private ProjectTreeViewModel? _viewModel;

    public ModuleInfo Info { get; } = new(ModuleId, "Дерево проекта", new Version(0, 1, 0));

    public void ConfigureServices(IServiceCollection services, HostKind host)
    {
        services.AddSingleton<ProjectTreeViewModel>();
    }

    public void Initialize(IModuleContext context)
    {
        _viewModel = context.Services.GetRequiredService<ProjectTreeViewModel>();
        _viewModel.Attach();

        context.Ui.RegisterPanel(new PanelDescriptor(
            "ProjectTree.Panel", "Проект", PanelPlacement.Navigator,
            sp => new ProjectTreeView { DataContext = sp.GetRequiredService<ProjectTreeViewModel>() },
            Icon: IconKeys.ProjectTree));

        var launcher = context.Services.GetRequiredService<IDocumentLauncher>();
        var session = context.Services.GetRequiredService<IProjectSession>();

        context.Ui.RegisterCommand(new CommandDescriptor(
            "ProjectTree.Open", "Открыть", CommandPlacement.ContextMenu,
            ctx => launcher.OpenAsync(ctx.Single!.Path),
            ctx => ctx.Single is { IsFolder: false },
            Order: 0, Group: "0-open", IsDefault: true, Icon: IconKeys.Open));

        context.Ui.RegisterCommand(new CommandDescriptor(
            "ProjectTree.ShowInExplorer", "Показать в проводнике Windows", CommandPlacement.ContextMenu,
            ctx =>
            {
                var item = ctx.Single!;
                var args = item.IsFolder ? $"\"{item.Path}\"" : $"/select,\"{item.Path}\"";
                Process.Start(new ProcessStartInfo("explorer.exe", args) { UseShellExecute = true })?.Dispose();
                return Task.CompletedTask;
            },
            ctx => ctx.Single is not null,
            Order: 10, Group: "1-file", Icon: IconKeys.ShowInWindows));

        context.Ui.RegisterCommand(new CommandDescriptor(
            "ProjectTree.CopyPath", "Копировать путь", CommandPlacement.ContextMenu,
            ctx =>
            {
                Clipboard.SetText(string.Join(Environment.NewLine, ctx.Selection.Select(i => i.Path)));
                return Task.CompletedTask;
            },
            ctx => ctx.Selection.Count > 0,
            Order: 20, Group: "1-file", Icon: IconKeys.CopyPath));

        context.Ui.RegisterCommand(new CommandDescriptor(
            "ProjectTree.Refresh", "Обновить дерево проекта", CommandPlacement.ContextMenu,
            _ => session.RefreshAsync(),
            _ => session.Project is not null,
            Order: 0, Group: "9-view", Icon: IconKeys.Refresh));
    }

    public void Shutdown() => _viewModel?.Detach();
}
