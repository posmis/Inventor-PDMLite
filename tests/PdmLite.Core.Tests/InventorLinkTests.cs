using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using PdmLite.Core.Events;
using PdmLite.Link;
using PdmLite.Modules.InventorLink;

namespace PdmLite.Core.Tests;

public class InventorLinkTests
{
    /// <summary>Сервер, изображающий плагин в Inventor.</summary>
    private static LinkServer FakeInventor(string endpoint, string? activeDocument)
    {
        var server = new LinkServer(endpoint);
        server.Handle(LinkProtocol.Requests.Ping, (_, _) => Task.FromResult<object?>(new PingResult("Inventor", "2025.4", LinkProtocol.Version)));
        server.Handle(LinkProtocol.Requests.GetStatus, (_, _) =>
            Task.FromResult<object?>(new InventorStatus("2025.4", "Изделие", @"C:\П\Изделие.ipj", @"C:\П", activeDocument)));
        server.Start();
        return server;
    }

    [Fact]
    public async Task Connection_ReportsStatus_ForwardsEvents_AndReconnects()
    {
        var endpoint = "Test" + Guid.NewGuid().ToString("N")[..8];
        var bus = new EventBus(NullLogger<EventBus>.Instance);
        // События приходят из фоновых потоков — потокобезопасные коллекции.
        var connection = new ConcurrentQueue<InventorConnectionChanged>();
        var active = new ConcurrentQueue<string?>();
        bus.Subscribe<InventorConnectionChanged>(connection.Enqueue);
        bus.Subscribe<ActiveDocumentChanged>(e => active.Enqueue(e.Path));

        await using var link = new InventorConnection(bus, NullLogger<InventorConnection>.Instance, endpoint, TimeSpan.FromMilliseconds(100));
        link.Start();

        var server = FakeInventor(endpoint, @"C:\П\Рама.iam");
        await LinkMoreTests.WaitUntil(() => link.IsConnected && active.Contains(@"C:\П\Рама.iam"));
        Assert.Equal("2025.4", link.InventorVersion);
        Assert.Equal(@"C:\П\Изделие.ipj", (await link.GetStatusAsync())!.ProjectFile);

        await LinkMoreTests.WaitUntil(() => server.ClientCount == 1);
        await server.BroadcastAsync(LinkProtocol.Events.ActiveDocumentChanged, new DocumentArgs(@"C:\П\Ось.ipt"));
        await LinkMoreTests.WaitUntil(() => active.Contains(@"C:\П\Ось.ipt"));

        // Inventor закрыт — связь потеряна; запущен снова — восстановлена.
        await server.DisposeAsync();
        await LinkMoreTests.WaitUntil(() => !link.IsConnected && connection.Any(c => !c.IsConnected));
        await using var again = FakeInventor(endpoint, null);
        await LinkMoreTests.WaitUntil(() => link.IsConnected && connection.Count(c => c.IsConnected) == 2);

        Assert.Equal(1, connection.Count(c => !c.IsConnected)); // разрыв сообщён один раз
    }

    private sealed class FakeConnection(bool connected) : IInventorConnection
    {
        public List<string> Opened { get; } = [];
        public bool IsConnected => connected;
        public string? InventorVersion => null;
        public Task<InventorStatus?> GetStatusAsync(CancellationToken cancellationToken = default) => Task.FromResult<InventorStatus?>(null);

        public Task OpenDocumentAsync(string path, CancellationToken cancellationToken = default)
        {
            Opened.Add(path);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Launcher_OpensThroughInventor_WhenConnected()
    {
        var connection = new FakeConnection(true);
        await new LinkedDocumentLauncher(connection, NullLogger<LinkedDocumentLauncher>.Instance).OpenAsync(@"C:\П\Рама.iam");
        Assert.Equal([@"C:\П\Рама.iam"], connection.Opened);
    }
}
