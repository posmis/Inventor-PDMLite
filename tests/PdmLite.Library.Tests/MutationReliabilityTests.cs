using System.Data.Common;
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

public sealed class PostgresTheoryAttribute : TheoryAttribute
{
    public PostgresTheoryAttribute() { if (string.IsNullOrEmpty(TestDatabase.AdminConnectionString)) Skip = "Requires PostgreSQL."; }
}

public class MutationReliabilityTests
{
    private sealed class CommitFault(string action, bool before) : DbTransactionInterceptor
    {
        public bool Fired;
        private bool Target(TransactionEventData e) => e.Context!.ChangeTracker.Entries<LibraryLogEntry>().Any(l => l.Entity.Action == action);
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction, TransactionEventData eventData,
            InterceptionResult result, CancellationToken ct = default)
        { if (before && Target(eventData)) { Fired = true; throw new IOException("before commit"); } return ValueTask.FromResult(result); }
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken ct = default)
        { if (!before && Target(eventData)) { Fired = true; throw new IOException("after real commit"); } return Task.CompletedTask; }
    }
    private sealed class Wrapped(IPdmDatabase inner, DbTransactionInterceptor interceptor) : IPdmDatabase
    {
        public bool IsConfigured => inner.IsConfigured;
        public DatabaseSettings Settings => inner.Settings;
        public void Configure(DatabaseSettings s) => inner.Configure(s);
        public PdmDbContext CreateContext() => new(new DbContextOptionsBuilder<PdmDbContext>().UseNpgsql(Settings.BuildConnectionString())
            .UseSnakeCaseNamingConvention().AddInterceptors(interceptor).Options);
        public Task MigrateAsync(CancellationToken ct = default) => inner.MigrateAsync(ct);
        public Task<(bool Ok, string Message)> TestAsync(DatabaseSettings s, CancellationToken ct = default) => inner.TestAsync(s, ct);
    }
    private sealed class Paused : DbTransactionInterceptor
    {
        public bool Enabled;
        public TaskCompletionSource Ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(DbConnection connection,
            TransactionStartingEventData eventData, InterceptionResult<DbTransaction> result, CancellationToken ct = default)
        { if (Enabled) { Enabled = false; Ready.TrySetResult(); await Resume.Task; } return result; }
    }

    [PostgresTheory]
    [InlineData("edit", "after")][InlineData("duplicate", "after")][InlineData("merge", "after")][InlineData("delete", "after")]
    [InlineData("edit", "before")][InlineData("duplicate", "before")][InlineData("merge", "before")][InlineData("delete", "before")]
    [InlineData("edit", "unknown")][InlineData("duplicate", "unknown")][InlineData("merge", "unknown")][InlineData("delete", "unknown")]
    [InlineData("rewrite-properties-file", "after")]
    [InlineData("rewrite-properties-file", "before")]
    [InlineData("rewrite-properties-file", "unknown")]
    public async Task Mutation_FilesFollowActualCommit_AndUnknownPreservesRecoveryData(string action, string mode)
    {
        await using var db = await TestDatabase.CreateAsync(); using var folders = new TempFolders(); var store = new FakePropertyStore();
        LibraryStorage Storage() => new(folders.Library);
        var importer = new LibraryImportService(db.Database, store, Storage, NullLogger<LibraryImportService>.Instance);
        var a = folders.AddFile("Болт M8x20 ГОСТ 7798-70.ipt", "twenty");
        var b = folders.AddFile("Болт M8x30 ГОСТ 7798-70.ipt", "thirty");
        var inputs = new List<string> { a, b };
        if (action == "merge")
        {
            var parent = folders.AddFile("Узел.iam", "assembly");
            store.Files[parent] = new PdmLite.Core.Documents.DocumentProperties { PartNumber = "Узел", References = [a] };
            inputs.Add(parent);
        }
        var importSession = await importer.AnalyzeAsync(inputs);
        foreach (var parent in importSession.Candidates.Where(c => c.DocumentType == LibraryDocumentType.Assembly))
        { parent.CategoryId = importSession.Catalog.Leaves.First().Id; parent.Manufacturer = "Свой"; }
        importer.Validate(importSession);
        Assert.Equal(inputs.Count, (await importer.CommitAsync(importSession)).Added);
        var reader = new LibraryItemService(db.Database, store, Storage, NullLogger<LibraryItemService>.Instance);
        var catalog = await reader.LoadCatalogAsync(); var originals = await reader.ListAsync(catalog);
        var source = originals.Single(i => i.Designation.Contains("20")); var keep = originals.Single(i => i.Id != source.Id && i.DocumentType == LibraryDocumentType.Part);
        var bytes = originals.ToDictionary(i => i.RelativePath, i => File.ReadAllText(Storage().GetFullPath(i.RelativePath)));
        var fault = new CommitFault(action, mode == "before");
        var service = new LibraryItemService(new Wrapped(db.Database, fault), store, Storage, NullLogger<LibraryItemService>.Instance)
        { CreateVerificationContext = cs => mode == "unknown" ? throw new IOException("verification unavailable") : LibraryMutationCommit.CreateContext(cs) };
        async Task Run()
        {
            if (action == "edit") await service.ApplyEditAsync(catalog, source.Id, new("Болт M8x40 ГОСТ 7798-70", source.Kind, null, source.CategoryId));
            else if (action == "duplicate") await service.DuplicateAsync(catalog, source.Id, new("Болт M8x40 ГОСТ 7798-70", source.Kind, null, source.CategoryId));
            else if (action == "merge") await service.MergeDuplicatesAsync(keep.Id, [source.Id]);
            else if (action == "rewrite-properties-file")
            {
                var path = Storage().GetFullPath(source.RelativePath);
                store.Files[path] = store.Files[path] with { Title = "wrong" };
                var result = await service.RewritePropertiesAsync(catalog, [source.Id]);
                if (mode == "before") Assert.Single(result.Failed); else Assert.Equal(1, result.Updated);
            }
            else await service.DeleteAsync(source.Id);
        }
        LibraryCommitUncertainException? unknown = null;
        try
        {
            if (mode == "before" && action != "rewrite-properties-file") await Assert.ThrowsAsync<IOException>(Run);
            else if (mode == "unknown") unknown = await Assert.ThrowsAsync<LibraryCommitUncertainException>(Run);
            else await Run();
            Assert.True(fault.Fired);
            var rows = await reader.ListAsync(catalog);
            foreach (var row in rows)
            {
                var path = Storage().GetFullPath(row.RelativePath);
                Assert.True(File.Exists(path));
                var hash = await LibraryStorage.HashAsync(path);
                await using var context = db.Database.CreateContext();
                Assert.Equal(hash.Hash, (await context.LibraryItems.SingleAsync(i => i.Id == row.Id)).FileHash);
            }
            if (mode == "before")
            {
                Assert.Equal(originals.Select(i => i.Id).Order(), rows.Select(i => i.Id).Order());
                foreach (var (path, data) in bytes) Assert.Equal(data, File.ReadAllText(Storage().GetFullPath(path)));
            }
            else Assert.Equal(originals.Count + (action == "duplicate" ? 1 : action is "edit" or "rewrite-properties-file" ? 0 : -1), rows.Count);
            if (action == "merge")
            {
                var parent = rows.Single(i => i.DocumentType == LibraryDocumentType.Assembly);
                await using var refs = db.Database.CreateContext();
                Assert.Equal(mode == "before" ? source.Id : keep.Id,
                    (await refs.LibraryItemReferences.SingleAsync(r => r.AssemblyId == parent.Id)).ComponentId);
            }
            if (unknown is not null)
            {
                Assert.True(File.Exists(unknown.JournalPath));
                foreach (var copy in unknown.Copies) Assert.True(File.Exists(copy.Backup));
                await Assert.ThrowsAsync<InvalidOperationException>(() => reader.DeleteAsync(keep.Id));
            }
            else Assert.False(File.Exists(folders.Library + ".mutation-recovery.json"));
        }
        finally
        {
            if (unknown is not null) foreach (var copy in unknown.Copies) LibraryStorage.Delete(copy.Backup);
        }
    }

    [PostgresFact]
    public async Task RestoreDuringPausedImport_IsRejectedWithoutMovingOrDeletingItsFile()
    {
        await using var db = await TestDatabase.CreateAsync(); using var folders = new TempFolders(); var store = new FakePropertyStore();
        LibraryStorage Storage() => new(folders.Library);
        var seed = new LibraryImportService(db.Database, store, Storage, NullLogger<LibraryImportService>.Instance);
        await seed.CommitAsync(await seed.AnalyzeAsync([folders.AddFile("Болт M8x20 ГОСТ 7798-70.ipt", "bolt")]));
        var backups = new LibraryBackupService(db.Database, Storage, () => new(Path.Combine(folders.Root, "Backups"), 10, 7), NullLogger<LibraryBackupService>.Instance);
        var archive = await backups.CreateAsync(BackupReason.Manual);
        var pause = new Paused();
        var importer = new LibraryImportService(new Wrapped(db.Database, pause), store, Storage, NullLogger<LibraryImportService>.Instance);
        var session = await importer.AnalyzeAsync([folders.AddFile("Гайка M8 ГОСТ 5915-70.ipt", "nut")]);
        pause.Enabled = true; var work = importer.CommitAsync(session);
        try
        {
            await pause.Ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Assert.ThrowsAsync<InvalidOperationException>(() => backups.RestoreAsync(archive.Path));
            Assert.True(File.Exists(Storage().GetFullPath(Assert.Single(session.Candidates).TargetRelativePath!)));
        }
        finally { pause.Resume.TrySetResult(); }
        Assert.Equal(1, (await work).Added);
        await using var context = db.Database.CreateContext();
        foreach (var row in await context.LibraryItems.ToListAsync()) Assert.True(File.Exists(Storage().GetFullPath(row.RelativePath)));
    }

    [PostgresFact]
    public async Task WritesDuringRestore_AreRejectedBeforeAnyFileIsCopied()
    {
        await using var db = await TestDatabase.CreateAsync(); using var folders = new TempFolders();
        LibraryStorage Storage() => new(folders.Library); var store = new FakePropertyStore();
        var importer = new LibraryImportService(db.Database, store, Storage, NullLogger<LibraryImportService>.Instance);
        var session = await importer.AnalyzeAsync([folders.AddFile("Болт M8x20 ГОСТ 7798-70.ipt", "bolt")]);
        using var recovery = new RestoreRecoveryJournal(folders.Library, Guid.NewGuid().ToString("N"), "previous", _ => { });
        await Assert.ThrowsAsync<InvalidOperationException>(() => importer.CommitAsync(session));
        Assert.False(File.Exists(Storage().GetFullPath(Assert.Single(session.Candidates).TargetRelativePath!)));
    }

    [PostgresFact]
    public async Task Rewrite_DatabaseFailure_RestoresBytesAndNextFileCanCommit()
    {
        await using var db = await TestDatabase.CreateAsync(); using var folders = new TempFolders(); var store = new FakePropertyStore();
        LibraryStorage Storage() => new(folders.Library);
        var importer = new LibraryImportService(db.Database, store, Storage, NullLogger<LibraryImportService>.Instance);
        await importer.CommitAsync(await importer.AnalyzeAsync([folders.AddFile("Болт M8x20 ГОСТ 7798-70.ipt", "a"), folders.AddFile("Болт M8x30 ГОСТ 7798-70.ipt", "b")]));
        var service = new LibraryItemService(db.Database, store, Storage, NullLogger<LibraryItemService>.Instance);
        var catalog = await service.LoadCatalogAsync(); var all = await service.ListAsync(catalog); var bad = all.Single(i => i.Designation.Contains("20"));
        var old = File.ReadAllText(Storage().GetFullPath(bad.RelativePath));
        foreach (var item in all) { var path = Storage().GetFullPath(item.RelativePath); store.Files[path] = store.Files[path] with { Title = "wrong" }; }
        await using (var context = db.Database.CreateContext())
            await context.Database.ExecuteSqlRawAsync("CREATE FUNCTION public.reject_one() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN IF NEW.designation LIKE '%20%' THEN RAISE EXCEPTION 'failed first file'; END IF; RETURN NEW; END $$; CREATE TRIGGER reject_one BEFORE UPDATE ON library.items FOR EACH ROW EXECUTE FUNCTION public.reject_one();");
        var result = await service.RewritePropertiesAsync(catalog, null);
        Assert.Equal(1, result.Updated); Assert.Single(result.Failed);
        Assert.Equal(old, File.ReadAllText(Storage().GetFullPath(bad.RelativePath)));
        await using var check = db.Database.CreateContext();
        foreach (var item in await check.LibraryItems.ToListAsync()) Assert.Equal(item.FileHash, (await LibraryStorage.HashAsync(Storage().GetFullPath(item.RelativePath))).Hash);
    }

    [PostgresFact]
    public async Task Rewrite_AlreadyCorrectProperties_RepairsStaleHashAndVersionWithoutWritingFile()
    {
        await using var db = await TestDatabase.CreateAsync(); using var folders = new TempFolders(); var store = new FakePropertyStore();
        LibraryStorage Storage() => new(folders.Library);
        var importer = new LibraryImportService(db.Database, store, Storage, NullLogger<LibraryImportService>.Instance);
        await importer.CommitAsync(await importer.AnalyzeAsync([folders.AddFile("Болт M8x20 ГОСТ 7798-70.ipt", "a")]));
        var service = new LibraryItemService(db.Database, store, Storage, NullLogger<LibraryItemService>.Instance);
        var catalog = await service.LoadCatalogAsync(); var writes = store.WriteCount;
        await using (var context = db.Database.CreateContext()) { var item = await context.LibraryItems.SingleAsync(); item.FileHash = "stale"; await context.SaveChangesAsync(); }
        var result = await service.RewritePropertiesAsync(catalog, null);
        Assert.Equal(1, result.Updated); Assert.Equal(writes, store.WriteCount);
        await using var check = db.Database.CreateContext(); var row = await check.LibraryItems.Include(i => i.Versions).SingleAsync();
        Assert.Equal((await LibraryStorage.HashAsync(Storage().GetFullPath(row.RelativePath))).Hash, row.FileHash);
        Assert.Equal(row.FileHash, Assert.Single(row.Versions).FileHash);
    }
}
