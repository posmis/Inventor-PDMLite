using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PdmLite.Core.Documents;
using PdmLite.Core.Domain;
using PdmLite.Core.Events;
using PdmLite.Core.Modularity;
using PdmLite.Core.Projects;
using PdmLite.Core.UI;
using PdmLite.Index;
using PdmLite.Index.Operations;
using PdmLite.UI.Shared;

namespace PdmLite.Modules.Rename;

/// <summary>
/// Переименование и перемещение файлов проекта — в дереве проекта (правая кнопка мыши, перетаскивание)
/// в Inventor и в Explorer; в Inventor ещё кнопка на ленте для выделенного компонента или активного документа.
/// Ссылки во всех документах проекта переносятся; связанные чертежи переименовываются и переезжают вместе с моделью.
/// </summary>
public sealed class RenameModule : IPdmModule
{
    private readonly List<IDisposable> _subscriptions = [];
    private string? _activeDocument;

    public ModuleInfo Info { get; } = new("Rename", "Переименование и перемещение", new Version(0, 1, 0));

    public void ConfigureServices(IServiceCollection services, HostKind host)
    {
        services.TryAddSingleton<ReferenceIndexService>();
        services.AddSingleton<ProjectFileOperations>();
    }

    public void Initialize(IModuleContext context)
    {
        var services = context.Services;
        var session = services.GetRequiredService<IProjectSession>();
        var events = services.GetRequiredService<IEventBus>();

        bool InProject(ProjectItem item) =>
            session.Project is { } project && !item.Path.Equals(project.RootFolder, StringComparison.OrdinalIgnoreCase)
                                           && item.Path.StartsWith(Path.TrimEndingDirectorySeparator(project.RootFolder) + Path.DirectorySeparatorChar,
                                               StringComparison.OrdinalIgnoreCase);

        context.Ui.RegisterCommand(new CommandDescriptor(
            "Rename.Rename", "Переименовать…", CommandPlacement.ContextMenu,
            ctx => Open(context, FileOperationMode.Rename, [ctx.Single!.Path], null),
            ctx => ctx.Single is { } item && InProject(item),
            Order: 30, Group: "2-change", Icon: IconKeys.Rename));

        context.Ui.RegisterCommand(new CommandDescriptor(
            "Rename.Move", "Переместить…", CommandPlacement.ContextMenu,
            ctx => Open(context, FileOperationMode.Move, ctx.Selection.Select(i => i.Path).ToList(), null),
            ctx => ctx.Selection.Count > 0 && ctx.Selection.All(InProject),
            Order: 31, Group: "2-change", Icon: IconKeys.Move));

        // После переноса файлов в проводнике Windows — восстановить ссылки во всех документах проекта.
        context.Ui.RegisterCommand(new CommandDescriptor(
            "Rename.RepairLinks", "Обновить связи проекта…", CommandPlacement.ContextMenu | CommandPlacement.Toolbar,
            _ => OpenRepair(context),
            _ => session.Project is not null || context.Host == HostKind.Inventor,
            Order: 32, Group: "2-change", Icon: IconKeys.Refresh, Compact: true));

        // Перетаскивание в дереве проекта.
        var ui = services.GetRequiredService<PdmLite.Core.Threading.IUiDispatcher>();
        _subscriptions.Add(events.Subscribe<MoveRequested>(e =>
            ui.Post(() => Open(context, FileOperationMode.Move, e.Paths, e.TargetFolder))));

        if (context.Host != HostKind.Inventor)
            return;

        // В Inventor — кнопка на ленте: выделенный в сборке компонент или активный документ.
        _subscriptions.Add(events.Subscribe<ActiveDocumentChanged>(e => _activeDocument = e.Path));
        context.Ui.RegisterCommand(new CommandDescriptor(
            "Rename.RenameSelected", "Переименовать…", CommandPlacement.Toolbar,
            async _ =>
            {
                string? path = null;
                if (services.GetService<IAssemblyComponents>() is { } assembly)
                {
                    try
                    {
                        path = (await assembly.GetSelectionAsync()).Selected.FirstOrDefault();
                    }
                    catch (InvalidOperationException)
                    {
                        // Активный документ — не сборка: переименовываем его самого.
                    }
                }
                path ??= _activeDocument;
                if (path is null)
                {
                    UiMessages.Info("Выделите компонент в сборке или откройте документ, который нужно переименовать.");
                    return;
                }
                await Open(context, FileOperationMode.Rename, [path], null);
            },
            Order: 5, Group: "2-change", Icon: IconKeys.Rename));
    }

    private static Task Open(IModuleContext context, FileOperationMode mode, IReadOnlyList<string> paths, string? target)
    {
        var services = context.Services;
        var session = services.GetRequiredService<IProjectSession>();
        if (session.Project is not { } project)
        {
            UiMessages.Info("Откройте проект — переименовывать и перемещать можно только файлы проекта.");
            return Task.CompletedTask;
        }
        var root = Path.TrimEndingDirectorySeparator(project.RootFolder);
        var outside = paths.Where(p => !p.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)).ToList();
        if (outside.Count > 0)
        {
            UiMessages.Info($"Файл не из папки открытого проекта ({root}):" + Environment.NewLine + string.Join(Environment.NewLine, outside.Take(5)));
            return Task.CompletedTask;
        }

        var viewModel = new FileOperationViewModel(
            services.GetRequiredService<ProjectFileOperations>(),
            services.GetRequiredService<IOpenDocuments>(),
            session,
            services.GetRequiredService<IEventBus>(),
            services.GetRequiredService<IDocumentPropertyStore>(),
            services.GetRequiredService<IDocumentLauncher>(),
            root, mode, paths, target);
        ShowWindow(context, new FileOperationWindow(viewModel));
        return Task.CompletedTask;
    }

    private static Task OpenRepair(IModuleContext context)
    {
        var services = context.Services;
        var session = services.GetRequiredService<IProjectSession>();
        if (session.Project is not { } project)
        {
            UiMessages.Info("Откройте проект — связи обновляются во всех документах его папки.");
            return Task.CompletedTask;
        }
        var viewModel = new RepairViewModel(services.GetRequiredService<ProjectFileOperations>(), services.GetRequiredService<IOpenDocuments>(),
            session, project.RootFolder);
        ShowWindow(context, new RepairWindow(viewModel));
        return Task.CompletedTask;
    }

    /// <summary>
    /// В Explorer — обычное окно поверх главного. Внутри Inventor — модальное окно, владелец которого — окно Inventor:
    /// иначе клавиатуру перехватывает Inventor (пробел и Enter у него — «повторить последнюю команду», и окно
    /// открывалось второй раз).
    /// </summary>
    private static void ShowWindow(IModuleContext context, Window window)
    {
        if (context.Host == HostKind.Explorer)
        {
            window.Owner = Application.Current?.MainWindow;
            window.Show();
            return;
        }
        new System.Windows.Interop.WindowInteropHelper(window).Owner = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;
        window.ShowDialog();
    }

    public void Shutdown()
    {
        _subscriptions.ForEach(s => s.Dispose());
        _subscriptions.Clear();
    }
}
