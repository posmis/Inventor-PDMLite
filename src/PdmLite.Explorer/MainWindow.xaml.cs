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
            DetailsBorder.Visibility = Visibility.Collapsed;
            DetailsColumn.Width = new GridLength(0);
            DetailsColumn.MinWidth = 0;
        }
        DetailsHost.Content = details;

        // Группы отражают задачи пользователя, а не порядок загрузки модулей.
        BuildToolbar();
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

        ToolsMenu.Items.Clear();
        foreach (var group in FunctionalCommands())
        {
            var menu = new MenuItem { Header = group.Key };
            foreach (var command in group)
                menu.Items.Add(CommandItem(command));
            ToolsMenu.Items.Add(menu);
        }
        if (ToolsMenu.Items.Count == 0)
            ToolsMenu.Items.Add(new MenuItem { Header = "(нет команд)", IsEnabled = false });
    }

    private static string FunctionalGroup(CommandDescriptor command) => command.Id switch
    {
        "InventorLink.OpenActiveProject" or "References.ProjectHealth" => "Проект",
        "Library.Settings" or "Library.Backups" => "Сервис",
        _ when command.Id.StartsWith("Library.", StringComparison.Ordinal) => "Библиотека",
        _ when command.Id.StartsWith("References.", StringComparison.Ordinal)
            || command.Id.StartsWith("Rename.", StringComparison.Ordinal) => "Связи проекта",
        _ => "Сервис",
    };

    private IEnumerable<IGrouping<string, CommandDescriptor>> FunctionalCommands() =>
        _viewModel.Host.Ui.GetCommands(CommandPlacement.Toolbar)
            .GroupBy(FunctionalGroup)
            .OrderBy(g => g.Key switch { "Проект" => 0, "Библиотека" => 1, "Связи проекта" => 2, _ => 3 });

    private static MenuItem CommandItem(CommandDescriptor command) => new()
    {
        Header = command.Title,
        Icon = PdmIcons.Image(command.Icon),
        Command = new DescriptorCommand(command, () => CommandContext.Empty),
    };

    private void BuildToolbar()
    {
        foreach (var group in FunctionalCommands())
        {
            if (group.Key == "Проект")
            {
                foreach (var command in group)
                    ProjectToolbar.Children.Add(CommandMenuBuilder.ToolButton(command.Title, command.Icon,
                        new DescriptorCommand(command, () => CommandContext.Empty), showText: true));
                continue;
            }

            var buttons = new WrapPanel { MaxWidth = 420 };
            if (group.Key == "Сервис")
            {
                var menu = new ContextMenu();
                foreach (var command in group)
                    menu.Items.Add(CommandItem(command));
                var button = CommandMenuBuilder.ToolButton("Обслуживание ▾", IconKeys.Settings,
                    null, showText: true);
                button.ContextMenu = menu;
                button.Click += (_, _) =>
                {
                    menu.PlacementTarget = button;
                    menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
                    menu.IsOpen = true;
                };
                buttons.Children.Add(button);
            }
            else
            {
                foreach (var command in group)
                    buttons.Children.Add(CommandMenuBuilder.ToolButton(command.Title, command.Icon,
                        new DescriptorCommand(command, () => CommandContext.Empty), showText: true));
            }

            Toolbar.Children.Add(new GroupBox
            {
                Header = group.Key,
                Content = buttons,
                Margin = new Thickness(0, 0, 8, 0),
                Padding = new Thickness(4, 2, 4, 4),
                VerticalAlignment = VerticalAlignment.Top,
            });
        }
    }

    private void Modules_OnClick(object sender, RoutedEventArgs e) =>
        new ModulesWindow(_viewModel.Host.Modules) { Owner = this }.ShowDialog();

    private void About_OnClick(object sender, RoutedEventArgs e) =>
        UiMessages.Info($"PdmLite Explorer {typeof(App).Assembly.GetName().Version}\nПросмотр проекта Inventor.");

    private void Exit_OnClick(object sender, RoutedEventArgs e) => Close();
}
