using System.Text.Json;
using Npgsql;

namespace PdmLite.Library.Backup;

/// <summary>Одна попытка восстановления папки; неопределённый исход сохраняется и после перезапуска.</summary>
internal sealed class RestoreRecoveryJournal : IDisposable
{
    private readonly FileStream file;
    private readonly Action<Exception> report;
    private bool preserve;
    private readonly string operationId;
    private readonly string root;
    private readonly string previous;
    private string? backup;
    private string? host;
    private int? port;
    private string? database;
    public string Path { get; }

    public RestoreRecoveryJournal(string root, string operationId, string previous, Action<Exception> report)
    {
        this.root = root;
        this.operationId = operationId;
        this.previous = previous;
        this.report = report;
        Path = root + ".restore-recovery.json";
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        try { file = new FileStream(Path, FileMode.CreateNew, FileAccess.Write, FileShare.Read); }
        catch (IOException ex) when (File.Exists(Path))
        {
            throw new InvalidOperationException("Восстановление уже выполняется или результат предыдущей попытки требует проверки. " +
                "Сначала проверьте состояние БД и сохранённых папок. Журнал: " + Path, ex);
        }
        try { Write("Preparing"); }
        catch { file.Dispose(); File.Delete(Path); throw; }
    }

    public void SetTarget(string connectionString)
    {
        var target = new NpgsqlConnectionStringBuilder(connectionString);
        host = target.Host; port = target.Port; database = target.Database;
        Write("Preparing");
    }

    public void SetBackup(string path) { backup = path; Write("Prepared"); }

    public void Preserve(string state)
    {
        preserve = true;
        try { Write(state); }
        catch (Exception ex) { report(ex); }
    }

    private void Write(string state)
    {
        long? lockKey = Guid.TryParseExact(operationId, "N", out var id) ? BitConverter.ToInt64(id.ToByteArray(), 0) : null;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { operationId, lockKey, state, host, port, database, root, previous, backup });
        file.Position = 0;
        file.Write(bytes);
        file.SetLength(bytes.Length);
        file.Flush(flushToDisk: true);
    }

    public void Dispose()
    {
        file.Dispose();
        if (!preserve)
        {
            try { File.Delete(Path); }
            catch (Exception ex) { report(ex); }
        }
    }
}
