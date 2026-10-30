using System.Runtime.InteropServices;
using System.Windows.Forms.Integration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PdmLite.Core.Projects;
using PdmLite.Core.UI;
using PdmLite.Hosting;
using PdmLite.UI.Shared;
using Wpf = System.Windows;
using WpfControls = System.Windows.Controls;

namespace PdmLite.AddIn.Core;

/// <summary>Интерфейс плагина в Inventor: вкладка ленты и закрепляемое окно с панелями модулей.</summary>
internal sealed class AddInUi : IDisposable
{
    private const string TabId = "PdmLite.Tab";
    private const string PanelId = "PdmLite.Panel";
    private const string WindowId = "PdmLite.ProjectWindow";

    /// <summary>Ленты, на которые добавляется вкладка PdmLite.</summary>
    private static readonly string[] Ribbons = ["ZeroDoc", "Part", "Assembly", "Drawing", "Presentation"];

    private readonly PdmHost _host;
    private readonly Inv.Application _inventor;
    private readonly string _clientId;
    private readonly ILogger _logger;
    private readonly List<Inv.ButtonDefinition> _buttons = [];

    private Inv.DockableWindow? _window;
    private ElementHost? _elementHost;

    public AddInUi(PdmHost host, string clientId, ILogger logger)
    {
        _host = host;
        _inventor = host.Services.GetRequiredService<Inv.Application>();
        _clientId = clientId;
        _logger = logger;
    }

    public void Create(bool firstTime)
    {
        CreateWindow(firstTime);

        var toggle = AddButton("Дерево проекта", "PdmLite.ToggleWindow", "Показать или скрыть окно PdmLite",
            IconKeys.ProjectTree, ToggleWindow);
        var reveal = AddButton("Показать в Explorer", "PdmLite.RevealInExplorer", "Открыть активный документ в PdmLite Explorer",
            IconKeys.RevealInExplorer, RevealInExplorer);
        var refresh = AddButton("Обновить проект", "PdmLite.RefreshProject", "Перечитать дерево проекта",
            IconKeys.Refresh, RefreshProject);

        foreach (var ribbonName in Ribbons)
        {
            try
            {
                var ribbon = _inventor.UserInterfaceManager.Ribbons[ribbonName];
                var tab = FindTab(ribbon) ?? ribbon.RibbonTabs.Add("PdmLite", TabId, _clientId);
                var panel = FindPanel(tab) ?? tab.RibbonPanels.Add("PdmLite", PanelId, _clientId);
                foreach (var button in new[] { toggle, reveal, refresh })
                    if (!HasControl(panel, button.InternalName))
                        panel.CommandControls.AddButton(button, true, true);
            }
            catch (COMException ex)
            {
                _logger.LogWarning(ex, "Не удалось добавить вкладку на ленту {Ribbon}", ribbonName);
            }
        }
    }

    private void CreateWindow(bool firstTime)
    {
        var windows = _inventor.UserInterfaceManager.DockableWindows;
        _window = FindWindow(windows) ?? windows.Add(_clientId, WindowId, "PdmLite");

        _elementHost = new ElementHost { Dock = System.Windows.Forms.DockStyle.Fill, Child = BuildContent() };
        DockableWindowHost.AddChild(_window, _elementHost.Handle);

        // Положение окна Inventor запоминает сам; задаём его только при первой загрузке.
        if (firstTime)
        {
            _window.DockingState = Inv.DockingStateEnum.kDockRight;
            _window.Visible = true;
        }
    }

