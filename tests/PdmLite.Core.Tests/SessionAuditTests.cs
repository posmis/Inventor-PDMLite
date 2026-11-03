using Microsoft.Extensions.Logging.Abstractions;
using PdmLite.Core.Domain;
using PdmLite.Core.Events;
using PdmLite.Core.Projects;

namespace PdmLite.Core.Tests;

public class SessionAuditTests
{
    private static readonly string RootPath = Path.GetFullPath("audit-project");
    private static ProjectItem Item(string name, params ProjectItem[] children) => new(Path.Combine(RootPath, name), DocumentKind.Folder, 0, DateTime.UtcNow, children);
    private static ProjectInfo Project(string name) => new(name, Path.Combine(RootPath, name));

    [Fact]
    public void DeepTree_TraversesWithoutRecursionAndKeepsDepthFirstOrder()
    {
        var tree = Item("leaf");
        for (var i = 0; i < 20000; i++) tree = Item(i.ToString(), tree);
        Assert.Equal(20001, tree.Descendants().Count());
        Assert.Equal(new[] { "a", "b", "c", "d" }, Item("a", Item("b", Item("c")), Item("d")).Descendants().Select(i => i.Name));
    }

    [Fact]
    public async Task FailedIndexBuild_DoesNotPublishOrReplacePreviouslyOpenedProject()
    {
        var events = new EventBus(NullLogger<EventBus>.Instance);
        var notifications = new List<ProjectOpened>(); using var subscription = events.Subscribe<ProjectOpened>(notifications.Add);
        var scanner = new Scanner();
        var session = new ProjectSession(scanner, events, NullLogger<ProjectSession>.Instance);
        scanner.Root = Item("good"); await session.OpenAsync(Project("good"));
        var previous = session.Root;
        scanner.Root = Item("bad", Item("duplicate"), Item("duplicate"));
        await Assert.ThrowsAsync<ArgumentException>(() => session.OpenAsync(Project("bad")));
        Assert.Equal("good", session.Project!.Name); Assert.Same(previous, session.Root);
        Assert.Same(previous, session.Find(previous!.Path)); Assert.Single(notifications);
    }

    [Fact]
    public async Task CanceledScanIgnoringToken_DoesNotCommitOrPublish()
    {
        var events = new EventBus(NullLogger<EventBus>.Instance);
        var notifications = new List<ProjectOpened>(); using var subscription = events.Subscribe<ProjectOpened>(notifications.Add);
        using var cts = new CancellationTokenSource();
        var scanner = new Scanner { BeforeReturn = cts.Cancel };
        var session = new ProjectSession(scanner, events, NullLogger<ProjectSession>.Instance);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.OpenAsync(Project("canceled"), cts.Token));
        Assert.Null(session.Project); Assert.Null(session.Root); Assert.Empty(notifications);
    }

    [Fact]
    public async Task RefreshWithoutProject_DoesNotScan()
    {
        var scanner = new Scanner();
        var session = new ProjectSession(scanner, new EventBus(NullLogger<EventBus>.Instance), NullLogger<ProjectSession>.Instance);
        await session.RefreshAsync(); Assert.Equal(0, scanner.Calls);
    }

    private sealed class Scanner : IProjectScanner
    {
        public ProjectItem Root { get; set; } = Item("root");
        public Action? BeforeReturn { get; init; }
        public int Calls { get; private set; }
        public Task<ProjectItem> ScanAsync(ProjectInfo project, CancellationToken cancellationToken = default)
        { Calls++; BeforeReturn?.Invoke(); return Task.FromResult(Root); }
    }
}
