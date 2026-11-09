using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using PdmLite.Core.Documents;
using PdmLite.Data;
using PdmLite.Library.Catalog;
using PdmLite.Library.Import;
using PdmLite.Library.Storage;
using PdmLite.Hosting;
using PdmLite.Modules.Library.Settings;

namespace PdmLite.Library.Tests;

public sealed class InitializationAndImportRecoveryTests
{
    private sealed class CallbackStore(FakePropertyStore inner, Action<string> write) : IDocumentPropertyStore
    {
        public bool IsAvailable => true;
        public string? UnavailableReason => null;
        public Task<DocumentProperties> ReadAsync(string path, CancellationToken cancellationToken = default) => inner.ReadAsync(path, cancellationToken);
        public Task<DocumentProperties> ReadWithoutGeometryAsync(string path, CancellationToken cancellationToken = default) => inner.ReadWithoutGeometryAsync(path, cancellationToken);
        public async Task WriteAsync(string path, DocumentPropertyChanges changes, CancellationToken cancellationToken = default)
        { await inner.WriteAsync(path, changes, cancellationToken); write(path); }
        public Task ReplaceReferencesAsync(string path, IReadOnlyDictionary<string,string> map, CancellationToken cancellationToken = default) => inner.ReplaceReferencesAsync(path,map,cancellationToken);
    }
    private static string Part(TempFolders folders) => folders.AddFile("Гайка M8 ГОСТ 5915-70.ipt", "original");
    private static LibraryImportService Import(IPdmDatabase db, IDocumentPropertyStore store, TempFolders folders) =>
        new(db, store, () => new(folders.Library), NullLogger<LibraryImportService>.Instance);

