using System.Windows;
using PdmLite.UI.Shared;

namespace PdmLite.Modules.Library.Checks;

public partial class AssemblyCheckWindow : Window
{
    public AssemblyCheckWindow(AssemblyCheckViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.Ask = window =>
        {
            window.Owner = this;
            return window.ShowDialog() == true;
        };
        Loaded += async (_, _) => await viewModel.RunAsync();
    }

    private IEnumerable<CheckRow> SelectedRows => Grid.SelectedItems.OfType<CheckRow>().ToList();

    private void ChooseSelected_OnClick(object sender, RoutedEventArgs e)
    {
        if (Grid.SelectedItem is CheckRow row)
            Choose(row);
    }

    private void Mark_OnClick(object sender, RoutedEventArgs e)
    {
        foreach (var row in SelectedRows.Where(r => r.CanReplace))
            row.Replace = true;
    }

    private void Unmark_OnClick(object sender, RoutedEventArgs e)
    {
        foreach (var row in SelectedRows)
            row.Replace = false;
    }

    private void ShowFile_OnClick(object sender, RoutedEventArgs e)
    {
        if (Grid.SelectedItem is CheckRow row)
            UiClipboard.ShowInExplorer(row.FilePath);
    }

    private void CopyDesignation_OnClick(object sender, RoutedEventArgs e) =>
        UiClipboard.SetText(string.Join(Environment.NewLine, SelectedRows.Select(r => r.Designation)));

    /// <summary>Ручной выбор замены для строки — из всей библиотеки, с поиском.</summary>
    private void Choose_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: CheckRow row })
            Choose(row);
    }

    private void Choose(CheckRow row)
    {
        if (DataContext is not AssemblyCheckViewModel vm)
            return;
        var picker = new LibraryPickerWindow(vm.Library, row.Designation, row.SearchHint) { Owner = this };
        if (picker.ShowDialog() == true && picker.Selected is { } item)
            row.Choose(item);
    }

    private void CopyMissing_OnClick(object sender, RoutedEventArgs e)
    {
        var names = Grid.Items.OfType<CheckRow>().Where(r => r.Check.Finding == PdmLite.Library.Checks.ComponentFinding.MissingFile)
            .Select(r => r.FileName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (names.Count == 0)
            UiMessages.Info("Ненайденных файлов в сборке нет.");
        else
            UiClipboard.SetText(string.Join(Environment.NewLine, names));
    }

    private void Close_OnClick(object sender, RoutedEventArgs e) => Close();
}
