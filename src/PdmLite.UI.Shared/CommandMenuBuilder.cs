using System.Windows;
using System.Windows.Controls;
using System.Windows.Shapes;
using PdmLite.Core.UI;

namespace PdmLite.UI.Shared;

/// <summary>Построение меню и панелей инструментов из команд модулей.</summary>
public static class CommandMenuBuilder
{
    /// <summary>
    /// Пункты меню для команд указанного места. Между группами ставится разделитель.
    /// Недоступные команды показываются неактивными.
    /// </summary>
    public static IReadOnlyList<Control> Build(IUiRegistry registry, CommandPlacement placement, Func<CommandContext> context)
    {
        var items = new List<Control>();
        string? group = null;

        foreach (var command in registry.GetCommands(placement))
        {
            if (group is not null && group != command.Group)
                items.Add(new Separator());
            group = command.Group;

            items.Add(new MenuItem
            {
                Header = command.Title,
                Icon = PdmIcons.Image(command.Icon),
                Command = new DescriptorCommand(command, context),
                FontWeight = command.IsDefault ? FontWeights.Bold : FontWeights.Normal,
            });
        }

        return items;
    }

    /// <summary>
    /// Кнопки панели инструментов (иконка и подпись) для команд указанного места;
    /// между группами — вертикальный разделитель.
    /// </summary>
    public static IReadOnlyList<FrameworkElement> BuildToolbar(IUiRegistry registry, CommandPlacement placement, Func<CommandContext> context)
    {
        var items = new List<FrameworkElement>();
        string? group = null;

        foreach (var command in registry.GetCommands(placement))
        {
            if (group is not null && group != command.Group)
                items.Add(ToolSeparator());
            group = command.Group;
            items.Add(ToolButton(command.Title, command.Icon, new DescriptorCommand(command, context), showText: true));
        }

        return items;
    }

    /// <summary>Плоская кнопка панели инструментов в стиле <c>Pdm.ToolButton</c>.</summary>
    public static Button ToolButton(string title, string? icon, System.Windows.Input.ICommand? command, bool showText)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        if (PdmIcons.Image(icon) is { } image)
            content.Children.Add(image);
        if (showText || content.Children.Count == 0)
            content.Children.Add(new TextBlock { Text = title, Margin = new Thickness(content.Children.Count > 0 ? 5 : 0, 0, 2, 0), VerticalAlignment = VerticalAlignment.Center });

        var button = new Button { Content = content, Command = command, ToolTip = title };
        button.SetResourceReference(FrameworkElement.StyleProperty, "Pdm.ToolButton");
        return button;
    }

    public static FrameworkElement ToolSeparator()
    {
        var separator = new Rectangle();
        separator.SetResourceReference(FrameworkElement.StyleProperty, "Pdm.ToolSeparator");
        return separator;
    }

    /// <summary>Выполняет команду и показывает ошибку пользователю.</summary>
    public static async Task RunAsync(CommandDescriptor command, CommandContext context)
    {
        if (!command.IsAvailable(context))
            return;
        try
        {
            await command.Execute(context);
        }
        catch (Exception ex)
        {
            UiMessages.Error($"Не удалось выполнить «{command.Title}».", ex);
        }
    }
}
