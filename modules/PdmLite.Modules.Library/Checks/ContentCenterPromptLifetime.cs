namespace PdmLite.Modules.Library.Checks;

/// <summary>Отложенные вызовы не стартуют после остановки; выполняющиеся завершаются до освобождения COM.</summary>
internal sealed class ContentCenterPromptLifetime : IDisposable, IAsyncDisposable
{
    private readonly object gate = new();
    private readonly CancellationTokenSource stop = new();
    private readonly HashSet<Task> pending = [];
    private Task? stopping;
    private bool disposed;

    public Task RunAsync(Func<CancellationToken, Task> work)
    {
        TaskCompletionSource done;
        lock (gate)
        {
            if (disposed) return Task.CompletedTask;
            done = new(TaskCreationOptions.RunContinuationsAsynchronously);
            pending.Add(done.Task);
        }
        _ = CompleteAsync(work, done);
        return done.Task;
    }
    private async Task CompleteAsync(Func<CancellationToken, Task> work, TaskCompletionSource done)
    {
        try { stop.Token.ThrowIfCancellationRequested(); await work(stop.Token); done.TrySetResult(); }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { done.TrySetResult(); }
        catch (Exception ex) { done.TrySetException(ex); }
        finally { lock (gate) pending.Remove(done.Task); }
    }
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            var active = pending.ToArray();
            stopping = FinishAsync(active);
        }
    }
    private async Task FinishAsync(Task[] active)
    {
        // CancelAsync не запускает произвольные callbacks под gate синхронно.
        var errors = new List<Exception>();
        try { await stop.CancelAsync().ConfigureAwait(false); }
        catch (Exception ex) { errors.Add(ex); }
        try { await Task.WhenAll(active).ConfigureAwait(false); }
        catch (Exception ex) { errors.Add(ex); }
        finally { stop.Dispose(); }
        if (errors.Count > 0) throw new AggregateException(errors);
    }
    public ValueTask DisposeAsync() { Dispose(); lock (gate) return new ValueTask(stopping!); }
}
