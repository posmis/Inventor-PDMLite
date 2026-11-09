using System.Collections.Concurrent;

namespace PdmLite.Apprentice;

/// <summary>STA queue: callers can stop waiting while a native COM call unwinds on its owning thread.</summary>
internal sealed class StaWorker : IDisposable
{
    private interface IWork { void Run(); void Stop(); }
    private sealed class Work<T> : IWork
    {
        private readonly Func<T> function;
        private readonly TaskCompletionSource<T> source = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenRegistration registration;
        public Task<T> Task => source.Task;
        public Work(Func<T> function, CancellationToken ct)
        {
            this.function = function;
            registration = ct.Register(() => source.TrySetCanceled(ct));
        }
        public void Run()
        {
            try { if (!source.Task.IsCompleted) source.TrySetResult(function()); }
            catch (Exception ex) { source.TrySetException(ex); }
            finally { registration.Dispose(); }
        }
        public void Stop() => source.TrySetException(new ObjectDisposedException(nameof(StaWorker)));
    }
    private readonly BlockingCollection<IWork> queue = new();
    private readonly HashSet<IWork> pending = [];
    private readonly object gate = new();
    private readonly Thread thread;
    private readonly TimeSpan timeout;
    private readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool stopping;
    private Action? finalizer;
    public Task Completion => completion.Task;

    public StaWorker(string name) : this(name, TimeSpan.FromSeconds(5)) { }
    internal StaWorker(string name, TimeSpan timeout)
    {
        this.timeout = timeout;
        thread = new Thread(Run) { IsBackground = true, Name = name };
        if (OperatingSystem.IsWindows()) thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }
    public Task<T> InvokeAsync<T>(Func<T> function, CancellationToken ct = default)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(stopping, this);
            var work = new Work<T>(function, ct);
            pending.Add(work); queue.Add(work);
            return work.Task;
        }
    }
    public Task InvokeAsync(Action action, CancellationToken ct = default) => InvokeAsync(() => { action(); return true; }, ct);
    private void Run()
    {
        Exception? failure = null;
        try
        {
            foreach (var work in queue.GetConsumingEnumerable())
            {
                work.Run();
                lock (gate) pending.Remove(work);
            }
        }
        catch (Exception ex) { failure = ex; }
        finally
        {
            lock (gate) { stopping = true; foreach (var work in pending) work.Stop(); pending.Clear(); }
            try { finalizer?.Invoke(); } catch (Exception ex) { failure = failure is null ? ex : new AggregateException(failure, ex); }
            queue.Dispose();
            if (failure is null) completion.TrySetResult(); else completion.TrySetException(failure);
        }
    }
    public void Dispose() => Stop(null);
    public void Stop(Action? releaseOnSta)
    {
        lock (gate)
        {
            if (!stopping)
            {
                stopping = true; finalizer = releaseOnSta;
                foreach (var work in pending) work.Stop();
                queue.CompleteAdding();
            }
        }
        if (Thread.CurrentThread == thread) return;
        if (!thread.Join(timeout)) throw new TimeoutException("STA-вызов ещё выполняется. Ресурсы будут освобождены на STA-потоке после его завершения.");
        Completion.GetAwaiter().GetResult();
    }
}
