using System.Windows.Threading;
using PdmLite.Core.Threading;

namespace PdmLite.UI.Shared;

/// <summary>Поток интерфейса на основе WPF <see cref="Dispatcher"/>.</summary>
/// <remarks>Создавать в потоке интерфейса (внутри Inventor — в <c>Activate</c> плагина).</remarks>
public sealed class WpfUiDispatcher(Dispatcher dispatcher) : IUiDispatcher
{
    public WpfUiDispatcher() : this(Dispatcher.CurrentDispatcher)
    {
    }

    public bool CheckAccess() => dispatcher.CheckAccess();

    public void Post(Action action)
    {
        if (dispatcher.CheckAccess())
            action();
        else
            dispatcher.BeginInvoke(action);
    }

    public Task InvokeAsync(Action action) =>
        dispatcher.CheckAccess() ? RunInline(action) : dispatcher.InvokeAsync(action).Task;

    public Task<T> InvokeAsync<T>(Func<T> func) =>
        dispatcher.CheckAccess() ? RunInline(func) : dispatcher.InvokeAsync(func).Task;

    private static Task RunInline(Action action)
    {
        try
        {
            action();
            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            return Task.FromException(ex);
        }
    }

    private static Task<T> RunInline<T>(Func<T> func)
    {
        try
        {
            return Task.FromResult(func());
        }
        catch (Exception ex)
        {
            return Task.FromException<T>(ex);
        }
    }
}
