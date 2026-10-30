using PdmLite.Core.Threading;

namespace PdmLite.Core.Tests;

public class SyncBridgeTests
{
    /// <summary>Контекст «потока интерфейса»: продолжения ставятся в очередь, которую никто не разбирает.</summary>
    private sealed class BlockedUiContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) { /* поток занят ожиданием */ }
    }

    private static async Task OperationResumingOnCapturedContext()
    {
        await Task.Delay(50); // без ConfigureAwait(false) — как InventorConnection.DisposeAsync раньше
    }

    [Fact]
    public void Run_DoesNotDeadlock_WhenCalledFromUiContext()
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new BlockedUiContext());
        try
        {
            Assert.True(SyncBridge.Run(OperationResumingOnCapturedContext, TimeSpan.FromSeconds(5)));
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    [Fact]
    public void Run_ReturnsFalse_OnTimeout() =>
        Assert.False(SyncBridge.Run(() => Task.Delay(TimeSpan.FromSeconds(10)), TimeSpan.FromMilliseconds(100)));
}
