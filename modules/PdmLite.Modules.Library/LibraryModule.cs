using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PdmLite.Core.Modularity;
using PdmLite.Core.UI;
using PdmLite.Hosting;
using PdmLite.Core.Threading;
using PdmLite.Library.Backup;
using PdmLite.Library.Import;
using PdmLite.Library.Items;
using PdmLite.Library.Rules;
using PdmLite.Library.Storage;
using PdmLite.Core.Documents;
using PdmLite.Modules.Library.Backup;
using PdmLite.Modules.Library.Catalog;
using PdmLite.Modules.Library.Checks;
using PdmLite.Modules.Library.Import;
using PdmLite.Modules.Library.Panel;
using PdmLite.Modules.Library.Rules;
using PdmLite.Modules.Library.Settings;

namespace PdmLite.Modules.Library;

/// <summary>
/// Стандартная библиотека: панель с деревом компонентов и вставкой в сборку (Inventor и Explorer);
/// добавление, каталог (изменение, удаление) и настройки — в Explorer (свойства файлов — через Apprentice).
/// </summary>
public sealed class LibraryModule : IPdmModule
{
    public ModuleInfo Info { get; } = new("Library", "Стандартная библиотека", new Version(0, 2, 0));

    /// <summary>Панели ленты Inventor.</summary>
    public const string LibraryPanel = "Библиотека";
    public const string DiagnosticsPanel = "Диагностика сборки";

    private LibraryPanelViewModel? _panel;
    private ContentCenterPrompt? _contentCenter;
    private LibraryFileResolver? _resolver;
    private CancellationTokenSource? _backupStop;
    private Task? _backupTask;

    public void ConfigureServices(IServiceCollection services, HostKind host)
    {
        services.TryAddSingleton(_ => new JsonSettings<LibrarySettings>(LibrarySettings.Name));
        services.AddSingleton<Func<LibraryStorage>>(sp =>
        {
            var settings = sp.GetRequiredService<JsonSettings<LibrarySettings>>();
            return () => new LibraryStorage(settings.Value.RootFolder);
        });
        services.AddSingleton<LibraryImportService>();
        services.AddSingleton(sp =>
        {
            var settings = sp.GetRequiredService<JsonSettings<LibrarySettings>>();
            return new LibraryBackupService(sp.GetRequiredService<PdmLite.Data.IPdmDatabase>(), sp.GetRequiredService<Func<LibraryStorage>>(),
                () => new BackupOptions(settings.Value.BackupFolder, settings.Value.BackupKeep, settings.Value.BackupIntervalDays),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<LibraryBackupService>>());
        });
        services.AddTransient<BackupViewModel>();
        services.AddSingleton<DesignationRuleService>();
        services.AddTransient<RulesViewModel>();
        services.AddSingleton<LibraryItemService>();
        services.AddSingleton<LibraryPanelViewModel>();
        services.AddTransient<ImportViewModel>();
        services.AddTransient<CatalogViewModel>();
        services.AddTransient<AssemblyCheckViewModel>();
        services.AddSingleton<ContentCenterPrompt>();
        services.AddSingleton<LibraryFileResolver>();
        services.AddTransient<AddFromAssembly>();
        services.AddTransient<OccurrenceNamesCommand>();
        services.AddSingleton<IMissingFileResolver>(sp => sp.GetRequiredService<LibraryFileResolver>());
        services.AddTransient<LibrarySettingsViewModel>();
    }

