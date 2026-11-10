using System.Data.Common;
using System.IO.Compression;
using System.Text.Json.Nodes;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using PdmLite.Data;
using PdmLite.Data.Library;
using PdmLite.Library.Backup;
using PdmLite.Library.Import;
using PdmLite.Library.Items;
using PdmLite.Library.Storage;

namespace PdmLite.Library.Tests;

public class RestoreCommitOutcomeTests
{
    private sealed class CommitFault(string mode, CancellationTokenSource stop) : DbTransactionInterceptor
    {
        public bool Fired;
        public Action? AfterCommit;
        private static bool IsRestore(TransactionEventData e) => e.Context!.ChangeTracker.Entries<LibraryLogEntry>()
            .Any(l => l.Entity.Action == "restore" && l.Entity.Details?.Contains("operationId") == true);

        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
            TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            if (!Fired && (mode is "before" or "unknown-before") && IsRestore(eventData)) { Fired = true; throw new IOException("commit rejected before send"); }
            return ValueTask.FromResult(result);
        }
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            if (!Fired && IsRestore(eventData))
            {
                Fired = true;
                AfterCommit?.Invoke();
                if (mode == "cancel") { stop.Cancel(); throw new OperationCanceledException(stop.Token); }
                throw new IOException("commit confirmation lost after actual commit");
            }
            return Task.CompletedTask;
        }
    }

    private sealed class Intercepted(IPdmDatabase inner, DbTransactionInterceptor fault) : IPdmDatabase
    {
        public bool IsConfigured => inner.IsConfigured;
        public DatabaseSettings Settings => inner.Settings;
        public void Configure(DatabaseSettings settings) => inner.Configure(settings);
        public PdmDbContext CreateContext() => new(new DbContextOptionsBuilder<PdmDbContext>()
            .UseNpgsql(Settings.BuildConnectionString()).UseSnakeCaseNamingConvention().AddInterceptors(fault).Options);
        public Task MigrateAsync(CancellationToken ct = default) => inner.MigrateAsync(ct);
        public Task<(bool Ok, string Message)> TestAsync(DatabaseSettings settings, CancellationToken ct = default) => inner.TestAsync(settings, ct);
    }

    [PostgresFact]
    public Task ActualCommit_ThenConnectionError_KeepsRestoredDatabaseAndFiles() => Run("after");
    [PostgresFact]
    public Task ActualCommit_ThenCancellation_VerificationIgnoresCancelledUserToken() => Run("cancel");
    [PostgresFact]
    public Task CommitRejected_OldArchiveMarkerDoesNotProveThisAttemptCommitted() => Run("before");
    [PostgresFact]
    public Task ActualCommit_VerificationUnavailable_PreservesBothVersionsAndBlocksRetry() => Run("unknown");
    [PostgresFact]
    public Task SettingsChangedAfterCommit_VerificationStillUsesOriginalDatabase() => Run("settings");

    [PostgresFact]
    public Task CommitNotSent_VerificationUnavailable_AlsoPreservesBothVersions() => Run("unknown-before");

    [PostgresFact]
    public Task RestoreGateConnectionLostAfterCommit_ResultIsUnknownAndBothFoldersSurvive() => Run("gate-lost");

    [PostgresFact]
    public Task SettingsChangedBeforeDatabaseWrites_AbortsAndReturnsOriginalFiles() => Run("settings-before");

    [PostgresFact]
    public Task NewerAutomaticArchive_CannotPruneSafetyCopyBeforeUnknownCommit() => Run("unknown-future");

    private static async Task Run(string mode)
    {
        await using var db = await TestDatabase.CreateAsync();
        using var folders = new TempFolders();
        using var stop = new CancellationTokenSource();
        LibraryStorage Storage() => new(folders.Library);
        BackupOptions Options() => new(Path.Combine(folders.Root, "Backups"), mode == "unknown-future" ? 1 : 20, 7);
        var store = new FakePropertyStore();
        var importer = new LibraryImportService(db.Database, store, Storage, NullLogger<LibraryImportService>.Instance);
        var items = new LibraryItemService(db.Database, store, Storage, NullLogger<LibraryItemService>.Instance);
        async Task Import(string name, string bytes)
        {
            var source = folders.AddFile(name, bytes);
            Assert.Equal(1, (await importer.CommitAsync(await importer.AnalyzeAsync([source]))).Added);
        }
        await Import("Болт M8x20 ГОСТ 7798-70.ipt", "bolt");
        // Старый restore-маркер входит в архив: проверять просто наличие Action=restore нельзя.
        await using (var context = db.Database.CreateContext())
        {
            context.LibraryLog.Add(new LibraryLogEntry { Action = "restore", User = "test", At = DateTime.UtcNow,
                Details = JsonSerializer.Serialize(new { operationId = Guid.NewGuid().ToString("N"), backup = "old.zip" }) });
            await context.SaveChangesAsync();
        }
        var backup = new LibraryBackupService(db.Database, Storage, Options, NullLogger<LibraryBackupService>.Instance);
        var archive = await backup.CreateAsync(BackupReason.Manual);
        if (mode == "unknown-future") MakeNewerAutomaticCopy(archive.Path, Path.Combine(Options().Folder, "future.zip"));
        var expectedRestored = Directory.GetFiles(folders.Library, "*", SearchOption.AllDirectories)
            .ToDictionary(p => Path.GetRelativePath(folders.Library, p), File.ReadAllText);
        await Import("Гайка M8 ГОСТ 5915-70.ipt", "nut");
        var original = Directory.GetFiles(folders.Library, "*", SearchOption.AllDirectories)
            .ToDictionary(p => Path.GetRelativePath(folders.Library, p), File.ReadAllText);
        var fault = new CommitFault(mode, stop);
        var wrapped = new Intercepted(db.Database, fault);
        var savedSettings = db.Database.Settings;
        if (mode == "settings") fault.AfterCommit = () =>
        {
            var changed = savedSettings.Copy(); changed.Database = "wrong_database_after_commit";
            db.Database.Configure(changed);
        };
        if (mode == "gate-lost") fault.AfterCommit = () =>
        {
            using var admin = db.Database.CreateContext();
            admin.Database.ExecuteSqlRaw("""
                SELECT pg_terminate_backend(pid, 5000) FROM pg_locks
                WHERE locktype = 'advisory' AND granted
                  AND database = (SELECT oid FROM pg_database WHERE datname = current_database())
                  AND pid <> pg_backend_pid()
                """);
        };
        var service = new LibraryBackupService(wrapped, Storage, Options, NullLogger<LibraryBackupService>.Instance)
        {
            MoveDirectory = (from, to) =>
            {
                Directory.Move(from, to);
                if (mode == "settings-before" && from.Contains(".restore-") && to == folders.Library)
                {
                    var changed = savedSettings.Copy(); changed.Database = "wrong_database_before_commit";
                    db.Database.Configure(changed);
                }
            },
            CreateVerificationContext = cs => mode.StartsWith("unknown", StringComparison.Ordinal)
                ? throw new IOException("verification database unavailable")
                : new PdmDbContext(new DbContextOptionsBuilder<PdmDbContext>().UseNpgsql(cs).UseSnakeCaseNamingConvention().Options)
        };
        LibraryRestoreException? error = null;
        BackupInfo? before = null;
        try
        {
            if (mode is "before" or "unknown" or "unknown-before" or "gate-lost" or "settings-before" or "unknown-future") error = await Assert.ThrowsAsync<LibraryRestoreException>(() => service.RestoreAsync(archive.Path, ct: stop.Token));
            else before = await service.RestoreAsync(archive.Path, ct: stop.Token);
        }
        finally { db.Database.Configure(savedSettings); }
        Assert.Equal(mode != "settings-before", fault.Fired);
        var committed = mode is not ("before" or "unknown-before" or "settings-before");
        var unknown = mode.StartsWith("unknown", StringComparison.Ordinal) || mode == "gate-lost";
        Assert.Equal(committed ? 1 : 2, (await items.ListAsync(await items.LoadCatalogAsync())).Count);
        AssertFiles(folders.Library, committed || unknown ? expectedRestored : original);
        var journal = folders.Library + ".restore-recovery.json";
        if (unknown)
        {
            Assert.Equal(RestoreDatabaseOutcome.Unknown, error!.DatabaseOutcome);
            Assert.False(error.RollbackCompleted);
            Assert.False(error.FileRollbackPerformed);
            Assert.NotNull(error.VerificationError);
            AssertFiles(error.PreviousFolder, original);
            Assert.True(File.Exists(error.BackupPath));
            // Новая автоматическая копия вытеснила бы safety-архив при Keep=1 без защиты журнала.
            var newer = Path.Combine(Options().Folder, "newer.zip");
            MakeNewerAutomaticCopy(error.BackupPath, newer);
            var pruner = new LibraryBackupService(db.Database, Storage,
                () => new BackupOptions(Options().Folder, 1, 7), NullLogger<LibraryBackupService>.Instance);
            pruner.Prune();
            Assert.True(File.Exists(error.BackupPath));
            Assert.True(File.Exists(journal));
            Assert.Contains("CommitUnknown", File.ReadAllText(journal));
            Assert.Contains("Файловый откат не выполнялся", error.Message);
            // Новый сервис имитирует перезапуск процесса. Неизвестный исход остаётся заблокирован.
            var retry = await Assert.ThrowsAsync<InvalidOperationException>(() => backup.RestoreAsync(archive.Path));
            Assert.Contains(journal, retry.Message);
            AssertFiles(folders.Library, expectedRestored);
            AssertFiles(error.PreviousFolder, original);
        }
        else
        {
            Assert.False(File.Exists(journal));
            Assert.DoesNotContain(Directory.GetDirectories(folders.Root), d => Path.GetFileName(d).StartsWith("Library."));
            if (mode is "before" or "settings-before")
            {
                Assert.Equal(RestoreDatabaseOutcome.NotCommitted, error!.DatabaseOutcome);
                Assert.True(error.RollbackCompleted);
            }
            else Assert.NotNull(before);
        }
    }

    private static void MakeNewerAutomaticCopy(string source, string target)
    {
        File.Copy(source, target);
        using var zip = ZipFile.Open(target, ZipArchiveMode.Update);
        var entry = zip.GetEntry("manifest.json")!;
        JsonNode manifest;
        using (var reader = new StreamReader(entry.Open())) manifest = JsonNode.Parse(reader.ReadToEnd())!;
        manifest["CreatedAt"] = DateTime.UtcNow.AddMinutes(1);
        manifest["Reason"] = "Scheduled";
        entry.Delete();
        using var writer = new StreamWriter(zip.CreateEntry("manifest.json").Open());
        writer.Write(manifest.ToJsonString());
    }

    private static void AssertFiles(string folder, Dictionary<string, string> expected)
    {
        var actual = Directory.GetFiles(folder, "*", SearchOption.AllDirectories).Select(p => Path.GetRelativePath(folder, p)).Order(StringComparer.Ordinal);
        Assert.Equal(expected.Keys.Order(StringComparer.Ordinal), actual);
        foreach (var (relative, bytes) in expected) Assert.Equal(bytes, File.ReadAllText(Path.Combine(folder, relative)));
    }

    [PostgresFact]
    public Task PendingCommit_MissingVisibleMarkerIsUnknownUntilTransactionFinishes() => Pending(true);
    [PostgresFact]
    public Task PendingRollback_MissingMarkerIsConfirmedOnlyAfterTransactionFinishes() => Pending(false);

    private static async Task Pending(bool commit)
    {
        await using var db = await TestDatabase.CreateAsync(); await db.Database.MigrateAsync();
        using var folders = new TempFolders();
        var key = BitConverter.ToInt64(Guid.NewGuid().ToByteArray(), 0);
        var marker = JsonSerializer.Serialize(new { operationId = Guid.NewGuid().ToString("N") });
        await using var context = db.Database.CreateContext();
        await using var tx = await context.Database.BeginTransactionAsync();
        await context.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})", [key]);
        context.LibraryLog.Add(new LibraryLogEntry { Action = "restore", User = "test", At = DateTime.UtcNow, Details = marker });
        await context.SaveChangesAsync();
        var service = new LibraryBackupService(db.Database, () => new LibraryStorage(folders.Library),
            () => new BackupOptions(folders.Root, 3, 7), NullLogger<LibraryBackupService>.Instance) { VerificationTimeout = TimeSpan.FromMilliseconds(200) };
        var pending = await service.VerifyRestoreCommitAsync(key, marker);
        Assert.Equal(RestoreDatabaseOutcome.Unknown, pending.Outcome);
        Assert.NotNull(pending.Error);
        if (commit) await tx.CommitAsync(); else await tx.RollbackAsync();
        var normalBudget = new LibraryBackupService(db.Database, () => new LibraryStorage(folders.Library),
            () => new BackupOptions(folders.Root, 3, 7), NullLogger<LibraryBackupService>.Instance);
        var finished = await normalBudget.VerifyRestoreCommitAsync(key, marker);
        Assert.Equal(commit ? RestoreDatabaseOutcome.Committed : RestoreDatabaseOutcome.NotCommitted, finished.Outcome);
    }

    [Fact]
    public void Journal_RejectsConcurrentAttempt_AndPersistsUnresolvedState()
    {
        using var folders = new TempFolders();
        string path;
        using (var journal = new RestoreRecoveryJournal(folders.Library, "id", "previous", _ => { }))
        {
            path = journal.Path;
            Assert.Throws<InvalidOperationException>(() => new RestoreRecoveryJournal(folders.Library, "other", "otherPrevious", _ => { }));
            journal.SetTarget("Host=localhost;Port=5432;Database=original_database;Username=private_user;Password=private_password");
            journal.SetBackup("before.zip"); journal.Preserve("CommitUnknown");
        }
        var metadata = File.ReadAllText(path);
        Assert.Contains("before.zip", metadata);
        Assert.Contains("original_database", metadata);
        Assert.DoesNotContain("private_user", metadata);
        Assert.DoesNotContain("private_password", metadata);
        Assert.Throws<InvalidOperationException>(() => new RestoreRecoveryJournal(folders.Library, "retry", "retryPrevious", _ => { }));
    }

    [PostgresFact]
    public async Task DatabaseGate_SerializesIndependentConnections_AndReleasesAfterCompletion()
    {
        await using var db = await TestDatabase.CreateAsync();
        var cs = db.Database.Settings.BuildConnectionString();
        await using (var gate = await RestoreDatabaseGate.EnterAsync(cs, TimeSpan.FromSeconds(5), _ => { }))
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                RestoreDatabaseGate.EnterAsync(cs, TimeSpan.FromMilliseconds(200), _ => { }));
            await gate.EnsureHeldAsync();
        }
        await using var next = await RestoreDatabaseGate.EnterAsync(cs, TimeSpan.FromSeconds(5), _ => { });
        await next.EnsureHeldAsync();
    }
}
