using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using PdmLite.Data;
using PdmLite.Modules.Library.Settings;
using PdmLite.UI.Shared;

namespace PdmLite.Modules.Library.Backup;

public partial class BackupWindow : Window
{
    private readonly BackupViewModel _viewModel;

    public BackupWindow(BackupViewModel viewModel, IServiceProvider services)
    {
        InitializeComponent();
        DataContext = _viewModel = viewModel;
        _viewModel.Ask = window =>
        {
            window.Owner = this;
            return window.ShowDialog() == true;
        };
        Loaded += (_, _) =>
        {
            if (!services.GetRequiredService<IPdmDatabase>().IsConfigured && !LibrarySettingsWindow.ShowDialog(services, this))
            {
                Close();
                return;
            }
            _viewModel.Refresh();
        };
    }

    private async void RestoreFromFile_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Резервная копия библиотеки",
            Filter = "Резервная копия PdmLite (*.zip)|*.zip",
            InitialDirectory = Directory.Exists(_viewModel.Folder) ? _viewModel.Folder : null,
        };
        if (dialog.ShowDialog(this) == true)
            await _viewModel.RestoreFromAsync(dialog.FileName);
    }

    private void Browse_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Папка резервных копий", InitialDirectory = Directory.Exists(_viewModel.Folder) ? _viewModel.Folder : null };
        if (dialog.ShowDialog(this) == true)
            _viewModel.Folder = dialog.FolderName;
    }

    private void SaveSettings_OnClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SaveSettings() is { } error)
            UiMessages.Error(error);
        else
            UiMessages.Info("Настройки резервных копий сохранены.");
    }

    private void Close_OnClick(object sender, RoutedEventArgs e) => Close();
}
