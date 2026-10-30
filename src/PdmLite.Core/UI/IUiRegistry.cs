namespace PdmLite.Core.UI;

/// <summary>
/// Реестр элементов интерфейса, которые модули предоставляют хосту.
/// Хост сам решает, где и как их разместить.
/// </summary>
public interface IUiRegistry
{
    IReadOnlyList<PanelDescriptor> Panels { get; }
    IReadOnlyList<CommandDescriptor> Commands { get; }

    void RegisterPanel(PanelDescriptor panel);
    void RegisterCommand(CommandDescriptor command);

    /// <summary>Команды для указанного места, отсортированные по <see cref="CommandDescriptor.Order"/>.</summary>
    IEnumerable<CommandDescriptor> GetCommands(CommandPlacement placement);
}
