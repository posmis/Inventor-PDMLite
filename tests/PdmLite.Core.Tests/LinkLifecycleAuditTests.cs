using PdmLite.Link;

namespace PdmLite.Core.Tests;

public class LinkLifecycleAuditTests
{
    private static string Endpoint() => "Audit" + Guid.NewGuid().ToString("N");

    [Fact]
    public async Task DisconnectedSubscriberFailure_DoesNotFaultReadLoopOrHideLaterSubscribers()
    {
        var endpoint = Endpoint(); var server = new LinkServer(endpoint); server.Start();
        await using var client = new LinkClient(endpoint);
        Assert.True(await client.TryConnectAsync(TimeSpan.FromSeconds(3)));
        var notified = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Disconnected += () => throw new InvalidOperationException("broken subscriber");
        client.Disconnected += () => notified.TrySetResult();
        await server.DisposeAsync();
        await notified.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(client.IsConnected);
    }

    [Fact]
    public async Task EventSubscriberFailure_DoesNotHideLaterSubscribersOrBreakRequests()
    {
        var endpoint = Endpoint(); await using var server = new LinkServer(endpoint);
        server.Handle("ping", (_, _) => Task.FromResult<object?>("pong")); server.Start();
        await using var client = new LinkClient(endpoint);
        Assert.True(await client.TryConnectAsync(TimeSpan.FromSeconds(3)));
        await LinkMoreTests.WaitUntil(() => server.ClientCount == 1);
        var notified = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.EventReceived += _ => throw new InvalidOperationException("broken subscriber");
        client.EventReceived += _ => notified.TrySetResult();
        await server.BroadcastAsync("event", null);
        await notified.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal("pong", await client.RequestAsync<string>("ping"));
    }

    [Fact]
    public async Task Dispose_IsIdempotent_AndCannotReconnectDisposedClientOrServer()
    {
        var endpoint = Endpoint(); var server = new LinkServer(endpoint); server.Start();
        var client = new LinkClient(endpoint); Assert.True(await client.TryConnectAsync(TimeSpan.FromSeconds(3)));
        await client.DisposeAsync(); await client.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => client.TryConnectAsync(TimeSpan.FromSeconds(1)));
        await server.DisposeAsync(); await server.DisposeAsync();
        Assert.Throws<ObjectDisposedException>(server.Start);
    }

    [Fact]
    public async Task Dispose_CancelsInProgressConnect()
    {
        var client = new LinkClient(Endpoint());
        var connecting = client.TryConnectAsync(TimeSpan.FromSeconds(30));
        await client.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => connecting);
    }

    [Fact]
    public async Task InvalidRequestTimeout_DoesNotExecuteServerHandler()
    {
        var endpoint = Endpoint(); await using var server = new LinkServer(endpoint);
        var calls = 0; server.Handle("write", (_, _) => { Interlocked.Increment(ref calls); return Task.FromResult<object?>(null); }); server.Start();
        await using var client = new LinkClient(endpoint); Assert.True(await client.TryConnectAsync(TimeSpan.FromSeconds(3)));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.RequestAsync("write", timeout: TimeSpan.FromMilliseconds(-2)));
        await client.RequestAsync("write"); Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(-2)][InlineData(-100)][InlineData(4294967295)]
    public async Task InvalidConnectTimeout_DoesNotConsumeClient(double milliseconds)
    {
        var endpoint = Endpoint(); await using var client = new LinkClient(endpoint);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.TryConnectAsync(TimeSpan.FromMilliseconds(milliseconds)));
        await using var server = new LinkServer(endpoint); server.Start();
        Assert.True(await client.TryConnectAsync(TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public async Task ConcurrentConnectAttempts_DoNotLeakSecondConnection()
    {
        var endpoint = Endpoint(); await using var server = new LinkServer(endpoint); server.Start();
        await using var client = new LinkClient(endpoint);
        var first = client.TryConnectAsync(TimeSpan.FromSeconds(3));
        var second = client.TryConnectAsync(TimeSpan.FromSeconds(3));
        Assert.True(await first);
        await Assert.ThrowsAsync<InvalidOperationException>(() => second);
        await LinkMoreTests.WaitUntil(() => server.ClientCount == 1);
    }

    [Fact]
    public async Task CanceledRequest_DoesNotBreakNextResponse()
    {
        var endpoint = Endpoint(); await using var server = new LinkServer(endpoint);
        server.Handle("ping", (_, _) => Task.FromResult<object?>("pong")); server.Start();
        await using var client = new LinkClient(endpoint); Assert.True(await client.TryConnectAsync(TimeSpan.FromSeconds(3)));
        using var ct = new CancellationTokenSource(); ct.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.RequestAsync("ping", cancellationToken: ct.Token));
        Assert.Equal("pong", await client.RequestAsync<string>("ping"));
    }
}
