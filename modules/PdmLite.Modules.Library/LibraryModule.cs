using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PdmLite.Core.Modularity;
using PdmLite.Core.UI;
using PdmLite.Hosting;
using PdmLite.Library.Import;
using PdmLite.Library.Items;
using PdmLite.Library.Storage;
using PdmLite.Modules.Library.Catalog;
using PdmLite.Modules.Library.Import;
using PdmLite.Modules.Library.Settings;

namespace PdmLite.Modules.Library;

/// <summary>
/// Стандартная библиотека. Шаг Б1 — добавление, каталог (изменение, удаление) и настройки; работает в Explorer
/// (свойства файлов читаются через Apprentice). Каталог и вставка в Inventor — следующие шаги.
/// </summary>
public sealed class LibraryModule : IPdmModule
{
    public ModuleInfo Info { get; } = new("Library", "Стандартная библиотека", new Version(0, 1, 0), HostKind.Explorer);

    public void ConfigureServices(IServiceCollection services, HostKind host)
    {
        services.TryAddSingleton(_ => new JsonSettings<LibrarySettings>(LibrarySettings.Name));
        services.AddSingleton<Func<LibraryStorage>>(sp =>
        {
            var settings = sp.GetRequiredService<JsonSettings<LibrarySettings>>();
            return () => new LibraryStorage(settings.Value.RootFolder);
        });
        services.AddSingleton<LibraryImportService>();
        services.AddSingleton<LibraryItemService>();
        services.AddTransient<ImportViewModel>();
        services.AddTransient<CatalogViewModel>();
        services.AddTransient<LibrarySettingsViewModel>();
    }

    public void Initialize(IModuleContext context)
    {
        var services = context.Services;

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
    }

    public void Shutdown()
    {
    }
}
