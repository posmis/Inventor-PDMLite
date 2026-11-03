using PdmLite.Core.Domain;
using PdmLite.Core.IO;
using PdmLite.Core.Projects;
using PdmLite.Explorer;

namespace PdmLite.Core.Tests;

public sealed class ReviewRegressionTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("pdm-review-").FullName;
    private string Add(string relative, string contents)
    {
        var path = Path.GetFullPath(Path.Combine(_root, relative));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, contents); return path;
    }

    [Fact]
    public void AssemblyBackups_EqualFileNamesKeepBothOriginals()
    {
        var a = Add("A/Сборка.iam", "assembly A"); var b = Add("B/Сборка.iam", "assembly B");
        var backups = new DocumentBackupSet(Path.Combine(_root, "backups"), a, new FrozenClock());
        var first = backups.Copy(a); var second = backups.Copy(b);
        Assert.NotEqual(first, second);
        Assert.Equal("assembly A", File.ReadAllText(first)); Assert.Equal("assembly B", File.ReadAllText(second));
    }

    [Fact]
    public void AssemblyBackups_OperationsWithinSameSecondDoNotOverwriteEarlierSnapshot()
    {
        var a = Add("Сборка.iam", "before first");
        var first = new DocumentBackupSet(Path.Combine(_root, "backups"), a, new FrozenClock()); var old = first.Copy(a);
        File.WriteAllText(a, "before second");
        var second = new DocumentBackupSet(Path.Combine(_root, "backups"), a, new FrozenClock()); var newer = second.Copy(a);
        Assert.NotEqual(first.Folder, second.Folder);
        Assert.Equal("before first", File.ReadAllText(old)); Assert.Equal("before second", File.ReadAllText(newer));
    }

    [Fact]
    public async Task Explorer_ExplicitDifferentIpjWithSameWorkspaceSwitchesProject()
    {
        Directory.CreateDirectory(Path.Combine(_root, "workspace"));
        var a = Add("A.ipj", "<Project><WorkspacePath>workspace</WorkspacePath></Project>");
        var b = Add("B.ipj", "<Project><WorkspacePath>workspace</WorkspacePath></Project>");
        var session = new Session(ProjectInfo.FromPath(a));
        await ExplorerLinkServer.OpenProjectIfNeededAsync(session, b, CancellationToken.None);
        Assert.Equal(b, session.Project!.ProjectFile); Assert.Equal("B", session.Project.Name); Assert.Equal(1, session.Opens);
    }

    [Fact]
    public async Task Explorer_SameIpjOrFolderDoesNotDiscardMoreSpecificProject()
    {
        var a = Add("A.ipj", "<Project/>"); var current = ProjectInfo.FromPath(a); var session = new Session(current);
        await ExplorerLinkServer.OpenProjectIfNeededAsync(session, a, CancellationToken.None);
        await ExplorerLinkServer.OpenProjectIfNeededAsync(session, _root, CancellationToken.None);
        Assert.Same(current, session.Project); Assert.Equal(0, session.Opens);
    }

    [Fact]
    public void AssemblyBackups_RepeatCopyKeepsFirstStateAndManifestMapsOriginals()
    {
        var path = Add("assembly.iam", "initial");
        var backups = new DocumentBackupSet(Path.Combine(_root, "backups"), path);
        var first = backups.Copy(path); File.WriteAllText(path, "modified");
        Assert.Equal(first, backups.Copy(path)); Assert.Equal("initial", File.ReadAllText(first));
        using var manifest = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(backups.Folder, "manifest.json")));
        var entry = Assert.Single(manifest.RootElement.GetProperty("Files").EnumerateArray());
        Assert.Equal(path, entry.GetProperty("Original").GetString());
        Assert.Equal(first, Path.GetFullPath(Path.Combine(backups.Folder, entry.GetProperty("Backup").GetString()!)));
    }

    private sealed class FrozenClock : TimeProvider
    { public override DateTimeOffset GetUtcNow() => new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero); }
    private sealed class Session(ProjectInfo project) : IProjectSession
    {
        public ProjectInfo? Project { get; private set; } = project;
        public ProjectItem? Root => null;
        public int Opens { get; private set; }
        public Task OpenAsync(ProjectInfo value, CancellationToken cancellationToken = default)
        { Project = value; Opens++; return Task.CompletedTask; }
        public Task RefreshAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ProjectItem? Find(string path) => null;
    }
    public void Dispose() => Directory.Delete(_root, true);
}
