namespace PdmLite.Library.Backup;

public enum RestoreDatabaseOutcome { NotCommitted, Committed, Unknown }

/// <summary>Ошибка восстановления с явным результатом возврата файлов и путями для восстановления.</summary>
public sealed class LibraryRestoreException : IOException
{
    public Exception RestoreError { get; }
    public IReadOnlyList<Exception> RollbackErrors { get; }
    public RestoreDatabaseOutcome DatabaseOutcome { get; }
    public bool FileRollbackPerformed { get; }
    public Exception? VerificationError { get; }
    public string? RecoveryJournal { get; }
    public bool RollbackCompleted => DatabaseOutcome == RestoreDatabaseOutcome.NotCommitted && FileRollbackPerformed && RollbackErrors.Count == 0;
    public string LibraryRoot { get; }
    public string PreviousFolder { get; }
    public string FailedFolder { get; }
    public string BackupPath { get; }

    internal LibraryRestoreException(Exception error, IReadOnlyList<Exception> rollbackErrors,
        string root, string previous, string failed, string backup,
        RestoreDatabaseOutcome databaseOutcome = RestoreDatabaseOutcome.NotCommitted, bool fileRollbackPerformed = true,
        Exception? verificationError = null, string? recoveryJournal = null)
        : base(MessageFor(error, rollbackErrors, root, previous, failed, backup, databaseOutcome, recoveryJournal, verificationError),
            rollbackErrors.Count == 0 && verificationError is null ? error
                : new AggregateException(new[] { error }.Concat(rollbackErrors).Concat(verificationError is null ? [] : new[] { verificationError })))
    {
        DatabaseOutcome = databaseOutcome;
        FileRollbackPerformed = fileRollbackPerformed;
        VerificationError = verificationError;
        RecoveryJournal = recoveryJournal;
        RestoreError = error;
        RollbackErrors = rollbackErrors.ToArray();
        LibraryRoot = root;
        PreviousFolder = previous;
        FailedFolder = failed;
        BackupPath = backup;
    }

    private static string MessageFor(Exception error, IReadOnlyList<Exception> errors,
        string root, string previous, string failed, string backup, RestoreDatabaseOutcome outcome, string? journal, Exception? verificationError) =>
        (outcome == RestoreDatabaseOutcome.Unknown
            ? "Не удалось определить результат фиксации БД. Файловый откат не выполнялся; обе версии файлов сохранены. " +
              "Не изменяйте библиотеку и не повторяйте восстановление до проверки состояния БД."
            : errors.Count == 0
            ? "Восстановление не выполнено. Возврат файлов в исходное состояние завершён."
            : "Откат восстановления не завершён. Согласованность БД и файлов не гарантируется. " +
              "Не изменяйте библиотеку до завершения восстановления.") +
        $"\n\nОшибка восстановления: {error.Message}" +
        (verificationError is null ? "" : $"\nОшибка проверки фиксации: {verificationError.Message}") +
        (errors.Count == 0 ? "" : "\nОшибки отката: " + string.Join("; ", errors.Select(e => e.Message))) +
        $"\n\nПапка библиотеки: {root}\nИсходная папка (если не возвращена): {previous}" +
        $"\nДанные неудачного восстановления (если сохранены): {failed}\nРезервная копия до восстановления: {backup}" +
        (journal is null ? "" : $"\nЖурнал попытки восстановления: {journal}");
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
