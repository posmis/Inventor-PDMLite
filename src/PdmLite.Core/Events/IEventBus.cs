namespace PdmLite.Core.Events;

/// <summary>
/// Шина событий для связи модулей без прямых ссылок друг на друга.
/// Обработчики вызываются синхронно в потоке публикации; обработчики UI
/// сами переключаются в поток интерфейса через <see cref="Threading.IUiDispatcher"/>.
/// </summary>
public interface IEventBus
{
    void Publish<TEvent>(TEvent @event) where TEvent : notnull;

    /// <returns>Объект, освобождение которого отменяет подписку.</returns>
    IDisposable Subscribe<TEvent>(Action<TEvent> handler) where TEvent : notnull;
}
