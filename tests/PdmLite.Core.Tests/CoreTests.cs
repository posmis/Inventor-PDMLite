using Microsoft.Extensions.Logging.Abstractions;
using PdmLite.Core.Domain;
using PdmLite.Core.Events;
using PdmLite.Core.Projects;
using PdmLite.Core.UI;

namespace PdmLite.Core.Tests;

public sealed class CoreTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("pdmlite-core-").FullName;

    private string Touch(string relative, string content = "")
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    [Theory]
    [InlineData("a.IPT", DocumentKind.Part)]
    [InlineData("a.iam", DocumentKind.Assembly)]
    [InlineData("a.idw", DocumentKind.Drawing)]
    [InlineData("a.DWG", DocumentKind.Drawing)]
    [InlineData("a.ipn", DocumentKind.Presentation)]
    [InlineData("a.ipj", DocumentKind.Project)]
    [InlineData("a.pdf", DocumentKind.Other)]
    [InlineData("noext", DocumentKind.Other)]
    public void DocumentKinds_ByExtension(string file, DocumentKind kind)
    {
        Assert.Equal(kind, DocumentKinds.FromPath(file));
        Assert.Equal(kind != DocumentKind.Other, DocumentKinds.IsKnown(file));
        Assert.False(string.IsNullOrEmpty(DocumentKinds.DisplayName(kind)));
    }

    [Fact]
    public void ProjectInfo_FromFolder_TrimsSeparator_AndMissingPathThrows()
    {
        var project = ProjectInfo.FromPath(_root + Path.DirectorySeparatorChar);
        Assert.Equal(Path.GetFileName(_root), project.Name);
        Assert.Equal(_root, project.RootFolder);
        Assert.Null(project.ProjectFile);
        Assert.Throws<FileNotFoundException>(() => ProjectInfo.FromPath(Path.Combine(_root, "нет такого")));
    }

    [Fact]
    public void ProjectInfo_FromIpj_WithoutWorkspaceOrBroken_UsesIpjFolder()
    {
        var plain = Touch("p/Plain.ipj", "<InventorProject></InventorProject>");
        var broken = Touch("q/Broken.ipj", "<<не xml");
        Assert.Equal(Path.GetDirectoryName(plain), ProjectInfo.FromPath(plain).RootFolder);
        Assert.Equal(Path.GetDirectoryName(broken), ProjectInfo.FromPath(broken).RootFolder);
    }

    [Fact]
    public void IpjReader_AbsoluteWorkspace()
    {
        var workspace = Directory.CreateDirectory(Path.Combine(_root, "ws")).FullName;
        var ipj = Touch("cfg/A.ipj", $"<InventorProject><WorkspacePath>{workspace}</WorkspacePath></InventorProject>");
        Assert.Equal(workspace, IpjReader.TryGetWorkspaceFolder(ipj));
    }

    [Fact]
    public async Task Scanner_SkipsHiddenAndLinkedFolders()
    {
        Touch("Детали/a.ipt");
        Touch(".hidden/b.ipt");
        Directory.CreateSymbolicLink(Path.Combine(_root, "Детали", "loop"), _root);

        var root = await new FileSystemProjectScanner(NullLogger<FileSystemProjectScanner>.Instance).ScanAsync(ProjectInfo.FromPath(_root));

        var files = root.Descendants().Where(i => !i.IsFolder).Select(i => i.Name).ToList();
        Assert.Equal(["a.ipt"], files);
    }

    [Fact]
    public async Task ProjectSession_PublishesOpened_FindsCaseInsensitive_AndRefreshes()
    {
        Touch("A/a.ipt");
        var bus = new EventBus(NullLogger<EventBus>.Instance);
        var opened = new List<ProjectOpened>();
        bus.Subscribe<ProjectOpened>(opened.Add);
        var session = new ProjectSession(new FileSystemProjectScanner(NullLogger<FileSystemProjectScanner>.Instance), bus,
            NullLogger<ProjectSession>.Instance);

        await session.OpenAsync(ProjectInfo.FromPath(_root));
        Assert.NotNull(session.Find(Path.Combine(_root, "A", "a.ipt")));
        Assert.Null(session.Find(Path.Combine(_root, "A", "b.ipt")));

        Touch("A/b.ipt");
        await session.RefreshAsync();

        Assert.Equal(2, opened.Count);
        Assert.NotNull(session.Find(Path.Combine(_root, "A", "b.ipt")));
    }

    [Fact]
    public void EventBus_UnsubscribeInsideHandler_AndDoubleDispose_AreSafe()
    {
        var bus = new EventBus(NullLogger<EventBus>.Instance);
        var calls = 0;
        IDisposable? subscription = null;
        subscription = bus.Subscribe<string>(_ =>
        {
            calls++;
            subscription!.Dispose();
        });
        var other = 0;
        bus.Subscribe<int>(_ => other++);

        bus.Publish("a");
        bus.Publish("b");
        subscription.Dispose();

        Assert.Equal(1, calls);
        Assert.Equal(0, other);
    }

    [Fact]
    public void UiRegistry_RejectsDuplicates_AndOrdersByGroupThenOrder()
    {
        var registry = new UiRegistry();
        Task Noop(CommandContext _) => Task.CompletedTask;
        registry.RegisterCommand(new CommandDescriptor("b", "B", CommandPlacement.ContextMenu, Noop, Order: 1, Group: "2"));
        registry.RegisterCommand(new CommandDescriptor("a", "A", CommandPlacement.ContextMenu | CommandPlacement.Toolbar, Noop, Order: 5, Group: "1"));
        registry.RegisterCommand(new CommandDescriptor("c", "C", CommandPlacement.ContextMenu, Noop, Order: 0, Group: "2"));
        registry.RegisterPanel(new PanelDescriptor("p", "P", PanelPlacement.Navigator, _ => new object()));

        Assert.Equal(["a", "c", "b"], registry.GetCommands(CommandPlacement.ContextMenu).Select(c => c.Id));
        Assert.Equal(["a"], registry.GetCommands(CommandPlacement.Toolbar).Select(c => c.Id));
        Assert.Throws<InvalidOperationException>(() => registry.RegisterCommand(new CommandDescriptor("a", "A", CommandPlacement.Toolbar, Noop)));
        Assert.Throws<InvalidOperationException>(() => registry.RegisterPanel(new PanelDescriptor("p", "P", PanelPlacement.Details, _ => new object())));
    }

    [Fact]
    public void CommandContext_Single_AndAvailability()
    {
        var item = new ProjectItem("x.ipt", DocumentKind.Part, 0, DateTime.UtcNow);
        Assert.Same(item, new CommandContext([item]).Single);
        Assert.Null(CommandContext.Empty.Single);

        var command = new CommandDescriptor("x", "X", CommandPlacement.Toolbar, _ => Task.CompletedTask, ctx => ctx.Single is not null);
        Assert.False(command.IsAvailable(CommandContext.Empty));
        Assert.True(new CommandDescriptor("y", "Y", CommandPlacement.Toolbar, _ => Task.CompletedTask).IsAvailable(CommandContext.Empty));
    }

    public void Dispose() => Directory.Delete(_root, true);
}
