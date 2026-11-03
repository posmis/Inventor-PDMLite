using PdmLite.Core.Documents;
using PdmLite.Core.Domain;
using PdmLite.Index;

namespace PdmLite.Library.Tests;

public sealed class ProjectHealthTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "pdm-health-" + Guid.NewGuid().ToString("N"));
    private string Root => Path.Combine(_home, "project");
    private ProjectInfo Project => new("Проверка", Root);
    private readonly Store _store = new();
    private ProjectHealthService Service => new(_store, Path.Combine(_home, "cards"));

    public ProjectHealthTests() => Directory.CreateDirectory(Root);
    private string FileAt(string name, string content = "part")
    {
        var path = Path.GetFullPath(Path.Combine(Root, name));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public async Task Analyze_SavesAndReloadsCard_WithoutRereadingProperties()
    {
        var part = FileAt("Болт.ipt"); var assembly = FileAt("Узел.iam"); FileAt("Узел.idw"); FileAt("Взрыв.ipn");
        _store.Read = path => new DocumentProperties { PartNumber = "A", Title = "Название",
            References = path == part ? [] : path == assembly ? [part] : [assembly] };
        var service = Service;
        var result = await service.AnalyzeAsync(Project);
        Assert.Equal(4, result.Files);
        Assert.Equal(4, result.Read);
        Assert.Equal(1, result.Parts); Assert.Equal(1, result.Assemblies);
        Assert.Equal(1, result.Drawings); Assert.Equal(1, result.Presentations);
        Assert.Empty(result.Issues);
        var saved = await service.LoadAsync(Project);
        Assert.NotNull(saved);
        Assert.Equal(result.CreatedUtc, saved.CreatedUtc);
        Assert.True(await service.IsCurrentAsync(saved));
        Assert.Equal(4, _store.Reads);
    }

    [Fact]
    public async Task Analyze_ReportsUnreadableMissingPropertiesContentCenterAndReferences()
    {
        var bad = FileAt("bad.ipt"); var assembly = FileAt("assembly.iam");
        var missing = Path.Combine(Root, "missing.ipt");
        _store.Read = path => path == bad ? throw new IOException("Повреждён")
            : new DocumentProperties { IsContentCenter = true, References = [missing, missing] };
        var result = await Service.AnalyzeAsync(Project);
        Assert.Equal(1, result.Read);
        Assert.Contains(result.Issues, i => i.Path == bad && i.Category == "Не прочитан");
        Assert.Contains(result.Issues, i => i.Path == assembly && i.Category == "Нет обозначения");
        Assert.Contains(result.Issues, i => i.Category == "Нет наименования");
        Assert.Contains(result.Issues, i => i.Category == "Content Center");
        Assert.Single(result.Issues, i => i.Category == "Ненайденная ссылка");
        Assert.Equal(2, result.Errors);
        File.WriteAllText(missing, "fixed");
        Assert.False(await Service.IsCurrentAsync(result));
    }

    [Fact]
    public async Task Duplicates_ModelsAreComparedByDesignation_DrawingsAreNotFalseDuplicates()
    {
        FileAt("one/a.ipt"); FileAt("two/a.ipt"); FileAt("a.idw");
        var result = await Service.AnalyzeAsync(Project);
        Assert.Equal(2, result.Issues.Count(i => i.Category == "Одинаковое имя файла"));
        Assert.Equal(2, result.Issues.Count(i => i.Category == "Повтор обозначения"));
        Assert.DoesNotContain(result.Issues, i => i.Path.EndsWith(".idw"));
    }

    [Fact]
    public async Task Freshness_DetectsContentChangeWithSameSizeAndTimestamp_AndOverwritesOneCard()
    {
        var path = FileAt("a.ipt", "aaaa");
        var time = File.GetLastWriteTimeUtc(path);
        var service = Service;
        var first = await service.AnalyzeAsync(Project);
        File.WriteAllText(path, "bbbb"); File.SetLastWriteTimeUtc(path, time);
        Assert.False(await service.IsCurrentAsync(first));
        var second = await service.AnalyzeAsync(Project);
        Assert.NotEqual(first.Inputs[path], second.Inputs[path]);
        Assert.True(await service.IsCurrentAsync(second));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(service.CardPath(Project))!, "*.json"));
        Assert.Equal(second.CreatedUtc, (await service.LoadAsync(Project))!.CreatedUtc);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Freshness_DetectsAddedOrRemovedDocuments(bool add)
    {
        var path = FileAt("a.ipt");
        var result = await Service.AnalyzeAsync(Project);
        if (add) FileAt("new.iam"); else File.Delete(path);
        Assert.False(await Service.IsCurrentAsync(result));
    }

    [Fact]
    public async Task Analysis_CanceledOrChangedDuringRead_PreservesPreviousCard()
    {
        var path = FileAt("a.ipt"); var service = Service;
        await service.AnalyzeAsync(Project);
        var bytes = await File.ReadAllBytesAsync(service.CardPath(Project));
        using var cts = new CancellationTokenSource();
        _store.Read = _ => { cts.Cancel(); return new DocumentProperties(); };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.AnalyzeAsync(Project, ct: cts.Token));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(service.CardPath(Project)));
        _store.Read = _ => { File.WriteAllText(path, "changed"); return new DocumentProperties(); };
        await Assert.ThrowsAsync<IOException>(() => service.AnalyzeAsync(Project));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(service.CardPath(Project)));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(service.CardPath(Project))!, "*.tmp"));
    }

    [Fact]
    public async Task ResolvedReferences_AreWarnings_AndExternalChangesInvalidateCard()
    {
        FileAt("a.iam");
        var external = Path.Combine(_home, "library.ipt"); File.WriteAllText(external, "part");
        var old = Path.Combine(_home, "old.ipt");
        _store.Read = _ => new DocumentProperties { PartNumber = "A", Title = "A", References = [old] };
        var service = new ProjectHealthService(_store, Path.Combine(_home, "cards"),
            (project, path, usedIn) => { Assert.Equal(Project, project); Assert.Equal(old, path); return external; });
        var result = await service.AnalyzeAsync(Project);
        Assert.Equal(0, result.Errors);
        Assert.Single(result.Issues, i => i.Category == "Ссылка по другому пути");
        Assert.True(await service.IsCurrentAsync(result));
        File.Delete(external);
        Assert.False(await service.IsCurrentAsync(result));
    }

    [Fact]
    public async Task OldVersions_AreExcluded_EmptyProjectIsNotReportedHealthy()
    {
        FileAt("OldVersions/old.ipt");
        var result = await Service.AnalyzeAsync(Project);
        Assert.Equal(0, result.Files); Assert.Equal(0, _store.Reads);
        Assert.Contains(result.Issues, i => i.Category == "Пустой проект");
        Assert.Equal("Есть замечания", result.Summary);
    }

    [Fact]
    public async Task ProjectIdentity_SeparatesIpjFiles_AndTracksConfigurationChanges()
    {
        var ipj = FileAt("one.ipj", "one"); var other = FileAt("two.ipj", "two");
        var project = Project with { ProjectFile = ipj };
        var service = Service;
        var result = await service.AnalyzeAsync(project);
        Assert.NotEqual(service.CardPath(project), service.CardPath(Project with { ProjectFile = other }));
        Assert.Null(await service.LoadAsync(Project));
        File.WriteAllText(ipj, "new");
        Assert.False(await service.IsCurrentAsync(result));
    }

    [Fact]
    public async Task UnavailableStore_DoesNotOverwriteCard()
    {
        FileAt("a.ipt"); await Service.AnalyzeAsync(Project);
        var service = new ProjectHealthService(new UnavailablePropertyStore("Нет Inventor"), Path.Combine(_home, "cards"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.AnalyzeAsync(Project));
        Assert.NotNull(await service.LoadAsync(Project));
    }

    public void Dispose() => Directory.Delete(_home, true);
    private sealed class Store : IDocumentPropertyStore
    {
        public int Reads { get; private set; }
        public Func<string, DocumentProperties> Read { get; set; } = _ => new() { PartNumber = "A", Title = "Название" };
        public bool IsAvailable => true;
        public string? UnavailableReason => null;
        public Task<DocumentProperties> ReadAsync(string path, CancellationToken cancellationToken = default)
        { Reads++; return Task.FromResult(Read(path)); }
        public Task WriteAsync(string path, DocumentPropertyChanges changes, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ReplaceReferencesAsync(string path, IReadOnlyDictionary<string, string> map, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
