using Microsoft.Extensions.Logging.Abstractions;
using PdmLite.Core.Domain;
using PdmLite.Core.Events;
using PdmLite.Core.Projects;

namespace PdmLite.Core.Tests;

public class RecoveryRegressionTests
{
    [Fact]
    public async Task ConcurrentOpen_PublishesInCommitOrderEvenWhenFirstPublisherIsDelayed()
    {
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var bus = new BlockingBus(entered, release);
        var session = new ProjectSession(new Scanner(), bus, NullLogger<ProjectSession>.Instance);
        var first = Task.Run(() => session.OpenAsync(Project("A")));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        try { await session.OpenAsync(Project("B")).WaitAsync(TimeSpan.FromSeconds(5)); }
        finally { release.Set(); }
        await first.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(["A", "B"], bus.Published);
        Assert.Equal(session.Project!.Name, bus.Published[^1]);
    }

    [Fact]
    public async Task ReentrantOpen_DoesNotDeadlockOrDeliverOlderEventLast()
    {
        var bus = new EventBus(NullLogger<EventBus>.Instance);
        var session = new ProjectSession(new Scanner(), bus, NullLogger<ProjectSession>.Instance);
        var delivered = new List<string>();
        using var first = bus.Subscribe<ProjectOpened>(e =>
        {
            if (e.Project.Name == "A") session.OpenAsync(Project("B")).WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        });
        using var second = bus.Subscribe<ProjectOpened>(e => delivered.Add(e.Project.Name));
        await session.OpenAsync(Project("A")).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(["A", "B"], delivered); Assert.Equal("B", session.Project!.Name);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Binding_AddOrVerificationFailureRestoresPreviousPath(bool wrongPath)
    {
        var old = Path.GetFullPath("old-library"); var next = Path.GetFullPath("new-library"); string? current = old;
        Assert.Throws<InvalidOperationException>(() => ProjectLibraryUpdate.Apply(next, () => current, () => current = null, path =>
        {
            if (path == next) { if (!wrongPath) throw new InvalidOperationException("Add failed"); current = Path.GetFullPath("wrong-library"); }
            else current = path;
        }));
        Assert.Equal(old, current);
    }

    [Fact]
    public void Binding_FailedRestoreReportsBothFailuresAndOldPath()
    {
        var old = Path.GetFullPath("old-library"); string? current = old;
        var error = Assert.Throws<AggregateException>(() => ProjectLibraryUpdate.Apply(Path.GetFullPath("new-library"),
            () => current, () => current = null, _ => throw new IOException("Add failed")));
        Assert.Equal(2, error.InnerExceptions.Count); Assert.Contains(old, error.Message); Assert.Null(current);
    }

    [Fact]
    public void Binding_SamePathDoesNotDeleteExistingEntry()
    {
        var path = Path.GetFullPath("library");
        ProjectLibraryUpdate.Apply(path, () => path, () => throw new Exception("Must not delete"), _ => throw new Exception("Must not add"));
    }

    [Fact]
    public void Binding_NoPreviousPathCleansPartiallyAddedEntry()
    {
        string? current = null; var path = Path.GetFullPath("new-library");
        Assert.Throws<IOException>(() => ProjectLibraryUpdate.Apply(path, () => current, () => current = null,
            value => { current = value; throw new IOException("Partial add failed"); }));
        Assert.Null(current);
    }

    private static ProjectInfo Project(string name) => new(name, Path.GetFullPath(name));
    private sealed class Scanner : IProjectScanner
    {
        public Task<ProjectItem> ScanAsync(ProjectInfo project, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ProjectItem(project.RootFolder, DocumentKind.Folder, 0, DateTime.UtcNow));
    }
    private sealed class BlockingBus(ManualResetEventSlim entered, ManualResetEventSlim release) : IEventBus
    {
        public List<string> Published { get; } = [];
        public void Publish<TEvent>(TEvent value) where TEvent : notnull
        {
            if (value is not ProjectOpened opened) return;
            if (opened.Project.Name == "A")
            { entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException(); }
            lock (Published) Published.Add(opened.Project.Name);
        }
        public IDisposable Subscribe<TEvent>(Action<TEvent> handler) where TEvent : notnull => throw new NotSupportedException();
    }
}
