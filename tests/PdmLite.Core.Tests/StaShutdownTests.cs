using PdmLite.Apprentice;

namespace PdmLite.Core.Tests;

public sealed class StaShutdownTests
{
    [Fact]
    public async Task TimedOutNativeCall_KeepsQueueAlive_AndReleasesComOnlyAfterCallFinishes()
    {
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var worker = new StaWorker("STA shutdown test", TimeSpan.FromMilliseconds(30));
        var activeThread = 0; var releasedThread = 0; var queuedExecuted = false;
        var active = worker.InvokeAsync(() => { activeThread = Environment.CurrentManagedThreadId; entered.Set(); release.Wait(); return 1; });
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        var queued = worker.InvokeAsync(() => queuedExecuted = true);
        try
        {
            Assert.Throws<TimeoutException>(() => worker.Stop(() => releasedThread = Environment.CurrentManagedThreadId));
            await Assert.ThrowsAsync<ObjectDisposedException>(() => active);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => queued);
            Assert.False(worker.Completion.IsCompleted); Assert.Equal(0, releasedThread);
            Assert.Throws<ObjectDisposedException>(() => { _ = worker.InvokeAsync(() => 2); });
        }
        finally { release.Set(); await worker.Completion.WaitAsync(TimeSpan.FromSeconds(5)); worker.Dispose(); }
        Assert.Equal(activeThread, releasedThread); Assert.False(queuedExecuted);
    }

    [Fact]
    public async Task Cancellation_UnblocksActiveCallerWithoutAbortingNativeThread()
    {
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        using var cts = new CancellationTokenSource(); using var worker = new StaWorker("STA cancellation test");
        var task = worker.InvokeAsync(() => { entered.Set(); release.Wait(); return 1; }, cts.Token);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        try { cts.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task.WaitAsync(TimeSpan.FromSeconds(1))); }
        finally { release.Set(); }
        Assert.Equal(42, await worker.InvokeAsync(() => 42).WaitAsync(TimeSpan.FromSeconds(5)));
    }
    [Fact]
    public async Task WorkerExceptionsAreObserved_AndDisposeIsRepeatable()
    {
        var worker = new StaWorker("STA exception test");
        await Assert.ThrowsAsync<IOException>(() => worker.InvokeAsync<int>(() => throw new IOException("work")));
        worker.Dispose(); worker.Dispose(); await worker.Completion;
    }
}
