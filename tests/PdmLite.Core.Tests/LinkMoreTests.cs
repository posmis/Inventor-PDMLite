using PdmLite.Link;

namespace PdmLite.Core.Tests;

public class LinkMoreTests
{
    private static string Endpoint() => "Test" + Guid.NewGuid().ToString("N")[..8];

    [Fact]
    public async Task UnknownRequest_ReturnsRemoteError()
    {
        var endpoint = Endpoint();
        await using var server = new LinkServer(endpoint);
        server.Start();
        await using var client = new LinkClient(endpoint);
        Assert.True(await client.TryConnectAsync(TimeSpan.FromSeconds(5)));

        var error = await Assert.ThrowsAsync<LinkRemoteException>(() => client.RequestAsync("нет-такого"));
        Assert.Contains("нет-такого", error.Message);
    }

    [Fact]
    public async Task ConcurrentRequests_GetTheirOwnResponses()
    {
        var endpoint = Endpoint();
        await using var server = new LinkServer(endpoint);
        server.Handle<DocumentArgs>("echo", async (args, ct) =>
        {
            await Task.Delay(Random.Shared.Next(1, 30), ct);
            return args;
        });
        server.Start();
        await using var client = new LinkClient(endpoint);
        Assert.True(await client.TryConnectAsync(TimeSpan.FromSeconds(5)));

        var results = await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(i => client.RequestAsync<DocumentArgs>("echo", new DocumentArgs($"файл {i}"))));

        Assert.Equal(Enumerable.Range(0, 20).Select(i => $"файл {i}"), results.Select(r => r!.Path));
    }

    [Fact]
    public async Task Broadcast_ReachesAllClients_AndServerShutdownDisconnectsThem()
    {
        var endpoint = Endpoint();
        var server = new LinkServer(endpoint);
        server.Start();
        await using var a = new LinkClient(endpoint);
        await using var b = new LinkClient(endpoint);
        Assert.True(await a.TryConnectAsync(TimeSpan.FromSeconds(5)));
        Assert.True(await b.TryConnectAsync(TimeSpan.FromSeconds(5)));
        await WaitUntil(() => server.ClientCount == 2);

        var gotA = new TaskCompletionSource<string?>();
        var gotB = new TaskCompletionSource<string?>();
        a.EventReceived += m => gotA.TrySetResult(m.Name);
        b.EventReceived += m => gotB.TrySetResult(m.Name);
        await server.BroadcastAsync("событие", null);
        Assert.Equal("событие", await gotA.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("событие", await gotB.Task.WaitAsync(TimeSpan.FromSeconds(5)));

        var disconnected = new TaskCompletionSource();
        a.Disconnected += () => disconnected.TrySetResult();
        await server.DisposeAsync();
        await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(a.IsConnected);
        await Assert.ThrowsAnyAsync<Exception>(() => a.RequestAsync("ping", timeout: TimeSpan.FromSeconds(2)));
    }

    internal static async Task WaitUntil(Func<bool> condition, int timeoutMs = 5000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > until)
                throw new TimeoutException("Условие не выполнилось вовремя.");
            await Task.Delay(20);
        }
    }
}
