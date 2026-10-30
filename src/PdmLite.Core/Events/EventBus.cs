using Microsoft.Extensions.Logging;

namespace PdmLite.Core.Events;

public sealed class EventBus(ILogger<EventBus> logger) : IEventBus
{
    private readonly object _lock = new();
    private readonly Dictionary<Type, List<Delegate>> _handlers = [];

    public void Publish<TEvent>(TEvent @event) where TEvent : notnull
    {
        Delegate[] handlers;
        lock (_lock)
        {
            if (!_handlers.TryGetValue(typeof(TEvent), out var list))
                return;
            handlers = [.. list];
        }

        foreach (var handler in handlers)
        {
            try
            {
                ((Action<TEvent>)handler)(@event);
            }
            catch (Exception ex)
            {
                // Ошибка одного подписчика не должна ломать остальных.
                logger.LogError(ex, "Ошибка в обработчике события {Event}", typeof(TEvent).Name);
            }
        }
    }

    public IDisposable Subscribe<TEvent>(Action<TEvent> handler) where TEvent : notnull
    {
        lock (_lock)
        {
            if (!_handlers.TryGetValue(typeof(TEvent), out var list))
                _handlers[typeof(TEvent)] = list = [];
            list.Add(handler);
        }

        return new Subscription(() =>
        {
            lock (_lock)
                _handlers[typeof(TEvent)].Remove(handler);
        });
    }

    private sealed class Subscription(Action unsubscribe) : IDisposable
    {
        private Action? _unsubscribe = unsubscribe;

        public void Dispose() => Interlocked.Exchange(ref _unsubscribe, null)?.Invoke();
    }
}
