using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using PdmLite.UI.Shared;

namespace PdmLite.Modules.Rename;

/// <summary>Окно «Обновить связи проекта».</summary>
public partial class RepairWindow : Window
{
    private readonly RepairViewModel _viewModel;

    public RepairWindow(RepairViewModel viewModel)
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
        Loaded += async (_, _) => await viewModel.FindCommand.ExecuteAsync(null);
    }

    private BrokenLinkRow? Selected => Grid.SelectedItem as BrokenLinkRow;

    private void Choose(BrokenLinkRow row)
    {
        var dialog = new OpenFileDialog
        {
            Title = $"Где теперь «{row.Name}»",
            InitialDirectory = _viewModel.Root,
            Filter = $"{row.Name}|*{Path.GetExtension(row.Name)}|Все файлы|*.*",
        };
        if (dialog.ShowDialog(this) != true)
            return;
        if (!dialog.FileName.StartsWith(_viewModel.Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            UiMessages.Error("Выберите файл внутри папки проекта:" + Environment.NewLine + _viewModel.Root);
            return;
        }
        if (!row.Candidates.Contains(dialog.FileName, StringComparer.OrdinalIgnoreCase))
            row.Candidates.Add(dialog.FileName);
        row.Chosen = row.Candidates.First(c => c.Equals(dialog.FileName, StringComparison.OrdinalIgnoreCase));
    }

    private void ChooseFile_OnClick(object sender, RoutedEventArgs e)
    {
        if (Selected is { } row)
            Choose(row);
    }

    private void ChooseFileRow_OnClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is BrokenLinkRow row)
            Choose(row);
    }

    private void Skip_OnClick(object sender, RoutedEventArgs e)
    {
        if (Selected is { } row)
            row.Chosen = null;
    }

    private void ShowChosen_OnClick(object sender, RoutedEventArgs e)
    {
        if (Selected is { Chosen: { } path })
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true })?.Dispose();
    }

    private void CopyName_OnClick(object sender, RoutedEventArgs e)
    {
        if (Selected is { } row)
            UiClipboard.SetText(row.Name);
    }

    private void Grid_OnPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Правый щелчок выделяет строку — меню относится к ней.
        var element = e.OriginalSource as DependencyObject;
        while (element is not null and not DataGridRow)
            element = element is Visual ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
        if (element is DataGridRow row)
            row.IsSelected = true;
    }

    private void Close_OnClick(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_viewModel.IsBusy && _viewModel.Summary is null)
            e.Cancel = true;
        base.OnClosing(e);
    }
}
