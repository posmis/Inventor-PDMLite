using System.Windows;
using System.Windows.Controls;
using PdmLite.Core.UI;

namespace PdmLite.UI.Shared;

/// <summary>Построение пунктов меню из команд модулей.</summary>
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

            var current = context();
            var item = new MenuItem
            {
                Header = command.Title,
                IsEnabled = command.IsAvailable(current),
                FontWeight = command.IsDefault ? FontWeights.Bold : FontWeights.Normal,
            };
            item.Click += (_, _) => _ = RunAsync(command, context());
            items.Add(item);
        }

        return items;
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
