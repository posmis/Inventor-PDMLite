using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using PdmLite.Core.Events;
using PdmLite.AddIn.Core;
using PdmLite.Explorer;
using PdmLite.Link;
using PdmLite.Modules.InventorLink;

namespace PdmLite.Core.Tests;

public sealed class LinkCompatibilityTests
{
    private static string Endpoint() => "Compat" + Guid.NewGuid().ToString("N");

    [Theory]
    [InlineData(0, "Inventor", false)]
    [InlineData(2, "Inventor", false)]
    [InlineData(1, "Unexpected", false)]
    [InlineData(1, "Inventor", true)]
    public async Task Handshake_ValidatesApplicationAndProtocol_NotProductVersion(int version, string app, bool compatible)
    {
        var endpoint = Endpoint(); await using var server = new LinkServer(endpoint);
        server.Handle(LinkProtocol.Requests.Ping, (_, _) => Task.FromResult<object?>(new PingResult(app, "999.42", version))); server.Start();
        await using var client = new LinkClient(endpoint); Assert.True(await client.TryConnectAsync(TimeSpan.FromSeconds(5)));
        if (compatible) Assert.Equal("999.42", (await LinkHandshake.VerifyAsync(client, "Inventor")).Version);
        else await Assert.ThrowsAsync<LinkCompatibilityException>(() => LinkHandshake.VerifyAsync(client, "Inventor"));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("missing")]
    [InlineData("malformed")]
    [InlineData("unsupported")]
    public async Task Handshake_MissingOrMalformedResponse_IsCompatibilityFailure(string mode)
    {
        var endpoint = Endpoint(); await using var server = new LinkServer(endpoint);
        if (mode != "unsupported") server.Handle(LinkProtocol.Requests.Ping, (_, _) => Task.FromResult<object?>(mode switch
        {
            "null" => null,
            "missing" => new { Application = "Inventor", Version = "1" },
            _ => new { Application = "Inventor", Version = "1", ProtocolVersion = "invalid" }
        }));
        server.Start(); await using var client = new LinkClient(endpoint); Assert.True(await client.TryConnectAsync(TimeSpan.FromSeconds(5)));
        await Assert.ThrowsAsync<LinkCompatibilityException>(() => LinkHandshake.VerifyAsync(client, "Inventor"));
    }

