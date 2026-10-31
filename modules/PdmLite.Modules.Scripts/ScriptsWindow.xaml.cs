using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using PdmLite.Core.Scripts;
using PdmLite.UI.Shared;

namespace PdmLite.Modules.Scripts;

/// <summary>Окно «Утилиты»: список внешних утилит и запуск. Утилиты ничего не получают от PDM.</summary>
public partial class ScriptsWindow : Window
{
    private readonly string _folder;

    public ScriptsWindow(string folder, string? seedError = null)
    {
        InitializeComponent();
        _folder = folder;
        FolderText.Text = "Папка утилит: " + folder
                          + (seedError is null ? "" : $"   (поставляемые утилиты не обновлены: {seedError})");
        Load();
    }

    private void Load()
    {
        var scripts = ScriptCatalog.List(_folder);
        Grid.ItemsSource = scripts;
        EmptyText.Visibility = scripts.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Run()
    {
        if (Grid.SelectedItem is not ScriptInfo script)
        {
            UiMessages.Info("Выберите утилиту в списке.");
            return;
        }
        try
        {
            ScriptCatalog.Start(script);
        }
        catch (Exception ex)
        {
            UiMessages.Error($"Не удалось запустить «{script.Title}».", ex);
        }
    }

    private void Run_OnClick(object sender, RoutedEventArgs e) => Run();

    private void Grid_OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Grid.SelectedItem is ScriptInfo)
            Run();
    }

    private void ShowFile_OnClick(object sender, RoutedEventArgs e)
    {
        if (Grid.SelectedItem is ScriptInfo script)
            UiClipboard.ShowInExplorer(script.Path);
    }

    private void OpenFolder_OnClick(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_folder);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_folder}\"") { UseShellExecute = true })?.Dispose();
    }

    private void Refresh_OnClick(object sender, RoutedEventArgs e) => Load();

    private void Close_OnClick(object sender, RoutedEventArgs e) => Close();
}
