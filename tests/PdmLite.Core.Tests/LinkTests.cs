using PdmLite.Link;

namespace PdmLite.Core.Tests;

public class LinkTests
{
    [Fact]
    public async Task Request_Response_And_Events_RoundTrip()
    {
        var endpoint = "Test" + Guid.NewGuid().ToString("N")[..8];
        await using var server = new LinkServer(endpoint);
        server.Handle(LinkProtocol.Requests.Ping, (_, _) => Task.FromResult<object?>(new PingResult("Test", "1.0", LinkProtocol.Version)));
        server.Handle<DocumentArgs>(LinkProtocol.Requests.OpenDocument, (args, _) =>
            args.Path == "bad" ? throw new InvalidOperationException("Нет файла") : Task.FromResult<object?>(null));
        server.Start();

        await using var client = new LinkClient(endpoint);
        Assert.True(await client.TryConnectAsync(TimeSpan.FromSeconds(5)));

        var ping = await client.RequestAsync<PingResult>(LinkProtocol.Requests.Ping);
        Assert.Equal("Test", ping!.Application);

        await client.RequestAsync(LinkProtocol.Requests.OpenDocument, new DocumentArgs("ok"));
        var error = await Assert.ThrowsAsync<LinkRemoteException>(() =>
            client.RequestAsync(LinkProtocol.Requests.OpenDocument, new DocumentArgs("bad")));
        Assert.Equal("Нет файла", error.Message);

        var received = new TaskCompletionSource<string?>();
        client.EventReceived += m => received.TrySetResult(m.PayloadAs<DocumentArgs>()?.Path);
        await server.BroadcastAsync(LinkProtocol.Events.ActiveDocumentChanged, new DocumentArgs(@"C:\Проект\Рама.iam"));

        Assert.Equal(@"C:\Проект\Рама.iam", await received.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Connect_WithoutServer_ReturnsFalse()
    {
        await using var client = new LinkClient("Missing" + Guid.NewGuid().ToString("N")[..8]);
        Assert.False(await client.TryConnectAsync(TimeSpan.FromMilliseconds(200)));
    }
}
