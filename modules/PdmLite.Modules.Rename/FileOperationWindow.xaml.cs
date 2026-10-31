using System.Diagnostics;
using System.Windows;
using Microsoft.Win32;
using PdmLite.UI.Shared;

namespace PdmLite.Modules.Rename;

/// <summary>Окно «Переименовать» / «Переместить»: что изменится — перед записью, с подтверждением.</summary>
public partial class FileOperationWindow : Window
{
    private readonly FileOperationViewModel _viewModel;

    public FileOperationWindow(FileOperationViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        viewModel.Confirm = text => MessageBox.Show(this, text, UiMessages.Caption, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
        viewModel.Completed += (_, _) =>
        {
            Close();
            if (viewModel.Summary is { } summary)
                UiMessages.Info(summary);
        };
        if (viewModel.IsMove || viewModel.IsFolderRename)
            ToFolderColumn.Visibility = Visibility.Visible;
        Loaded += async (_, _) =>
        {
            await viewModel.InitializeAsync();
            if (viewModel.IsFolderRename)
            {
                FolderNameBox.Focus();
                FolderNameBox.SelectAll();
            }
            else if (viewModel.IsRename)
            {
                DesignationBox.Focus();
                DesignationBox.SelectAll();
            }
            else if (viewModel.TargetFolder.Length == 0)
            {
                Browse();
            }
        };
    }

    private void Browse()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Куда переместить (внутри папки проекта)",
            InitialDirectory = Directory.Exists(_viewModel.TargetFolder) ? _viewModel.TargetFolder : _viewModel.Root,
        };
        if (dialog.ShowDialog(this) != true)
            return;
        var folder = Path.TrimEndingDirectorySeparator(dialog.FolderName);
        if (!(folder + Path.DirectorySeparatorChar).StartsWith(_viewModel.Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            UiMessages.Error("Перемещать можно только внутри папки проекта:" + Environment.NewLine + _viewModel.Root);
            return;
        }
        _viewModel.TargetFolder = folder;
    }

    private void Browse_OnClick(object sender, RoutedEventArgs e) => Browse();

    private IEnumerable<FileRow> Selected => FilesGrid.SelectedItems.OfType<FileRow>().ToList();

    private void IncludeSelected_OnClick(object sender, RoutedEventArgs e)
    {
        foreach (var row in Selected)
            row.Include = true;
    }

    private void ExcludeSelected_OnClick(object sender, RoutedEventArgs e)
    {
        foreach (var row in Selected)
            row.Include = false;
    }

    private void ShowFile_OnClick(object sender, RoutedEventArgs e)
    {
        if (Selected.FirstOrDefault() is { } row)
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{row.From}\"") { UseShellExecute = true })?.Dispose();
    }

    private void CopyPath_OnClick(object sender, RoutedEventArgs e) =>
        UiClipboard.SetText(string.Join(Environment.NewLine, Selected.Select(r => r.From)));

    private void Close_OnClick(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // Во время записи окно не закрывается — иначе не будет видно, чем закончилось.
        if (_viewModel.IsBusy && _viewModel.Summary is null)
            e.Cancel = true;
        base.OnClosing(e);
    }
}