    public void Initialize(IModuleContext context)
    {
        var services = context.Services;

        // Панель «Библиотека» — в обоих хостах: вкладка рядом с деревом проекта.
        _panel = services.GetRequiredService<LibraryPanelViewModel>();
        _panel.Attach();
        context.Ui.RegisterPanel(new PanelDescriptor(
            "Library.Panel", "Библиотека", PanelPlacement.Navigator,
            sp => new LibraryPanelView { DataContext = sp.GetRequiredService<LibraryPanelViewModel>() },
            Order: 10, Icon: IconKeys.Library));

        // Где теперь компоненты библиотеки: в Inventor — подстановка ссылок при открытии сборок,
        // в обоих хостах — индекс ссылок и «Обновить связи проекта» (переехавшие компоненты не считаются потерянными).
        _resolver = services.GetRequiredService<LibraryFileResolver>();
        _resolver.Attach();

        // Проверка сборок и отделение Content Center — там, где есть сборка Inventor (внутри Inventor).
        if (services.GetService<IAssemblyComponents>() is not null)
        {
            _contentCenter = services.GetRequiredService<ContentCenterPrompt>();
            _contentCenter.Attach();


            // Лента Inventor: панель «Библиотека» и панель «Диагностика сборки» (по всей открытой сборке).
            context.Ui.RegisterCommand(new CommandDescriptor(
                "Library.AddSelected", "Добавить в библиотеку", CommandPlacement.Toolbar,
                _ => services.GetRequiredService<AddFromAssembly>().RunAsync(),
                Order: 1, Group: "1-library", Icon: IconKeys.LibraryAdd, Panel: LibraryPanel));

            context.Ui.RegisterCommand(new CommandDescriptor(
                "Library.CheckAssembly", "Проверить сборку", CommandPlacement.Toolbar,
                _ =>
                {
                    new AssemblyCheckWindow(services.GetRequiredService<AssemblyCheckViewModel>()).Show();
                    return Task.CompletedTask;
                },
                Order: 0, Group: "1-library", Icon: IconKeys.Check, Panel: DiagnosticsPanel));

            context.Ui.RegisterCommand(new CommandDescriptor(
                "Library.MissingFiles", "Ненайденные файлы", CommandPlacement.Toolbar,
                _ =>
                {
                    new MissingFilesWindow(services.GetRequiredService<IAssemblyComponents>(),
                        services.GetRequiredService<Microsoft.Extensions.Logging.ILogger<MissingFilesWindow>>()).Show();
                    return Task.CompletedTask;
                },
                Order: 1, Group: "1-library", Icon: IconKeys.Warning, Panel: DiagnosticsPanel));

            context.Ui.RegisterCommand(new CommandDescriptor(
                "Library.OccurrenceNames", "Имена в обозревателе", CommandPlacement.Toolbar,
                _ => services.GetRequiredService<OccurrenceNamesCommand>().RunAsync(),
                Order: 2, Group: "1-library", Icon: IconKeys.ProjectTree, Panel: DiagnosticsPanel));
        }

        // Добавление, каталог и настройки — только в Explorer (свойства файлов читаются через Apprentice).
        if (context.Host != HostKind.Explorer)
            return;

        context.Ui.RegisterCommand(new CommandDescriptor(
            "Library.Import", "Добавить в библиотеку…", CommandPlacement.Toolbar,
            _ =>
            {
                new ImportWindow(services.GetRequiredService<ImportViewModel>(), services) { Owner = Application.Current?.MainWindow }.Show();
                return Task.CompletedTask;
            },
            Order: 0, Group: "1-library", Icon: IconKeys.LibraryAdd));

        context.Ui.RegisterCommand(new CommandDescriptor(
            "Library.Catalog", "Каталог библиотеки…", CommandPlacement.Toolbar,
            _ =>
            {
                new CatalogWindow(services.GetRequiredService<CatalogViewModel>(), services) { Owner = Application.Current?.MainWindow }.Show();
                return Task.CompletedTask;
            },
            Order: 5, Group: "1-library", Icon: IconKeys.Library));

        context.Ui.RegisterCommand(new CommandDescriptor(
            "Library.Settings", "Настройки библиотеки…", CommandPlacement.Toolbar,
            _ =>
            {
                LibrarySettingsWindow.ShowDialog(services, Application.Current?.MainWindow);
                return Task.CompletedTask;
            },
            Order: 10, Group: "1-library", Icon: IconKeys.Settings));

        context.Ui.RegisterCommand(new CommandDescriptor(
            "Library.Backups", "Резервные копии…", CommandPlacement.Toolbar,
            _ =>
            {
                new BackupWindow(services.GetRequiredService<BackupViewModel>(), services) { Owner = Application.Current?.MainWindow }.Show();
                return Task.CompletedTask;
            },
            Order: 15, Group: "1-library", Icon: IconKeys.Database));

        ScheduleBackup(services);
    }

    /// <summary>
    /// Копия по расписанию: через минуту после запуска Explorer, если последняя копия старше интервала.
    /// В фоне: ошибка только записывается в журнал (база могла быть ещё не настроена).
    /// </summary>
    private void ScheduleBackup(IServiceProvider services)
    {
        var database = services.GetRequiredService<PdmLite.Data.IPdmDatabase>();
        var backup = services.GetRequiredService<LibraryBackupService>();
        var logger = services.GetRequiredService<Microsoft.Extensions.Logging.ILogger<LibraryModule>>();
        _backupStop = new CancellationTokenSource();
        var token = _backupStop.Token;
        _backupTask = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(1), token);
                if (database.IsConfigured && backup.IsScheduledDue())
                    await backup.CreateAsync(PdmLite.Library.Backup.BackupReason.Scheduled, ct: token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex)
            {
                Microsoft.Extensions.Logging.LoggerExtensions.LogWarning(logger, ex, "Резервная копия по расписанию не сделана");
            }
        });
    }

    public async ValueTask ShutdownAsync()
    {
        var errors = new List<Exception>();
        try { Shutdown(); } catch (Exception ex) { errors.Add(ex); }
        try { if (_backupTask is not null) await _backupTask.ConfigureAwait(false); }
        catch (Exception ex) { errors.Add(ex); }
        finally { _backupStop?.Dispose(); _backupStop = null; }
        if (errors.Count > 0) throw new AggregateException(errors);
    }

    public void Shutdown()
    {
        var errors = new List<Exception>();
        foreach (var stop in new Action[] { () => _backupStop?.Cancel(), () => _panel?.Detach(), () => _contentCenter?.Dispose(), () => _resolver?.Dispose() })
            try { stop(); } catch (Exception ex) { errors.Add(ex); }
        if (errors.Count > 0) throw new AggregateException(errors);
    }
}
