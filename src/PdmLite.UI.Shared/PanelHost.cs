using System.Windows;
using System.Windows.Controls;
using PdmLite.Core.UI;

namespace PdmLite.UI.Shared;

/// <summary>Размещение панелей модулей в интерфейсе хоста.</summary>
public static class PanelHost
{
    /// <summary>
    /// Создаёт элемент для панелей одного места: одна панель — как есть,
    /// несколько — вкладками, ни одной — <c>null</c>.
    /// </summary>
    public static FrameworkElement? Create(IUiRegistry registry, PanelPlacement placement, IServiceProvider services)
    {
        var panels = registry.Panels.Where(p => p.Placement == placement).OrderBy(p => p.Order).ToList();
        switch (panels.Count)
        {
            case 0:
                return null;
            case 1:
                return CreateView(panels[0], services);
            default:
                var tabs = new TabControl();
                foreach (var panel in panels)
                    tabs.Items.Add(new TabItem { Header = Header(panel), Content = CreateView(panel, services) });
                return tabs;
        }
    }

    private static object Header(PanelDescriptor panel)
    {
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        if (PdmIcons.Image(panel.Icon) is { } icon)
        {
            icon.Margin = new Thickness(0, 0, 5, 0);
            header.Children.Add(icon);
        }
        header.Children.Add(new TextBlock { Text = panel.Title, VerticalAlignment = VerticalAlignment.Center });
        return header;
    }

    private static FrameworkElement CreateView(PanelDescriptor panel, IServiceProvider services)
    {
        try
        {
            return panel.CreateView(services) as FrameworkElement
                   ?? throw new InvalidOperationException($"Панель '{panel.Id}' вернула не FrameworkElement.");
        }
        catch (Exception ex)
        {
            return new TextBlock
            {
                Text = $"Панель «{panel.Title}» не загружена:\n{ex.Message}",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(8),
            };
        }
    }
}