    [Theory]
    [InlineData("open")]
    [InlineData("reveal")]
    [InlineData("none")]
    public async Task ExplorerForwarder_IncompatibleInstance_DoesNotForwardOrFallBackToNewProcess(string mode)
    {
        var endpoint = Endpoint(); await using var server = new LinkServer(endpoint); var calls = 0;
        server.Handle(LinkProtocol.Requests.Ping, (_, _) => Task.FromResult<object?>(new PingResult("PdmLite.Explorer", "old", 2)));
        server.Handle(LinkProtocol.Requests.OpenProject, (_, _) => { Interlocked.Increment(ref calls); return Task.FromResult<object?>(null); });
        server.Handle(LinkProtocol.Requests.RevealDocument, (_, _) => { Interlocked.Increment(ref calls); return Task.FromResult<object?>(null); });
        server.Start();
        var args = new StartupArgs(mode == "none" ? null : "project.ipj", mode == "reveal" ? "part.ipt" : null);
        await Assert.ThrowsAsync<LinkCompatibilityException>(() => ExplorerInstanceForwarder.ForwardAsync(args, endpoint));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task InventorExplorerLauncher_RejectsMismatchBeforeForwardingOrLaunching()
    {
        var endpoint = Endpoint(); var calls = 0; await using var server = new LinkServer(endpoint);
        server.Handle(LinkProtocol.Requests.Ping, (_, _) => Task.FromResult<object?>(new PingResult("PdmLite.Explorer", "old", 2)));
        server.Handle(LinkProtocol.Requests.RevealDocument, (_, _) => { Interlocked.Increment(ref calls); return Task.FromResult<object?>(null); });
        server.Start();
        var launcher = new ExplorerLauncher(NullLogger<ExplorerLauncher>.Instance, endpoint);
        await Assert.ThrowsAsync<LinkCompatibilityException>(() => launcher.RevealAsync("part.ipt", "project.ipj"));
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplorerForwarder_CompatibleInstance_ChecksPingBeforeCommand(bool reveal)
    {
        var endpoint = Endpoint(); var calls = new ConcurrentQueue<string>(); await using var server = new LinkServer(endpoint);
        server.Handle(LinkProtocol.Requests.Ping, (_, _) => { calls.Enqueue("ping"); return Task.FromResult<object?>(new PingResult("PdmLite.Explorer", "0.1", 1)); });
        var command = reveal ? LinkProtocol.Requests.RevealDocument : LinkProtocol.Requests.OpenProject;
        server.Handle(command, (_, _) => { calls.Enqueue(command); return Task.FromResult<object?>(null); }); server.Start();
        Assert.True(await ExplorerInstanceForwarder.ForwardAsync(new("project.ipj", reveal ? "part.ipt" : null), endpoint));
        Assert.Equal(["ping", command], calls);
    }

    [Fact]
    public async Task InventorMismatch_DoesNotRequestStatusOrEnableCommands_AndRecoversAfterUpdate()
    {
        var endpoint = Endpoint(); var statusCalls = 0;
        await using var server = new LinkServer(endpoint);
        server.Handle(LinkProtocol.Requests.Ping, (_, _) => Task.FromResult<object?>(new PingResult("Inventor", "2025", 2)));
        server.Handle(LinkProtocol.Requests.GetStatus, (_, _) => { Interlocked.Increment(ref statusCalls); return Task.FromResult<object?>(new InventorStatus("2025", null, null, null, null)); }); server.Start();
        var bus = new EventBus(NullLogger<EventBus>.Instance); var errors = new ConcurrentQueue<InventorConnectionChanged>();
        using var subscription = bus.Subscribe<InventorConnectionChanged>(errors.Enqueue);
        await using var connection = new InventorConnection(bus, NullLogger<InventorConnection>.Instance, endpoint, TimeSpan.FromMilliseconds(30));
        connection.Start(); await LinkMoreTests.WaitUntil(() => errors.Any(e => e.Error is not null));
        Assert.False(connection.IsConnected); Assert.Equal(0, statusCalls);
        Assert.Contains("Обновите", errors.First().Error!);
        await Assert.ThrowsAsync<InvalidOperationException>(() => connection.OpenDocumentAsync("part.ipt"));
        server.Handle(LinkProtocol.Requests.Ping, (_, _) => Task.FromResult<object?>(new PingResult("Inventor", "2025", 1)));
        await LinkMoreTests.WaitUntil(() => connection.IsConnected && errors.Any(e => e.IsConnected));
        Assert.Null(errors.Last().Error); Assert.True(statusCalls > 0);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(1, true)]
    [InlineData(0, false)]
    [InlineData(2, false)]
    public async Task Server_RejectsDeclaredMismatchBeforeExecutingHandler_AndAcceptsLegacyV1(int? protocol, bool allowed)
    {
        var endpoint = Endpoint(); var calls = 0; await using var server = new LinkServer(endpoint);
        server.Handle("change", (_, _) => { Interlocked.Increment(ref calls); return Task.FromResult<object?>(null); }); server.Start();
        await using var pipe = new NamedPipeClientStream(".", LinkEndpoints.PipeName(endpoint), PipeDirection.InOut, PipeOptions.Asynchronous);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)); await pipe.ConnectAsync(timeout.Token);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(pipe, leaveOpen: true);
        await writer.WriteLineAsync(new LinkMessage { Name = "change", Id = "test", ProtocolVersion = protocol }.Serialize());
        var response = LinkMessage.Deserialize((await reader.ReadLineAsync(timeout.Token))!)!;
        Assert.Equal(allowed, response.Ok); Assert.Equal(allowed ? 1 : 0, calls);
    }
}
