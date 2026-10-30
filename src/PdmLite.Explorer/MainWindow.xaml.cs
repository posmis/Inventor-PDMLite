using System.Windows;
using System.Windows.Controls;
using PdmLite.Core.UI;
using PdmLite.UI.Shared;

namespace PdmLite.Explorer;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = _viewModel = viewModel;

        var host = viewModel.Host;
        NavigatorHost.Content = PanelHost.Create(host.Ui, PanelPlacement.Navigator, host.Services)
                                ?? new TextBlock { Text = "Модуль дерева проекта отключён.", Margin = new Thickness(10) };

        var details = PanelHost.Create(host.Ui, PanelPlacement.Details, host.Services);
        if (details is null)
        {
            Splitter.Visibility = Visibility.Collapsed;
            DetailsColumn.Width = new GridLength(0);
            DetailsColumn.MinWidth = 0;
        }
        DetailsHost.Content = details;
    }

    /// <summary>Показывает окно поверх остальных (по запросу из Inventor).</summary>
    public void BringToFront()
    {
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    private void ToolsMenu_OnSubmenuOpened(object sender, RoutedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, ToolsMenu))
            return;

        var items = CommandMenuBuilder.Build(_viewModel.Host.Ui, CommandPlacement.Toolbar, () => CommandContext.Empty);
        ToolsMenu.Items.Clear();
        if (items.Count == 0)
            ToolsMenu.Items.Add(new MenuItem { Header = "(нет команд)", IsEnabled = false });
        foreach (var item in items)
            ToolsMenu.Items.Add(item);
    }

    private void Modules_OnClick(object sender, RoutedEventArgs e) =>
        new ModulesWindow(_viewModel.Host.Modules) { Owner = this }.ShowDialog();

    private void About_OnClick(object sender, RoutedEventArgs e) =>
        UiMessages.Info($"PdmLite Explorer {typeof(App).Assembly.GetName().Version}\nПросмотр проекта Inventor.");

    private void Exit_OnClick(object sender, RoutedEventArgs e) => Close();
}
