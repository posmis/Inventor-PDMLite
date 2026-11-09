namespace PdmLite.Core.UI;

public sealed class UiRegistry : IUiRegistry
{
    private readonly List<PanelDescriptor> _panels = [];
    private readonly List<CommandDescriptor> _commands = [];

    public IReadOnlyList<PanelDescriptor> Panels => _panels;
    public IReadOnlyList<CommandDescriptor> Commands => _commands;

    public void RegisterPanel(PanelDescriptor panel)
    {
        if (_panels.Any(p => p.Id == panel.Id))
            throw new InvalidOperationException($"Панель '{panel.Id}' уже зарегистрирована.");
        _panels.Add(panel);
    }

    public void RegisterCommand(CommandDescriptor command)
    {
        if (_commands.Any(c => c.Id == command.Id))
            throw new InvalidOperationException($"Команда '{command.Id}' уже зарегистрирована.");
        _commands.Add(command);
    }

    public void UnregisterPanel(string id) => _panels.RemoveAll(p => p.Id == id);
    public void UnregisterCommand(string id) => _commands.RemoveAll(c => c.Id == id);

    public IEnumerable<CommandDescriptor> GetCommands(CommandPlacement placement) =>
        _commands.Where(c => c.Placement.HasFlag(placement)).OrderBy(c => c.Group).ThenBy(c => c.Order);
}