    [PostgresFact]
    public async Task CancellationAfterPropertyWrite_RemovesUnregisteredCopy_AndAllowsRetry()
    {
        await using var db = await TestDatabase.CreateAsync(); using var folders = new TempFolders(); using var cts = new CancellationTokenSource();
        var fake = new FakePropertyStore(); var source = Part(folders);
        var service = Import(db.Database, new CallbackStore(fake, _ => cts.Cancel()), folders);
        var session = await service.AnalyzeAsync([source]); var candidate = Assert.Single(session.Candidates); Assert.True(candidate.CanImport);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.CommitAsync(session, ct:cts.Token));
        Assert.False(File.Exists(folders.InLibrary(candidate.TargetRelativePath!))); Assert.Equal("original", File.ReadAllText(source));
        await using (var context = db.Database.CreateContext()) Assert.Empty(await context.LibraryItems.ToListAsync());
        var retry = Import(db.Database, fake, folders);
        Assert.Equal(1, (await retry.CommitAsync(await retry.AnalyzeAsync([source]))).Added);
    }

    [PostgresFact]
    public async Task OrdinaryFailure_ReportsOriginalFailureAndUnremovableCopyPath()
    {
        await using var db = await TestDatabase.CreateAsync(); using var folders = new TempFolders(); var source = Part(folders);
        var service = Import(db.Database, new CallbackStore(new(), path => { File.Delete(path); Directory.CreateDirectory(path); throw new IOException("original failure"); }), folders);
        var session = await service.AnalyzeAsync([source]); var candidate = Assert.Single(session.Candidates);
        var result = await service.CommitAsync(session);
        var failure = Assert.Single(result.Failed); Assert.Contains("original failure", failure.Error);
        Assert.Contains(folders.InLibrary(candidate.TargetRelativePath!), failure.Error); Assert.Contains("Не удалось очистить", failure.Error);
        Assert.Equal(0, result.Added); Assert.Equal("original", File.ReadAllText(source));
    }

    [PostgresFact]
    public async Task CancellationWithCleanupFailure_RemainsCancellation_ButIncludesRemainingPath()
    {
        await using var db = await TestDatabase.CreateAsync(); using var folders = new TempFolders(); using var cts = new CancellationTokenSource();
        var source = Part(folders);
        var service = Import(db.Database, new CallbackStore(new(), path => { File.Delete(path); Directory.CreateDirectory(path); cts.Cancel(); throw new OperationCanceledException(cts.Token); }), folders);
        var session = await service.AnalyzeAsync([source]); var candidate = Assert.Single(session.Candidates);
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.CommitAsync(session, ct:cts.Token));
        Assert.Contains(folders.InLibrary(candidate.TargetRelativePath!), error.Message); Assert.Contains("Не удалось очистить", error.Message);
        await using var context = db.Database.CreateContext(); Assert.Empty(await context.LibraryItems.ToListAsync());
    }

    private sealed class CancelAfterCommit(CancellationTokenSource stop) : DbTransactionInterceptor
    {
        public bool Enabled;
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            if (Enabled) { Enabled=false; stop.Cancel(); throw new OperationCanceledException(stop.Token); }
            return Task.CompletedTask;
        }
    }
    private sealed class Intercepted(IPdmDatabase inner, DbTransactionInterceptor interceptor) : IPdmDatabase
    {
        public bool IsConfigured => inner.IsConfigured;
        public DatabaseSettings Settings => inner.Settings;
        public void Configure(DatabaseSettings settings) => inner.Configure(settings);
        public PdmDbContext CreateContext() => new(new DbContextOptionsBuilder<PdmDbContext>()
            .UseNpgsql(inner.Settings.BuildConnectionString()).UseSnakeCaseNamingConvention().AddInterceptors(interceptor).Options);
        public Task MigrateAsync(CancellationToken ct=default) => inner.MigrateAsync(ct);
        public Task<(bool Ok,string Message)> TestAsync(DatabaseSettings settings,CancellationToken ct=default) => inner.TestAsync(settings,ct);
    }
    [PostgresFact]
    public async Task CancellationReportedAfterRealCommit_DoesNotDeleteRegisteredFile()
    {
        await using var db = await TestDatabase.CreateAsync(); using var folders = new TempFolders(); using var cts = new CancellationTokenSource();
        var interceptor = new CancelAfterCommit(cts); var wrapped = new Intercepted(db.Database, interceptor);
        var service = Import(wrapped, new FakePropertyStore(), folders); var source = Part(folders);
        var session = await service.AnalyzeAsync([source]); var candidate = Assert.Single(session.Candidates); interceptor.Enabled=true;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.CommitAsync(session, ct:cts.Token));
        await using var context = db.Database.CreateContext(); var item = Assert.Single(await context.LibraryItems.ToListAsync());
        Assert.True(File.Exists(folders.InLibrary(item.RelativePath))); Assert.Equal(candidate.TargetRelativePath,item.RelativePath);
    }

    [PostgresFact]
    public async Task ConcurrentCatalogInitialization_IndependentConnectionsCreateExactlyOneCatalog()
    {
        await using var db = await TestDatabase.CreateAsync(); await db.Database.MigrateAsync();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var seeds = Enumerable.Range(0,8).Select(async _ =>
        {
            await using var context = db.Database.CreateContext(); await start.Task;
            await LibraryCatalog.SeedAsync(context);
        }).ToArray();
        start.SetResult(); await Task.WhenAll(seeds);
        await using var verify = db.Database.CreateContext();
        var catalog = await LibraryCatalog.LoadAsync(verify); var expected = catalog.Categories.Select(c => c.Path).Distinct().Count();
        Assert.Equal(expected, catalog.Categories.Count); Assert.True(expected > 10);
        var rules = await verify.LibraryCategoryRules.Select(r => new {r.Kind,r.Pattern}).ToListAsync();
        Assert.Equal(rules.Distinct().Count(),rules.Count);
        await LibraryCatalog.SeedAsync(verify); Assert.Equal(expected,await verify.LibraryCategories.CountAsync());
    }

    [Fact]
    public void FailedCopyPromotion_PreservesExistingTarget_AndRemovesOnlyItsTemporaryFile()
    {
        using var folders = new TempFolders(); var storage = new LibraryStorage(folders.Library); var source = Part(folders);
        var target = folders.InLibrary("target.ipt"); Directory.CreateDirectory(folders.Library); File.WriteAllText(target, "existing");
        Assert.Throws<IOException>(() => storage.CopyIn(source, "target.ipt"));
        Assert.Equal("existing",File.ReadAllText(target)); Assert.Equal("original",File.ReadAllText(source));
        Assert.Equal([target],Directory.GetFiles(folders.Library));
    }
    [Fact]
    public void ReadOnlySource_CopiesThroughStaging_WithoutChangingSourceAttributes()
    {
        using var folders = new TempFolders(); var source = Part(folders); File.SetAttributes(source, FileAttributes.ReadOnly);
        var target = new LibraryStorage(folders.Library).CopyIn(source,"target.ipt");
        Assert.False(File.GetAttributes(target).HasFlag(FileAttributes.ReadOnly));
        Assert.True(File.GetAttributes(source).HasFlag(FileAttributes.ReadOnly));
        Assert.Equal([target],Directory.GetFiles(folders.Library));
    }

    [Theory]
    [InlineData("database.json")]
    [InlineData("library.json")]
    public void SettingsEditor_FailedSaveDoesNotChangeActiveConnectionOrLibrary(string blocked)
    {
        using var folders = new TempFolders(); var config = Path.Combine(folders.Root,"settings");
        var databaseSettings = new JsonSettings<DatabaseSettings>("database",config); databaseSettings.Value.Host="old-host"; databaseSettings.Save();
        var librarySettings = new JsonSettings<LibrarySettings>("library",config); librarySettings.Value.RootFolder="old-library"; librarySettings.Save();
        var database = new PdmDatabase(databaseSettings.Value,NullLoggerFactory.Instance);
        File.Delete(Path.Combine(config,blocked)); Directory.CreateDirectory(Path.Combine(config,blocked));
        var viewModel = new LibrarySettingsViewModel(database,databaseSettings,librarySettings)
        { Host="new-host", RootFolder=folders.Library, WarnOnContentCenter=false };
        Assert.NotNull(viewModel.Save());
        Assert.Equal("old-host",database.Settings.Host); Assert.Equal("old-host",databaseSettings.Value.Host);
        Assert.Equal("old-library",librarySettings.Value.RootFolder); Assert.True(librarySettings.Value.WarnOnContentCenter);
    }
    [Fact]
    public void SettingsEditor_SuccessPublishesBothSettingsAndKeepsOtherLibraryOptions()
    {
        using var folders = new TempFolders(); var config=Path.Combine(folders.Root,"settings");
        var dbSettings=new JsonSettings<DatabaseSettings>("database",config);
        var librarySettings=new JsonSettings<LibrarySettings>("library",config); librarySettings.Value.BackupKeep=27;
        var database=new PdmDatabase(dbSettings.Value,NullLoggerFactory.Instance);
        var editor=new LibrarySettingsViewModel(database,dbSettings,librarySettings) { Host="new-host",RootFolder=folders.Library,WarnOnContentCenter=false };
        Assert.Null(editor.Save()); Assert.Equal("new-host",database.Settings.Host);
        Assert.Equal(folders.Library,new JsonSettings<LibrarySettings>("library",config).Value.RootFolder);
        Assert.False(librarySettings.Value.WarnOnContentCenter); Assert.Equal(27,librarySettings.Value.BackupKeep);
    }
}
