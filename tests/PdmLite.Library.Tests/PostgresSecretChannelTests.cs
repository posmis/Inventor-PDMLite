using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.Versioning;
using PdmLite.Installation;

namespace PdmLite.Library.Tests;

public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute() { if (!OperatingSystem.IsWindows()) Skip = "Windows named-pipe ACL and process authentication test."; }
}

[SupportedOSPlatform("windows")]
public sealed class PostgresSecretChannelTests
{
    [WindowsFact]
    public async Task AuthenticatedClient_ReceivesSecretOverPipe()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var channel = new PostgresSecretChannel();
        using var client = new NamedPipeClientStream(".", channel.Name, PipeDirection.In, PipeOptions.Asynchronous);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var send = channel.SendAsync(Process.GetCurrentProcess(), new string('A', 64), timeout.Token);
        await client.ConnectAsync(timeout.Token);
        using var reader = new StreamReader(client);
        Assert.Equal(new string('A', 64), await reader.ReadLineAsync(timeout.Token));
        await send;
    }

    [WindowsFact]
    public async Task UnexpectedClientProcess_IsRejectedWithoutReceivingSecret()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var channel = new PostgresSecretChannel();
        using var child = Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 20 127.0.0.1 >nul") { CreateNoWindow = true, UseShellExecute = false })!;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var send = channel.SendAsync(child, new string('B', 64), timeout.Token);
            using var client = new NamedPipeClientStream(".", channel.Name, PipeDirection.In, PipeOptions.Asynchronous);
            await client.ConnectAsync(timeout.Token);
            await Assert.ThrowsAsync<IOException>(() => send);
            channel.Dispose();
            using var reader = new StreamReader(client);
            Assert.Null(await reader.ReadLineAsync(timeout.Token));
        }
        finally { if (!child.HasExited) child.Kill(entireProcessTree: true); }
    }

    [WindowsFact]
    public async Task ChildExitsBeforeConnection_FailsWithoutWaitingForTimeout()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var channel = new PostgresSecretChannel();
        using var child = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit 1") { CreateNoWindow = true, UseShellExecute = false })!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await Assert.ThrowsAsync<IOException>(() => channel.SendAsync(child, new string('C', 64), timeout.Token));
    }

    [WindowsFact]
    public async Task CancelledTransfer_DoesNotWaitForClient()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var channel = new PostgresSecretChannel();
        using var timeout = new CancellationTokenSource(); timeout.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => channel.SendAsync(Process.GetCurrentProcess(), new string('D', 64), timeout.Token));
    }
}
