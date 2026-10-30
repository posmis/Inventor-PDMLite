using System.Windows;

namespace PdmLite.Modules.Library.Checks;

public partial class AssemblyCheckWindow : Window
{
    public AssemblyCheckWindow(AssemblyCheckViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Loaded += async (_, _) => await viewModel.RunAsync();
    }

    private void Close_OnClick(object sender, RoutedEventArgs e) => Close();
}
