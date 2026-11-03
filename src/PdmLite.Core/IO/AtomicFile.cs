namespace PdmLite.Core.IO;

/// <summary>Сохранение через уникальный соседний файл: ошибка и отмена оставляют прежний результат.</summary>
public static class AtomicFile
{
    public static void WriteAllText(string path, string text)
    {
        var temporary = Prepare(path);
        try { File.WriteAllText(temporary, text); File.Move(temporary, path, overwrite: true); }
        finally { Cleanup(temporary); }
    }

    public static async Task WriteAllTextAsync(string path, string text, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var temporary = Prepare(path);
        try
        {
            await File.WriteAllTextAsync(temporary, text, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
        }
        finally { Cleanup(temporary); }
    }

    private static string Prepare(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        return path + "." + Guid.NewGuid().ToString("N") + ".tmp";
    }
    private static void Cleanup(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Не подменяем исходную ошибку. */ }
    }
}
