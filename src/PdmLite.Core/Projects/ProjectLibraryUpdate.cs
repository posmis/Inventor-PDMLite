namespace PdmLite.Core.Projects;

/// <summary>Изменение привязки с восстановлением прежнего пути при отказе API.</summary>
public static class ProjectLibraryUpdate
{
    public static void Apply(string folder, Func<string?> current, Action remove, Action<string> add)
    {
        var previous = current();
        if (previous is not null && SameFolder(previous, folder)) return;
        try
        {
            if (previous is not null) remove();
            add(folder);
            if (current() is not { } actual || !SameFolder(actual, folder))
                throw new InvalidOperationException("Inventor не сохранил библиотеку в проекте.");
        }
        catch (Exception failure)
        {
            try
            {
                var actual = current();
                if (previous is null)
                {
                    if (actual is not null) remove();
                    if (current() is not null) throw new InvalidOperationException("Не удалось удалить неполную новую привязку.");
                }
                else if (actual is null || !SameFolder(actual, previous))
                {
                    if (actual is not null) remove();
                    add(previous);
                    if (current() is not { } restored || !SameFolder(restored, previous))
                        throw new InvalidOperationException("Прежняя привязка библиотеки не восстановлена.");
                }
            }
            catch (Exception restore)
            {
                throw new AggregateException($"Не удалось изменить и полностью восстановить привязку библиотеки. " +
                    $"Проверьте проект вручную. Прежняя папка: {previous ?? "библиотека отсутствовала"}.", failure, restore);
            }
            throw;
        }
    }
    private static bool SameFolder(string a, string b) => string.Equals(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);
}
