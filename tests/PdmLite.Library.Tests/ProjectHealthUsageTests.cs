using System.Text.Json.Nodes;
using PdmLite.Core.Documents;
using PdmLite.Core.Domain;
using PdmLite.Index;

namespace PdmLite.Library.Tests;

public sealed class ProjectHealthUsageTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("pdm-usage-").FullName;
    private string Root => Path.Combine(_home, "project");
    private ProjectInfo Project => new("Проверка использований", Root);
    private readonly Store _store = new();
    private ProjectHealthService Service => new(_store, Path.Combine(_home, "cards"));
    public ProjectHealthUsageTests() => Directory.CreateDirectory(Root);

    private string Add(string relative, params string[] references)
    {
        var path = Path.GetFullPath(Path.Combine(Root, relative));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Join("\n", references));
        _store.Documents[path] = new() { PartNumber = Path.GetFileNameWithoutExtension(path), Title = "Название", References = references };
        return path;
    }
    private static IEnumerable<ProjectHealthIssue> Unused(ProjectHealthCard card) =>
        card.Issues.Where(i => i.Category is "Неиспользуемые детали" or "Неиспользуемые сборки");

    [Fact]
    public async Task StandaloneModels_AreListedSeparately_DrawingsAndPresentationsAreNotCandidates()
    {
        var part = Add("part.ipt"); var assembly = Add("assembly.iam"); Add("drawing.idw"); Add("presentation.ipn");
        var card = await Service.AnalyzeAsync(Project);
        Assert.Equal(new[] { part, assembly }.Order(), Unused(card).Select(i => i.Path).Order());
        Assert.Contains(card.Issues, i => i.Path == part && i.Category == "Неиспользуемые детали");
        var issue = Assert.Single(card.Issues, i => i.Category == "Неиспользуемые сборки");
        Assert.Contains("главная сборка", issue.Details);
        Assert.Contains("других проектах", issue.Details);
        Assert.All(Unused(card), i => Assert.Equal("Предупреждение", i.Severity));
    }

    [Fact]
    public async Task AssemblyChain_OnlyTopLevelAssemblyHasNoIncomingReferences()
    {
        var part = Add("part.ipt"); var sub = Add("sub.iam", part); var top = Add("top.iam", sub);
        var card = await Service.AnalyzeAsync(Project);
        Assert.Equal(top, Assert.Single(Unused(card)).Path);
    }

    [Theory]
    [InlineData("drawing.idw")]
    [InlineData("drawing.dwg")]
    [InlineData("presentation.ipn")]
    [InlineData("derived.ipt")]
    public async Task AnyDocumentReference_CountsAsUsage(string parentName)
    {
        var part = Add("part.ipt"); var assembly = Add("assembly.iam", part);
        var parent = Add(parentName, assembly);
        var card = await Service.AnalyzeAsync(Project);
        Assert.DoesNotContain(Unused(card), i => i.Path == part || i.Path == assembly);
        if (parentName.EndsWith(".ipt")) Assert.Equal(parent, Assert.Single(Unused(card)).Path);
        else Assert.Empty(Unused(card));
    }

    [Fact]
    public async Task SelfReference_IsNotUsageByAnotherDocument()
    {
        var part = Add("part.ipt"); Add("part.ipt", part);
        Assert.Equal(part, Assert.Single(Unused(await Service.AnalyzeAsync(Project))).Path);
    }

    [Fact]
    public async Task ResolvedOldPath_ProtectsActualModelFromFalsePositive()
    {
        var part = Add("moved/part.ipt"); var old = Path.Combine(_home, "old", "part.ipt"); Add("assembly.iam", old);
        var service = new ProjectHealthService(_store, Path.Combine(_home, "cards"), (_, path, _) => path == old ? part : null);
        var card = await service.AnalyzeAsync(Project);
        Assert.DoesNotContain(Unused(card), i => i.Path == part);
        Assert.Contains(card.Issues, i => i.Category == "Ссылка по другому пути");
    }

    [Fact]
    public async Task EqualFileNamesInDifferentFolders_AreMatchedByFullPath()
    {
        var used = Add("one/part.ipt"); var unused = Add("two/part.ipt");
        Add("assembly.iam", used);
        var card = await Service.AnalyzeAsync(Project);
        Assert.DoesNotContain(Unused(card), i => i.Path == used);
        Assert.Contains(Unused(card), i => i.Path == unused);
    }

    [Fact]
    public async Task UnreadableDocument_SuppressesCandidatesAndExplainsIncompleteCheck()
    {
        Add("part.ipt"); var drawing = Add("drawing.idw"); _store.Failed.Add(drawing);
        var card = await Service.AnalyzeAsync(Project);
        Assert.Empty(Unused(card));
        Assert.Contains(card.Issues, i => i.Category == "Не прочитан");
        Assert.Contains(card.Issues, i => i.Category == "Поиск неиспользуемых не завершён");
    }

    [Fact]
    public async Task UncheckableReference_SuppressesCandidatesInsteadOfReportingOrphans()
    {
        Add("part.ipt"); Add("assembly.iam", "invalid\0path");
        var card = await Service.AnalyzeAsync(Project);
        Assert.Empty(Unused(card));
        Assert.Contains(card.Issues, i => i.Category == "Проверка ссылки");
        Assert.Contains(card.Issues, i => i.Category == "Поиск неиспользуемых не завершён");
    }

    [Fact]
    public async Task SavedCandidates_Reopen_AndDisappearAfterUsageIsAdded()
    {
        var part = Add("part.ipt"); var service = Service;
        await service.AnalyzeAsync(Project);
        Assert.Equal(part, Assert.Single(Unused((await service.LoadAsync(Project))!)).Path);
        Add("drawing.idw", part);
        Assert.False(await service.IsCurrentAsync((await service.LoadAsync(Project))!));
        var updated = await service.AnalyzeAsync(Project);
        Assert.Empty(Unused(updated));
        Assert.Empty(Unused((await service.LoadAsync(Project))!));
        Assert.True(await service.IsCurrentAsync(updated));
    }

    [Fact]
    public async Task CardFromBeforeUsageCheck_IsMarkedOutdatedEvenWhenFilesAreUnchanged()
    {
        Add("part.ipt"); var service = Service; await service.AnalyzeAsync(Project);
        var path = service.CardPath(Project);
        var old = JsonNode.Parse(File.ReadAllText(path))!.AsObject(); old.Remove("AnalysisVersion");
        File.WriteAllText(path, old.ToJsonString());
        var loaded = await service.LoadAsync(Project);
        Assert.NotNull(loaded);
        Assert.False(await service.IsCurrentAsync(loaded));
        Assert.True(await service.IsCurrentAsync(await service.AnalyzeAsync(Project)));
    }

    [Fact]
    public async Task IgnoredOldVersions_DoNotAffectUsageOrCompleteness()
    {
        var part = Add("part.ipt"); var old = Add("OldVersions/old.iam", part); _store.Failed.Add(old);
        var card = await Service.AnalyzeAsync(Project);
        Assert.Equal(part, Assert.Single(Unused(card)).Path);
        Assert.DoesNotContain(card.Issues, i => i.Category == "Поиск неиспользуемых не завершён");
    }

    [Fact]
    public async Task ReferencedExternalModel_IsNotAddedToProjectCandidates()
    {
        var external = Path.Combine(_home, "external.ipt"); File.WriteAllText(external, "part");
        Add("assembly.iam", external);
        var card = await Service.AnalyzeAsync(Project);
        Assert.Equal(1, card.Files);
        Assert.DoesNotContain(Unused(card), i => i.Path == external);
    }

    public void Dispose() => Directory.Delete(_home, true);
    private sealed class Store : IDocumentPropertyStore
    {
        public Dictionary<string, DocumentProperties> Documents { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Failed { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool IsAvailable => true;
        public string? UnavailableReason => null;
        public Task<DocumentProperties> ReadAsync(string path, CancellationToken cancellationToken = default) =>
            Failed.Contains(path) ? throw new IOException("Не удалось прочитать") : Task.FromResult(Documents[path]);
        public Task WriteAsync(string path, DocumentPropertyChanges changes, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ReplaceReferencesAsync(string path, IReadOnlyDictionary<string, string> map, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
