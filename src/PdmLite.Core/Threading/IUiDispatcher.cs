namespace PdmLite.Core.Threading;

/// <summary>
/// Поток интерфейса хоста. Внутри Inventor это также единственный поток,
/// из которого разрешено вызывать Inventor API.
/// </summary>
public interface IUiDispatcher
{
    bool CheckAccess();

    void Post(Action action);

    Task InvokeAsync(Action action);

    Task<T> InvokeAsync<T>(Func<T> func);
}
