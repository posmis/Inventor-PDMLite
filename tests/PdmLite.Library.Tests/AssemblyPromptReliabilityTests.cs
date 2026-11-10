using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using PdmLite.Core.Documents;
using PdmLite.Core.Events;
using PdmLite.Core.Threading;
using PdmLite.Data;
using PdmLite.Hosting;
using PdmLite.Library.Import;
using PdmLite.Library.Items;
using PdmLite.Library.Storage;
using PdmLite.Modules.Library.Checks;

namespace PdmLite.Library.Tests;

public class AssemblyPromptReliabilityTests
{
    private sealed class Ui : IUiDispatcher
    {
        public bool Queue;
        public readonly List<Action> Posted = [];
        public bool CheckAccess() => true;
        public void Post(Action action) { if (Queue) Posted.Add(action); else action(); }
        public Task InvokeAsync(Action action) { action(); return Task.CompletedTask; }
        public Task<T> InvokeAsync<T>(Func<T> action) => Task.FromResult(action());
    }
    private sealed class Assembly(string path) : IAssemblyComponents
    {
        public string Active = path;
        public string? Expected;
        public int Mutations;
        public IReadOnlyList<AssemblyComponentInfo> Components = [];
        public Task<AssemblyInspection> InspectActiveAsync(CancellationToken ct = default) { ct.ThrowIfCancellationRequested(); return Task.FromResult(new AssemblyInspection(Active, Components)); }
        public Task<ReplacementResult> ReplaceAsync(IReadOnlyList<ComponentReplacement> replacements, CancellationToken ct = default, string? expectedAssemblyPath = null)
        {
            Expected = expectedAssemblyPath; AssemblyTarget.Verify(expectedAssemblyPath, Active, ct); Mutations++;
            return Task.FromResult(new ReplacementResult(1, [Active], null, []));
        }
        public Task<IReadOnlyList<OccurrenceRename>> FixOccurrenceNamesAsync(IReadOnlyCollection<string> paths, bool apply, CancellationToken ct = default, string? expectedAssemblyPath = null)
        {
            if (apply || expectedAssemblyPath is not null) AssemblyTarget.Verify(expectedAssemblyPath, Active, ct);
            if (apply) Mutations++;
            return Task.FromResult<IReadOnlyList<OccurrenceRename>>([]);
        }
        public Task<ComponentSelection> GetSelectionAsync(CancellationToken ct = default) => Task.FromResult(new ComponentSelection([], [], [], Active));
        public Task SaveDocumentsAsync(IReadOnlyList<string> paths, CancellationToken ct = default) => Task.CompletedTask;
    }
    private sealed class Pause : DbCommandInterceptor
    {
        public bool Enabled;
        public readonly TaskCompletionSource Ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData data,
            InterceptionResult<DbDataReader> result, CancellationToken ct = default)
        {
            if (Enabled && command.CommandText.Contains("categories")) { Enabled = false; Ready.TrySetResult(); await Resume.Task; }
            return result;
        }
    }
    private sealed class Wrapped(IPdmDatabase inner, Pause pause) : IPdmDatabase
    {
        public bool IsConfigured => inner.IsConfigured;
        public DatabaseSettings Settings => inner.Settings;
        public void Configure(DatabaseSettings value) => inner.Configure(value);
        public PdmDbContext CreateContext() => new(new DbContextOptionsBuilder<PdmDbContext>().UseNpgsql(Settings.BuildConnectionString())
            .UseSnakeCaseNamingConvention().AddInterceptors(pause).Options);
        public Task MigrateAsync(CancellationToken ct = default) => inner.MigrateAsync(ct);
        public Task<(bool Ok, string Message)> TestAsync(DatabaseSettings s, CancellationToken ct = default) => inner.TestAsync(s, ct);
    }

    [Fact]
    public async Task QueuedNotification_AfterDispose_DoesNotLoadOrShowAnything()
    {
        using var folders = new TempFolders(); var ui = new Ui { Queue = true }; var dialogs = new PromptDialogs(); PromptDialogs.Current.Value = dialogs;
        var bus = new EventBus(NullLogger<EventBus>.Instance); var assembly = new Assembly(Path.Combine(folders.Root, "A.iam"));
        await using var prompt = new ContentCenterPrompt(bus, ui, assembly, null!, () => new(folders.Library),
            new JsonSettings<LibrarySettings>("prompt", folders.Root), NullLogger<ContentCenterPrompt>.Instance);
        prompt.Attach(); bus.Publish(new ContentCenterComponentPlaced(assembly.Active, "part.ipt", "part"));
        Assert.Single(ui.Posted); prompt.Dispose(); ui.Posted[0](); await prompt.DisposeAsync();
        Assert.Equal(0, dialogs.Questions); Assert.Equal(0, dialogs.Errors); Assert.Equal(0, assembly.Mutations);
    }

    [PostgresTheory]
    [InlineData("stop")]
    [InlineData("switch-during-load")]
    [InlineData("switch-after-question")]
    [InlineData("same")]
    public async Task Prompt_StopsSafely_AndNeverMutatesAnotherAssembly(string mode)
    {
        await using var db = await TestDatabase.CreateAsync(); using var folders = new TempFolders(); var store = new FakePropertyStore();
        LibraryStorage Storage() => new(folders.Library);
        var source = folders.AddFile("Болт M8x20 ГОСТ 7798-70.ipt", "bolt");
        var importer = new LibraryImportService(db.Database, store, Storage, NullLogger<LibraryImportService>.Instance);
        Assert.Equal(1, (await importer.CommitAsync(await importer.AnalyzeAsync([source]))).Added);
        var reader = new LibraryItemService(db.Database, store, Storage, NullLogger<LibraryItemService>.Instance);
        var item = Assert.Single(await reader.ListAsync(await reader.LoadCatalogAsync()));
        var a = Path.Combine(folders.Root, "A.iam"); var b = Path.Combine(folders.Root, "B.iam"); var assembly = new Assembly(a);
        var dialogs = new PromptDialogs(); PromptDialogs.Current.Value = dialogs;
        var pause = new Pause { Enabled = mode is "stop" or "switch-during-load" };
        var service = new LibraryItemService(new Wrapped(db.Database, pause), store, Storage, NullLogger<LibraryItemService>.Instance);
        var bus = new EventBus(NullLogger<EventBus>.Instance);
        if (mode == "switch-after-question") dialogs.OnQuestion = () => assembly.Active = b;
        var prompt = new ContentCenterPrompt(bus, new Ui(), assembly, service, Storage,
            new JsonSettings<LibrarySettings>("prompt", folders.Root), NullLogger<ContentCenterPrompt>.Instance);
        prompt.Attach();
        bus.Publish(new ContentCenterComponentPlaced(a, source, item.Designation));
        try
        {
            if (mode is "stop" or "switch-during-load")
            {
                await pause.Ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
                if (mode == "stop")
                {
                    prompt.Dispose(); var stopping = prompt.DisposeAsync().AsTask();
                    Assert.False(stopping.IsCompleted); pause.Resume.TrySetResult();
                    await stopping.WaitAsync(TimeSpan.FromSeconds(10));
                    Assert.Equal(0, dialogs.Questions); Assert.Equal(0, dialogs.Errors); Assert.Equal(0, assembly.Mutations);
                    return;
                }
                assembly.Active = b; pause.Resume.TrySetResult();
            }
            if (mode != "same") await dialogs.ErrorShown.Task.WaitAsync(TimeSpan.FromSeconds(10));
            else
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                while (assembly.Mutations == 0) await Task.Delay(10, timeout.Token);
            }
            Assert.Equal(mode == "same" ? 1 : 0, assembly.Mutations);
            Assert.Equal(mode == "switch-during-load" ? 0 : 1, dialogs.Questions);
            if (mode != "same") Assert.Contains("Активная сборка изменилась", dialogs.LastError);
            if (mode is "same" or "switch-after-question") Assert.Equal(a, assembly.Expected);
        }
        finally { pause.Resume.TrySetResult(); await prompt.DisposeAsync(); }
    }

    [PostgresFact]
    public async Task ModelessAssemblyCheck_RejectsReplacementAfterDocumentSwitch()
    {
        await using var db = await TestDatabase.CreateAsync(); using var folders = new TempFolders(); var store = new FakePropertyStore();
        LibraryStorage Storage() => new(folders.Library);
        var source = folders.AddFile("Болт M8x20 ГОСТ 7798-70.ipt", "bolt");
        var importer = new LibraryImportService(db.Database, store, Storage, NullLogger<LibraryImportService>.Instance);
        await importer.CommitAsync(await importer.AnalyzeAsync([source]));
        var service = new LibraryItemService(db.Database, store, Storage, NullLogger<LibraryItemService>.Instance);
        var item = Assert.Single(await service.ListAsync(await service.LoadCatalogAsync()));
        var a = Path.Combine(folders.Root, "A.iam"); var assembly = new Assembly(a) { Components = [new(source, item.Designation, true, 1)] };
        var dialogs = new PromptDialogs(); PromptDialogs.Current.Value = dialogs;
        var vm = new AssemblyCheckViewModel(assembly, service, Storage, NullLogger<AssemblyCheckViewModel>.Instance);
        await vm.RunAsync(); Assert.Equal(1, vm.ToReplace);
        assembly.Active = Path.Combine(folders.Root, "B.iam");
        await vm.ReplaceCommand.ExecuteAsync(null);
        Assert.Equal(0, assembly.Mutations); Assert.Equal(a, assembly.Expected);
        Assert.Contains("Активная сборка изменилась", dialogs.LastError);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void UnsavedOrUnspecifiedAssembly_CannotBeMutationTarget(string? expected) =>
        Assert.Throws<InvalidOperationException>(() => AssemblyTarget.Verify(expected, Path.Combine(Path.GetTempPath(), "B.iam")));

    [Fact]
    public async Task Lifetime_ThrowingCancellationCallback_DoesNotAllowEarlyResourceDisposal()
    {
        var scope = new ContentCenterPromptLifetime();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelling = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var work = scope.RunAsync(async ct =>
        {
            using var registration = ct.Register(() => { cancelling.TrySetResult(); throw new IOException("cancellation callback failed"); });
            await release.Task;
        });
        var stopping = scope.DisposeAsync().AsTask();
        await cancelling.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(stopping.IsCompleted);
        release.TrySetResult(); await work;
        await Assert.ThrowsAsync<AggregateException>(() => stopping);
    }

    [Fact]
    public async Task Lifetime_WaitsForIgnoringCancellationOperation_AndRejectsNewWork()
    {
        var scope = new ContentCenterPromptLifetime(); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = scope.RunAsync(async ct => { await release.Task; ct.ThrowIfCancellationRequested(); });
        scope.Dispose(); var stopping = scope.DisposeAsync().AsTask();
        Assert.False(stopping.IsCompleted);
        await scope.RunAsync(_ => throw new Exception("late admission"));
        release.TrySetResult(); await started; await stopping;
        await scope.DisposeAsync();
    }
}
