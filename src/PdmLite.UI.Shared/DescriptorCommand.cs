using System.Windows.Input;
using PdmLite.Core.UI;

namespace PdmLite.UI.Shared;

/// <summary>
/// <see cref="ICommand"/> поверх команды модуля: WPF сам обновляет доступность
/// кнопок и пунктов меню (<see cref="CommandManager.RequerySuggested"/>).
/// </summary>
public sealed class DescriptorCommand(CommandDescriptor descriptor, Func<CommandContext> context) : ICommand
{
    public CommandDescriptor Descriptor => descriptor;

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => descriptor.IsAvailable(context());

    public void Execute(object? parameter) => _ = CommandMenuBuilder.RunAsync(descriptor, context());
}
