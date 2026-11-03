namespace PdmLite.Core.IO;

/// <summary>Сохранение через уникальный соседний файл: ошибка и отмена оставляют прежний результат.</summary>
public static class AtomicFile
{
    // Ограниченный набор блокировок не растёт с количеством файлов. Одинаковый путь
    // использует один шлюз во всех экземплярах настроек, включая sync/async вызовы.
    private static readonly SemaphoreSlim[] Gates = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1)).ToArray();

    private static SemaphoreSlim Gate(string fullPath) =>
        Gates[(uint)StringComparer.OrdinalIgnoreCase.GetHashCode(fullPath) % (uint)Gates.Length];

    /// <summary>Открывает снимок файла, не запрещая его замену через rename в Windows.</summary>
    public static FileStream OpenRead(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    public static void WriteAllText(string path, string text)
    {
        path = Path.GetFullPath(path);
        var gate = Gate(path);
        gate.Wait();
        try
        {
            var temporary = Prepare(path);
            try { File.WriteAllText(temporary, text); Commit(temporary, path); }
            finally { Cleanup(temporary); }
        }
        finally { gate.Release(); }
    }

    public static async Task WriteAllTextAsync(string path, string text, CancellationToken ct = default)
    {
        path = Path.GetFullPath(path);
        var gate = Gate(path);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var temporary = Prepare(path);
            try
            {
                await File.WriteAllTextAsync(temporary, text, ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                Commit(temporary, path);
            }
            finally { Cleanup(temporary); }
        }
        finally { gate.Release(); }
    }

    private static void Commit(string temporary, string path)
    {
        // В Windows ReplaceFile предназначен для замены существующего файла и
        // сохраняет открытым читателям прежний снимок. MoveFileEx(overwrite)
        // не предоставляет ту же семантику для открытого назначения.
        if (File.Exists(path)) File.Replace(temporary, path, destinationBackupFileName: null);
        else File.Move(temporary, path);
    }

    private static string Prepare(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return path + "." + Guid.NewGuid().ToString("N") + ".tmp";
    }
    private static void Cleanup(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Не подменяем исходную ошибку. */ }
    }
}
