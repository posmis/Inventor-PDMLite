using System.Windows;

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

    /// <summary>Ручной выбор замены для строки — из всей библиотеки, с поиском.</summary>
    private void Choose_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: CheckRow row } || DataContext is not AssemblyCheckViewModel vm)
            return;
        var picker = new LibraryPickerWindow(vm.Library, row.Designation, row.SearchHint) { Owner = this };
        if (picker.ShowDialog() == true && picker.Selected is { } item)
            row.Choose(item);
    }

    private void Close_OnClick(object sender, RoutedEventArgs e) => Close();
}
