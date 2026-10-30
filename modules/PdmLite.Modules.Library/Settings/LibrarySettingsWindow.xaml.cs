using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using PdmLite.UI.Shared;

namespace PdmLite.Modules.Library.Settings;

public partial class LibrarySettingsWindow : Window
{
    private readonly LibrarySettingsViewModel _viewModel;

    public LibrarySettingsWindow(LibrarySettingsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = _viewModel = viewModel;
    }

    /// <summary>Открывает окно настроек. <c>true</c> — настройки сохранены.</summary>
    public static bool ShowDialog(IServiceProvider services, Window? owner) =>
        new LibrarySettingsWindow(services.GetRequiredService<LibrarySettingsViewModel>()) { Owner = owner }.ShowDialog() == true;

    private void PasswordInput_OnPasswordChanged(object sender, RoutedEventArgs e) => _viewModel.Password = PasswordInput.Password;

    private void Browse_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Папка библиотеки", InitialDirectory = Directory.Exists(_viewModel.RootFolder) ? _viewModel.RootFolder : null };
        if (dialog.ShowDialog(this) == true)
            _viewModel.RootFolder = dialog.FolderName;
    }

    private void Save_OnClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.Save() is { } error)
        {
            UiMessages.Error(error);
            return;
        }
        DialogResult = true;
    }
}
