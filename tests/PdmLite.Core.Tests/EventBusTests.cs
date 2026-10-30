using Microsoft.Extensions.Logging.Abstractions;
using PdmLite.Core.Events;

namespace PdmLite.Core.Tests;

public class EventBusTests
{
    private sealed record Ping(int Value);

    [Fact]
    public void Publish_DeliversToSubscribers_UntilUnsubscribed()
    {
        var bus = new EventBus(NullLogger<EventBus>.Instance);
        var received = new List<int>();

        var subscription = bus.Subscribe<Ping>(e => received.Add(e.Value));
        bus.Publish(new Ping(1));
        subscription.Dispose();
        bus.Publish(new Ping(2));

        Assert.Equal([1], received);
    }

    [Fact]
    public void Publish_HandlerError_DoesNotBreakOtherHandlers()
    {
        var bus = new EventBus(NullLogger<EventBus>.Instance);
        var delivered = false;

        bus.Subscribe<Ping>(_ => throw new InvalidOperationException());
        bus.Subscribe<Ping>(_ => delivered = true);
        bus.Publish(new Ping(1));

        Assert.True(delivered);
    }
}
