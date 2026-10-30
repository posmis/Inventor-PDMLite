namespace PdmLite.Core.Threading;

public static class TaskExtensions
{
    /// <summary>
    /// Запуск без ожидания из обработчика событий. Задачи, которые так запускаются, сами
    /// перехватывают свои ошибки; метод только явно показывает, что результат не нужен.
    /// </summary>
    public static void Forget(this Task task) => _ = task;
}
