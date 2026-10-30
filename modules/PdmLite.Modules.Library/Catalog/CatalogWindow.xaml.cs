using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using Microsoft.Extensions.DependencyInjection;
using PdmLite.Data;
using PdmLite.Library.Storage;
using PdmLite.Modules.Library.Settings;
using PdmLite.UI.Shared;

namespace PdmLite.Modules.Library.Catalog;

public partial class CatalogWindow : Window
{
    private readonly CatalogViewModel _viewModel;
    private readonly IServiceProvider _services;

    public CatalogWindow(CatalogViewModel viewModel, IServiceProvider services)
    {
        InitializeComponent();
        DataContext = _viewModel = viewModel;
        _services = services;
        InputBindings.Add(new KeyBinding(viewModel.LoadCommand, Key.F5, ModifierKeys.None));
        Loaded += async (_, _) =>
        {
            if (!_services.GetRequiredService<IPdmDatabase>().IsConfigured
                && !LibrarySettingsWindow.ShowDialog(_services, this))
            {
                Close();
                return;
            }
            await _viewModel.LoadAsync();
        };
    }

    private async void Edit_OnClick(object sender, RoutedEventArgs e) => await EditSelectedAsync();

    private async void Grid_OnMouseDoubleClick(object sender, MouseButtonEventArgs e) => await EditSelectedAsync();

    private async Task EditSelectedAsync()
    {
        if (_viewModel is not { Selected: { } row, Catalog: { } catalog })
            return;
        var editor = new EditItemWindow(new EditItemViewModel(_viewModel.Service, catalog, row.Item)) { Owner = this };
        if (editor.ShowDialog() == true)
            await _viewModel.LoadAsync();
    }

    private async void Delete_OnClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.Selected is { } row)
            await _viewModel.DeleteAsync(row);
    }

    private void ShowInWindows_OnClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.Selected is not { } row)
            return;
        var root = _services.GetRequiredService<Func<LibraryStorage>>()();
        var path = root.GetFullPath(row.Item.RelativePath);
        if (!File.Exists(path))
        {
            UiMessages.Error($"Файл не найден в папке библиотеки:\n{path}");
            return;
        }
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true })?.Dispose();
    }

    private void Close_OnClick(object sender, RoutedEventArgs e) => Close();
}
