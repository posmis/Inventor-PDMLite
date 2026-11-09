using PdmLite.Core.Lifetime;
using PdmLite.Link;

namespace PdmLite.Core.Tests;

public sealed class LinkShutdownTests
{
    [Fact]
    public async Task IgnoredCancellation_DrainsBeforeOwnedServicesAreDisposed_AndReportsTimeout()
    {
        var endpoint = "Drain" + Guid.NewGuid().ToString("N");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = new LinkServer(endpoint) { ShutdownTimeout = TimeSpan.FromMilliseconds(40) };
        server.Handle("hold", async (_, _) => { entered.SetResult(); await release.Task; return null; }); server.Start();
        await using var client = new LinkClient(endpoint); Assert.True(await client.TryConnectAsync(TimeSpan.FromSeconds(5)));
        var request = client.RequestAsync("hold"); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var disposed = false; var owner = new ResourceLifetime();
        owner.Add(() => { disposed = true; return Task.CompletedTask; }); owner.Add(server.StopAsync);
        var stop = owner.DisposeAsync().AsTask();
        try
        {
            await Assert.ThrowsAsync<TimeoutException>(() => server.DisposeAsync().AsTask());
            Assert.False(stop.IsCompleted); Assert.False(disposed);
            await Assert.ThrowsAsync<IOException>(() => request);
            await using var next = new LinkClient(endpoint);
            Assert.False(await next.TryConnectAsync(TimeSpan.FromMilliseconds(50)));
        }
        finally { release.TrySetResult(); await stop.WaitAsync(TimeSpan.FromSeconds(5)); await server.DisposeAsync(); }
        Assert.True(disposed);
    }
    [Fact]
    public async Task CooperativeRequest_IsCancelledAndAwaitedBeforeShutdownReturns()
    {
        var endpoint = "Cancel" + Guid.NewGuid().ToString("N");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var finished = false;
        var server = new LinkServer(endpoint);
        server.Handle("wait", async (_, ct) => { entered.SetResult(); try { await Task.Delay(Timeout.Infinite, ct); } finally { finished = true; } return null; }); server.Start();
        await using var client = new LinkClient(endpoint); Assert.True(await client.TryConnectAsync(TimeSpan.FromSeconds(5)));
        var request = client.RequestAsync("wait"); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await server.DisposeAsync(); Assert.True(finished);
        var error = await Record.ExceptionAsync(() => request.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(error is IOException or LinkRemoteException); await server.DisposeAsync();
    }
}
