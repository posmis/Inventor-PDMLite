using PdmLite.Core.Events;
using PdmLite.Core.UI;

namespace PdmLite.Hosting;

/// <summary>Tracks registrations made through a module context, including a failed Initialize.</summary>
internal sealed class ModuleResourceScope(IEventBus events, IUiRegistry ui) : IEventBus, IUiRegistry, IDisposable
{
    private readonly List<IDisposable> subscriptions = [];
    private readonly List<string> panels = [], commands = [];
    private bool disposed;
    private int active;
    private readonly List<Exception> errors = [];
    private readonly TaskCompletionSource drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task Completion => drained.Task;
    private bool Enter()
    {
        lock (subscriptions) { if (disposed) return false; active++; return true; }
    }
    private void Exit()
    {
        lock (subscriptions) { active--; CompleteIfDrained(); }
    }
    private void CompleteIfDrained()
    {
        if (!disposed || active != 0) return;
        if (errors.Count == 0) drained.TrySetResult(); else drained.TrySetException(new AggregateException(errors));
    }
    public IReadOnlyList<PanelDescriptor> Panels => ui.Panels;
    public IReadOnlyList<CommandDescriptor> Commands => ui.Commands;
    public void Publish<T>(T value) where T : notnull
    {
        if (!Enter()) return;
        try { events.Publish(value); } finally { Exit(); }
    }
    public IDisposable Subscribe<T>(Action<T> handler) where T : notnull
    {
        lock (subscriptions)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var subscription = events.Subscribe<T>(value => { if (!Enter()) return; try { handler(value); } finally { Exit(); } });
            subscriptions.Add(subscription);
            return subscription;
        }
    }
    public void RegisterPanel(PanelDescriptor panel)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ui.RegisterPanel(panel with { CreateView = services =>
        {
            if (!Enter()) throw new ObjectDisposedException(nameof(ModuleResourceScope));
            try { return panel.CreateView(services); } finally { Exit(); }
        } });
        panels.Add(panel.Id);
    }
    public void RegisterCommand(CommandDescriptor command)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ui.RegisterCommand(command with
        {
            CanExecute = context => !Volatile.Read(ref disposed) && command.IsAvailable(context),
            Execute = async context =>
            {
                if (!Enter()) throw new ObjectDisposedException(nameof(ModuleResourceScope));
                try { await command.Execute(context); } finally { Exit(); }
            },
        });
        commands.Add(command.Id);
    }
    public void UnregisterPanel(string id) { if (panels.Remove(id)) ui.UnregisterPanel(id); }
    public void UnregisterCommand(string id) { if (commands.Remove(id)) ui.UnregisterCommand(id); }
    public IEnumerable<CommandDescriptor> GetCommands(CommandPlacement placement) => ui.GetCommands(placement);
    public void Dispose()
    {
        lock (subscriptions)
        {
            if (disposed) return; disposed = true;
            foreach (var subscription in subscriptions)
                try { subscription.Dispose(); } catch (Exception ex) { errors.Add(ex); }
            subscriptions.Clear();
            foreach (var id in commands)
                try { ui.UnregisterCommand(id); } catch (Exception ex) { errors.Add(ex); }
            foreach (var id in panels)
                try { ui.UnregisterPanel(id); } catch (Exception ex) { errors.Add(ex); }
            commands.Clear(); panels.Clear();
            CompleteIfDrained();
        }
    }
}
