namespace PdmLite.Library.Backup;

/// <summary>Ошибка восстановления с явным результатом возврата файлов и путями для восстановления.</summary>
public sealed class LibraryRestoreException : IOException
{
    public Exception RestoreError { get; }
    public IReadOnlyList<Exception> RollbackErrors { get; }
    public bool RollbackCompleted => RollbackErrors.Count == 0;
    public string LibraryRoot { get; }
    public string PreviousFolder { get; }
    public string FailedFolder { get; }
    public string BackupPath { get; }

    internal LibraryRestoreException(Exception error, IReadOnlyList<Exception> rollbackErrors,
        string root, string previous, string failed, string backup)
        : base(MessageFor(error, rollbackErrors, root, previous, failed, backup),
            rollbackErrors.Count == 0 ? error : new AggregateException(new[] { error }.Concat(rollbackErrors)))
    {
        RestoreError = error;
        RollbackErrors = rollbackErrors.ToArray();
        LibraryRoot = root;
        PreviousFolder = previous;
        FailedFolder = failed;
        BackupPath = backup;
    }

    private static string MessageFor(Exception error, IReadOnlyList<Exception> errors,
        string root, string previous, string failed, string backup) =>
        (errors.Count == 0
            ? "Восстановление не выполнено. Возврат файлов в исходное состояние завершён."
            : "Откат восстановления не завершён. Согласованность БД и файлов не гарантируется. " +
              "Не изменяйте библиотеку до завершения восстановления.") +
        $"\n\nОшибка восстановления: {error.Message}" +
        (errors.Count == 0 ? "" : "\nОшибки отката: " + string.Join("; ", errors.Select(e => e.Message))) +
        $"\n\nПапка библиотеки: {root}\nИсходная папка (если не возвращена): {previous}" +
        $"\nДанные неудачного восстановления (если сохранены): {failed}\nРезервная копия до восстановления: {backup}";
}

internal static class RestoreRollback
{
    // Не удаляем данные, пока исходная папка не возвращена. Ошибка каждого шага сохраняется.
    internal static IReadOnlyList<Exception> Run(string root, string previous, string failed,
        bool hadRoot, bool staged, Action<string, string> move)
    {
        var errors = new List<Exception>();
        if (staged)
        {
            try { move(root, failed); }
            catch (Exception ex) { errors.Add(ex); }
        }
        if (hadRoot)
        {
            // Directory.Move не перезаписывает существующую папку. Даже при ошибке первого шага
            // пробуем возврат: ошибка не должна молча превращаться в пропуск восстановления.
            try { move(previous, root); }
            catch (Exception ex) { errors.Add(ex); }
        }
        if (!hadRoot && Directory.Exists(root))
            errors.Add(new IOException($"Папка {root} осталась после неудачного восстановления."));
        return errors;
    }
}
