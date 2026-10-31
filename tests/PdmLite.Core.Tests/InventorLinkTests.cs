using PdmLite.Core.Documents;
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

        public List<string> Placed { get; } = [];

        public Task PlaceComponentAsync(string path, CancellationToken cancellationToken = default)
        {
            Placed.Add(path);
            return Task.CompletedTask;
        }

        public OpenDocumentsResult? Open { get; set; }
        public bool FailOpenList { get; set; }

        public Task<OpenDocumentsResult?> GetOpenDocumentsAsync(CancellationToken cancellationToken = default) =>
            FailOpenList ? throw new TimeoutException("нет ответа") : Task.FromResult(Open);
    }

    [Fact]
    public async Task LinkedOpenDocuments_ListsThroughLink_CannotClose()
    {
        var connection = new FakeConnection(true) { Open = new OpenDocumentsResult([new OpenDocumentEntry("C:\\a.iam", true, true)]) };
        var documents = new LinkedOpenDocuments(connection, Microsoft.Extensions.Logging.Abstractions.NullLogger<LinkedOpenDocuments>.Instance);

        var list = await documents.ListAsync();

        Assert.Equal(new OpenDocument("C:\\a.iam", true, true), Assert.Single(list));
        Assert.False(documents.CanClose);
        await Assert.ThrowsAsync<InvalidOperationException>(() => documents.CloseAsync(["x"]));
    }

    [Fact]
    public async Task LinkedOpenDocuments_NotConnected_Empty_LinkError_Throws()
    {
        var offline = new LinkedOpenDocuments(new FakeConnection(false), Microsoft.Extensions.Logging.Abstractions.NullLogger<LinkedOpenDocuments>.Instance);
        Assert.Empty(await offline.ListAsync());

        var broken = new LinkedOpenDocuments(new FakeConnection(true) { FailOpenList = true },
            Microsoft.Extensions.Logging.Abstractions.NullLogger<LinkedOpenDocuments>.Instance);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => broken.ListAsync());
        Assert.Contains("открыты в Inventor", error.Message);
    }

    [Fact]
    public async Task Launcher_OpensThroughInventor_WhenConnected()
    {
        var connection = new FakeConnection(true);
        await new LinkedDocumentLauncher(connection, NullLogger<LinkedDocumentLauncher>.Instance).OpenAsync(@"C:\П\Рама.iam");
        Assert.Equal([@"C:\П\Рама.iam"], connection.Opened);
    }

    [Fact]
    public async Task ComponentPlacer_ForwardsToInventor_OnlyWhenConnected()
    {
        var connected = new FakeConnection(true);
        var placer = new LinkedComponentPlacer(connected);
        Assert.True(placer.CanPlace);
        await placer.PlaceAsync(@"C:\Lib\Болт.ipt");
        Assert.Equal([@"C:\Lib\Болт.ipt"], connected.Placed);

        var offline = new LinkedComponentPlacer(new FakeConnection(false));
        Assert.False(offline.CanPlace);
        await Assert.ThrowsAsync<InvalidOperationException>(() => offline.PlaceAsync(@"C:\Lib\Болт.ipt"));
    }

    [Fact]
    public async Task PlaceComponent_ReachesPlugin_AndPluginErrorsComeBack()
    {
        var endpoint = "Test" + Guid.NewGuid().ToString("N")[..8];
        var placed = new ConcurrentQueue<string>();
        await using var server = FakeInventor(endpoint, null);
        server.Handle<DocumentArgs>(LinkProtocol.Requests.PlaceComponent, (args, _) =>
            args.Path!.EndsWith(".ipt") ? Enqueue(placed, args.Path!) : throw new InvalidOperationException("Активный документ — не сборка."));

        await using var link = new InventorConnection(new EventBus(NullLogger<EventBus>.Instance), NullLogger<InventorConnection>.Instance,
            endpoint, TimeSpan.FromMilliseconds(100));
        link.Start();
        await LinkMoreTests.WaitUntil(() => link.IsConnected);

        await link.PlaceComponentAsync(@"C:\Lib\Гайка.ipt");
        Assert.Equal([@"C:\Lib\Гайка.ipt"], placed.ToArray());
        var error = await Assert.ThrowsAsync<LinkRemoteException>(() => link.PlaceComponentAsync(@"C:\Lib\Сборка.iam"));
        Assert.Contains("не сборка", error.Message);
    }

    private static Task<object?> Enqueue(ConcurrentQueue<string> queue, string value)
    {
        queue.Enqueue(value);
        return Task.FromResult<object?>(null);
    }
}
