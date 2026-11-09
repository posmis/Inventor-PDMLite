namespace PdmLite.Core.Lifetime;

/// <summary>Owns partially initialized resources and unwinds them exactly once in reverse order.</summary>
public sealed class ResourceLifetime(Action<Exception>? onError = null) : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly List<Func<Task>> cleanup = [];
    private Task? stopping;

    public void Add(Func<Task> dispose)
    {
        lock (gate)
        {
            if (stopping is not null) throw new ObjectDisposedException(nameof(ResourceLifetime));
            cleanup.Add(dispose);
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (gate)
        {
            if (stopping is not null) return new(stopping);
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            stopping = done.Task;
            _ = CompleteAsync(cleanup.AsEnumerable().Reverse().ToArray(), done, onError);
            return new(stopping);
        }
    }

    private static async Task CompleteAsync(Func<Task>[] actions, TaskCompletionSource done, Action<Exception>? report)
    {
        var errors = new List<Exception>();
        foreach (var action in actions)
        {
            try { await action().ConfigureAwait(false); }
            catch (Exception ex) { errors.Add(ex); try { report?.Invoke(ex); } catch { } }
        }
        if (errors.Count > 0) done.TrySetException(new AggregateException("Не все ресурсы удалось освободить.", errors));
        else done.TrySetResult();
    }
}
