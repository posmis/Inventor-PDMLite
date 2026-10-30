using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
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
            await _viewModel.OnEditedAsync();
    }

    private async void Duplicate_OnClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel is not { Selected: { } row, Catalog: { } catalog })
        {
            UiMessages.Info("Выделите компонент, который нужно скопировать.");
            return;
        }
        var editor = new EditItemWindow(new EditItemViewModel(_viewModel.Service, catalog, row.Item, duplicate: true)) { Owner = this };
        if (editor.ShowDialog() == true)
            await _viewModel.OnEditedAsync();
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

    private async void Maintenance_OnClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.Catalog is not { } catalog)
            return;
        var selected = Grid.SelectedItems.OfType<CatalogRow>().Select(r => r.Item.Id).ToList();
        var window = new MaintenanceWindow(new MaintenanceViewModel(_viewModel.Service, catalog, selected, _viewModel.Items.Count)) { Owner = this };
        window.ShowDialog();
        if (window.Changed)
            await _viewModel.OnEditedAsync();
    }

    private void FolderTree_OnSelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is CatalogFolder folder)
            _viewModel.Folder = folder;
    }

    /// <summary>Правый щелчок по невыделенной строке выделяет её — меню относится к ней.</summary>
    private void Grid_OnPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var row = ItemsControl.ContainerFromElement(Grid, (DependencyObject)e.OriginalSource) as DataGridRow;
        if (row is null || row.IsSelected)
            return;
        Grid.SelectedItems.Clear();
        row.IsSelected = true;
        row.Focus();
    }

    /// <summary>Клавиши: Enter — изменить, Del — удалить, Ctrl+C — копировать обозначение.</summary>
    private async void Grid_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                await EditSelectedAsync();
                break;
            case Key.Delete:
                e.Handled = true;
                if (_viewModel.Selected is { } row)
                    await _viewModel.DeleteAsync(row);
                break;
            case Key.C when Keyboard.Modifiers == ModifierKeys.Control:
                e.Handled = true;
                CopyDesignation();
                break;
        }
    }

    private void CopyDesignation_OnClick(object sender, RoutedEventArgs e) => CopyDesignation();

    /// <summary>Обозначения выделенных компонентов — по строке на компонент.</summary>
    private void CopyDesignation()
    {
        var designations = Grid.SelectedItems.OfType<CatalogRow>().Select(r => r.Item.Designation).ToList();
        if (designations.Count == 0)
            return;
        try
        {
            Clipboard.SetText(string.Join(Environment.NewLine, designations));
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // Буфер обмена занят другой программой — не критично.
        }
    }

    private void Close_OnClick(object sender, RoutedEventArgs e) => Close();
}
