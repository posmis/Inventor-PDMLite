using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace PdmLite.Modules.Library.Catalog;

public partial class MaintenanceWindow : Window
{
    private readonly MaintenanceViewModel _viewModel;

    public MaintenanceWindow(MaintenanceViewModel viewModel)
    {
        InitializeComponent();
        DataContext = _viewModel = viewModel;
        _viewModel.Ask = window =>
        {
            window.Owner = this;
            return window.ShowDialog() == true;
        };
        // Закрыть окно посреди операции — значит остановить её.
        Closing += (_, _) =>
        {
            if (_viewModel.StopCommand.CanExecute(null))
                _viewModel.StopCommand.Execute(null);
        };
    }

    public bool Changed => _viewModel.Changed;

    private void Close_OnClick(object sender, RoutedEventArgs e) => Close();
}

/// <summary>Логическое «не» для пары переключателей.</summary>
public sealed class InverseBool : IValueConverter
{
    public static InverseBool Instance { get; } = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
}

/// <summary>Логические значения для параметров команд в XAML.</summary>
public static class BoolBox
{
    public static readonly object True = true;
    public static readonly object False = false;
}
