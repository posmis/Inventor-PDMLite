namespace PdmLite.Core.Threading;

/// <summary>Синхронное ожидание асинхронной операции там, где await невозможен (Dispose, Shutdown).</summary>
public static class SyncBridge
{
    /// <summary>
    /// Выполняет операцию в пуле потоков и ждёт её не дольше <paramref name="timeout"/>.
    /// Ожидание из потока интерфейса через <c>GetAwaiter().GetResult()</c> приводит
    /// к взаимной блокировке, если операция продолжается в том же потоке; здесь — нет.
    /// </summary>
    /// <returns><c>false</c>, если операция не завершилась за отведённое время.</returns>
    public static bool Run(Func<Task> operation, TimeSpan timeout)
    {
        try
        {
            return Task.Run(operation).Wait(timeout);
        }
        catch (AggregateException)
        {
            return true; // завершилась с ошибкой — ждать больше нечего
        }
    }
}
