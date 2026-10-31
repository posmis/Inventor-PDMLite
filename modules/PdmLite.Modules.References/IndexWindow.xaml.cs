using System.Windows;

namespace PdmLite.Modules.References;

public partial class IndexWindow : Window
{
    private readonly IndexViewModel _viewModel;

    public IndexWindow(IndexViewModel viewModel)
    {
        InitializeComponent();
        DataContext = _viewModel = viewModel;
        Loaded += async (_, _) => await _viewModel.ScanAsync();
        // Закрыть окно посреди сканирования — остановить его (прочитанное сохранено).
        Closing += (_, _) =>
        {
            if (_viewModel.StopCommand.CanExecute(null))
                _viewModel.StopCommand.Execute(null);
        };
    }

    private void Close_OnClick(object sender, RoutedEventArgs e) => Close();
}
