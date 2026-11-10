using System.Text.Json;
using Npgsql;
using PdmLite.Data;
using PdmLite.Library.Backup;

namespace PdmLite.Library.Items;

/// <summary>Запись файлов и БД сериализована с восстановлением, включая разные процессы.</summary>
internal sealed class LibraryWriteLease : IAsyncDisposable
{
    private readonly RestoreDatabaseGate gate;
    private readonly FileStream journal;
    private readonly Action<Exception> report;
    private bool preserve;
    private object? operation;
    private RecoveryCopy[] copies = [];
    public bool IsPreserved => preserve;
    public string Root { get; }
    public string ConnectionString { get; }
    public string JournalPath => Root + ".mutation-recovery.json";

    private LibraryWriteLease(string root, string connectionString, RestoreDatabaseGate gate, Action<Exception> report)
    {
        Root = root; ConnectionString = connectionString; this.gate = gate; this.report = report;
        journal = new FileStream(JournalPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        try { Write("Writing"); }
        catch { journal.Dispose(); throw; } // Пустой/частичный журнал также блокирует повторную запись.
    }

    public static void CheckPending(string root, bool restoring = false)
    {
        foreach (var path in restoring ? new[] { root + ".mutation-recovery.json" }
                     : new[] { root + ".mutation-recovery.json", root + ".restore-recovery.json" })
            if (File.Exists(path)) throw new InvalidOperationException("Библиотека занята или требует проверки после незавершённой операции. Журнал: " + path);
    }

    public static async Task<LibraryWriteLease> EnterAsync(IPdmDatabase database, string root, Action<Exception> report, CancellationToken ct)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        CheckPending(root);
        var cs = database.Settings.BuildConnectionString();
        var gate = await RestoreDatabaseGate.EnterAsync(cs, TimeSpan.FromSeconds(5), report, ct).ConfigureAwait(false);
        try
        {
            CheckPending(root);
            Directory.CreateDirectory(Path.GetDirectoryName(root)!);
            return new LibraryWriteLease(root, cs, gate, report);
        }
        catch { await gate.DisposeAsync().ConfigureAwait(false); throw; }
    }

    public Task EnsureHeldAsync() => gate.EnsureHeldAsync();
    public void SetCopies(IEnumerable<RecoveryCopy> value) { copies = value.ToArray(); Write("Writing"); }
    public void SetOperation(string id, long key, string action, string marker)
    { operation = new { operationId = id, lockKey = key, action, marker }; Write("Saving"); }
    public void Preserve(string state, IEnumerable<RecoveryCopy> value)
    {
        preserve = true;
        copies = value.ToArray();
        try { Write(state); } catch (Exception ex) { report(ex); }
    }
    private void Write(string state)
    {
        var target = new NpgsqlConnectionStringBuilder(ConnectionString);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { state, root = Root, host = target.Host, port = target.Port,
            database = target.Database, operation, copies });
        journal.Position = 0; journal.Write(bytes); journal.SetLength(bytes.Length); journal.Flush(true);
    }
    public async ValueTask DisposeAsync()
    {
        try
        {
            journal.Dispose();
            if (!preserve) { try { File.Delete(JournalPath); } catch (Exception ex) { report(ex); } }
        }
        finally { await gate.DisposeAsync().ConfigureAwait(false); }
    }
}
