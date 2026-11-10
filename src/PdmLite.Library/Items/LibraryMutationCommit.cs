using System.Data;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using PdmLite.Data;
using PdmLite.Data.Library;

namespace PdmLite.Library.Items;

public sealed class LibraryCommitUncertainException : IOException
{
    public IReadOnlyList<RecoveryCopy> Copies { get; }
    public string JournalPath { get; }
    internal LibraryCommitUncertainException(Exception error, Exception? verification, string journal, RecoveryCopy[] copies)
        : base("Результат фиксации БД неизвестен. Файловый откат не выполнялся. Сохранённые файлы и копии:\n" +
               string.Join("\n", copies.Select(c => $"{c.Original} ← {c.Backup}")) +
               $"\nЖурнал: {journal}\nНе повторяйте запись или восстановление до проверки состояния БД.",
            verification is null ? error : new AggregateException(error, verification))
    { Copies = copies; JournalPath = journal; }
}

/// <summary>Явная транзакция и проверяемый маркер вместо предположения об откате SaveChanges.</summary>
internal sealed class LibraryMutationCommit(LibraryWriteLease lease, Func<string, PdmDbContext> verificationContext)
{
    private bool uncertain;
    private Exception? verificationError;
    public static PdmDbContext CreateContext(string cs) => new(new DbContextOptionsBuilder<PdmDbContext>()
        .UseNpgsql(cs).UseSnakeCaseNamingConvention().Options);

    public async Task SaveAsync(PdmDbContext db, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        var key = BitConverter.ToInt64(id.ToByteArray(), 0);
        var log = db.ChangeTracker.Entries<LibraryLogEntry>().Last(e => e.State == EntityState.Added).Entity;
        var details = JsonNode.Parse(log.Details ?? "{}")!.AsObject();
        details["operationId"] = id.ToString("N");
        log.Details = details.ToJsonString();
        lease.SetOperation(id.ToString("N"), key, log.Action, log.Details);
        var attempted = false;
        try
        {
            await lease.EnsureHeldAsync().ConfigureAwait(false);
            if (db.Database.GetConnectionString() != lease.ConnectionString)
                throw new InvalidOperationException("Настройки БД изменились во время операции. Повторите проверку компонента.");
            await using (var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false))
            {
                await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})", [key], ct).ConfigureAwait(false);
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                attempted = true;
                await tx.CommitAsync(ct).ConfigureAwait(false);
            }
            await lease.EnsureHeldAsync().ConfigureAwait(false);
        }
        catch
        {
            if (attempted)
            {
                try
                {
                    await lease.EnsureHeldAsync().ConfigureAwait(false);
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await using var check = verificationContext(lease.ConnectionString);
                    await using var tx = await check.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, timeout.Token).ConfigureAwait(false);
                    await check.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})", [key], timeout.Token).ConfigureAwait(false);
                    var saved = await check.LibraryLog.AnyAsync(l => l.Action == log.Action && l.Details == log.Details, timeout.Token).ConfigureAwait(false);
                    await lease.EnsureHeldAsync().ConfigureAwait(false);
                    if (saved) return;
                }
                catch (Exception ex) { uncertain = true; verificationError = ex; }
            }
            throw;
        }
    }

    public async Task<bool> CanRollbackAsync()
    {
        if (!uncertain)
        {
            try { await lease.EnsureHeldAsync().ConfigureAwait(false); }
            catch (Exception ex) { uncertain = true; verificationError = ex; }
        }
        return !uncertain;
    }

    public LibraryCommitUncertainException Preserve(Exception error, IEnumerable<RecoveryCopy> copies)
    {
        var saved = copies.ToArray();
        lease.Preserve("CommitUnknown", saved);
        return new LibraryCommitUncertainException(error, verificationError, lease.JournalPath, saved);
    }
}
