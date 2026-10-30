using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using PdmLite.Data;
using PdmLite.Modules.Library.Settings;
using PdmLite.UI.Shared;

namespace PdmLite.Modules.Library.Import;

public partial class ImportWindow : Window
{
    private readonly ImportViewModel _viewModel;
    private readonly IServiceProvider _services;

    public ImportWindow(ImportViewModel viewModel, IServiceProvider services)
    {
        InitializeComponent();
        DataContext = _viewModel = viewModel;
        _services = services;
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Без подключения к базе добавлять некуда — сначала настройки.
        if (_services.GetRequiredService<IPdmDatabase>().IsConfigured)
            return;
        UiMessages.Info("Сначала укажите подключение к базе данных и папку библиотеки.");
        if (!LibrarySettingsWindow.ShowDialog(_services, this))
            Close();
    }

    private IEnumerable<ImportRowViewModel> SelectedRows => Grid.SelectedItems.OfType<ImportRowViewModel>();

    private async void AddFiles_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Детали и сборки для библиотеки",
            Filter = "Детали и сборки Inventor (*.ipt;*.iam)|*.ipt;*.iam",
            Multiselect = true,
        };
        if (dialog.ShowDialog(this) == true)
            await _viewModel.AddAsync(dialog.FileNames);
    }

    private async void AddFolder_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Папка с компонентами (с подпапками)", Multiselect = true };
        if (dialog.ShowDialog(this) == true)
            await _viewModel.AddAsync(dialog.FolderNames);
    }

    private void Exclude_OnClick(object sender, RoutedEventArgs e) => _viewModel.SetInclude(SelectedRows, false);

    private void Include_OnClick(object sender, RoutedEventArgs e) => _viewModel.SetInclude(SelectedRows, true);

    private void Close_OnClick(object sender, RoutedEventArgs e) => Close();
}
