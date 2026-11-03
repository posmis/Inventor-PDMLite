namespace PdmLite.Library.Items;

public sealed record RecoveryCopy(string Original, string Backup);

/// <summary>Операция не завершена, а откат выполнен не полностью. Копии оставлены для ручного восстановления.</summary>
public sealed class LibraryRollbackException : IOException
{
    public IReadOnlyList<RecoveryCopy> Copies { get; }
    public LibraryRollbackException(Exception failure, IReadOnlyList<Exception> rollbackFailures, IEnumerable<RecoveryCopy> copies)
        : this(failure, rollbackFailures, copies.ToArray()) { }
    private LibraryRollbackException(Exception failure, IReadOnlyList<Exception> rollbackFailures, RecoveryCopy[] copies)
        : base($"Операция не выполнена ({failure.Message}), восстановление неполное. Не повторяйте операцию до проверки файлов. " +
            "Доступные резервные копии сохранены:\n" + string.Join("\n", copies.Select(c => $"{c.Original} ← {c.Backup}")) +
            "\nОшибки восстановления: " + string.Join("; ", rollbackFailures.Select(e => e.Message)),
            new AggregateException(new[] { failure }.Concat(rollbackFailures))) => Copies = copies;
}