    /// <summary>
    /// Сверху — панели навигации, снизу — сведения о документе. Внутри Inventor нет
    /// WPF-приложения с общими ресурсами, поэтому тема подключается к корню окна.
    /// </summary>
    private Wpf.FrameworkElement BuildContent()
    {
        var navigator = PanelHost.Create(_host.Ui, PanelPlacement.Navigator, _host.Services);
        var details = PanelHost.Create(_host.Ui, PanelPlacement.Details, _host.Services);

        var grid = new WpfControls.Grid();
        grid.RowDefinitions.Add(new WpfControls.RowDefinition { Height = new Wpf.GridLength(3, Wpf.GridUnitType.Star) });
        grid.RowDefinitions.Add(new WpfControls.RowDefinition { Height = Wpf.GridLength.Auto });
        grid.RowDefinitions.Add(new WpfControls.RowDefinition { Height = new Wpf.GridLength(details is null ? 0 : 2, Wpf.GridUnitType.Star) });

        if (navigator is not null)
            grid.Children.Add(navigator);

        if (details is not null)
        {
            var splitter = new WpfControls.GridSplitter
            {
                Height = 5,
                HorizontalAlignment = Wpf.HorizontalAlignment.Stretch,
                ResizeDirection = WpfControls.GridResizeDirection.Rows,
            };
            WpfControls.Grid.SetRow(splitter, 1);
            WpfControls.Grid.SetRow(details, 2);
            grid.Children.Add(splitter);
            grid.Children.Add(details);
        }

        var root = new WpfControls.Border { Child = grid };
        root.Resources.MergedDictionaries.Add(ThemeResources.Create());
        root.SetResourceReference(Wpf.FrameworkElement.StyleProperty, "Pdm.Root");
        return root;
    }

    private Inv.ButtonDefinition AddButton(string title, string internalName, string description, string icon, Func<Task> action)
    {
        var definitions = _inventor.CommandManager.ControlDefinitions;
        var button = definitions.AddButtonDefinition(
            title, internalName, Inv.CommandTypesEnum.kQueryOnlyCmdType, _clientId, description, description,
            Icon(icon, 16), Icon(icon, 32));

        button.OnExecute += _ => Run(title, action);
        _buttons.Add(button);
        return button;
    }

    private object? Icon(string key, int size)
    {
        try
        {
            return RibbonIcons.Create(key, size);
        }
        catch (Exception ex)
        {
            // Без иконки кнопка всё равно работает (показывается текст).
            _logger.LogWarning(ex, "Не удалось создать иконку {Icon}", key);
            return null;
        }
    }

    private async void Run(string title, Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка команды {Command}", title);
            UiMessages.Error($"Не удалось выполнить «{title}».", ex);
        }
    }

    private Task ToggleWindow()
    {
        if (_window is not null)
            _window.Visible = !_window.Visible;
        return Task.CompletedTask;
    }

    private Task RevealInExplorer()
    {
        var document = InventorLinkServer.ActiveDocumentPath(_inventor);
        var project = _host.Services.GetRequiredService<IProjectSession>().Project;
        return _host.Services.GetRequiredService<ExplorerLauncher>()
            .RevealAsync(document, project?.ProjectFile ?? project?.RootFolder);
    }

    private Task RefreshProject() => _host.Services.GetRequiredService<IProjectSession>().RefreshAsync();

    private Inv.RibbonTab? FindTab(Inv.Ribbon ribbon)
    {
        foreach (Inv.RibbonTab tab in ribbon.RibbonTabs)
            if (tab.InternalName == TabId)
                return tab;
        return null;
    }

    private static Inv.RibbonPanel? FindPanel(Inv.RibbonTab tab)
    {
        foreach (Inv.RibbonPanel panel in tab.RibbonPanels)
            if (panel.InternalName == PanelId)
                return panel;
        return null;
    }

    private static bool HasControl(Inv.RibbonPanel panel, string internalName)
    {
        foreach (Inv.CommandControl control in panel.CommandControls)
            if (control.InternalName == internalName)
                return true;
        return false;
    }

    private static Inv.DockableWindow? FindWindow(Inv.DockableWindows windows)
    {
        foreach (Inv.DockableWindow window in windows)
            if (window.InternalName == WindowId)
                return window;
        return null;
    }

    public void Dispose()
    {
        // Удаление определений команд убирает и кнопки с ленты.
        foreach (var button in _buttons)
        {
            try
            {
                button.Delete();
            }
            catch (COMException ex)
            {
                _logger.LogWarning(ex, "Не удалось удалить команду {Command}", button.InternalName);
            }
        }
        _buttons.Clear();

        // Само закрепляемое окно не удаляем: Inventor хранит его положение и размер.
        // При повторной загрузке плагина окно будет найдено по InternalName.
        _window = null;

        _elementHost?.Dispose();
        _elementHost = null;
    }
}
