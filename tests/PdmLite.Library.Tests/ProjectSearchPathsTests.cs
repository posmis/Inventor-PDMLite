using Microsoft.Extensions.Logging.Abstractions;
using PdmLite.Core.Domain;
using PdmLite.Core.Projects;
using PdmLite.Index;

namespace PdmLite.Library.Tests;

/// <summary>Поиск файла по ссылке как в Inventor: относительный путь в папках поиска проекта (Content Center, библиотеки).</summary>
public sealed class ProjectSearchPathsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("pdmlite-search-").FullName;
    private string Project => Path.Combine(_root, "Проект");
    private string ContentCenter => Path.Combine(_root, "CC");

    private string Create(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "");
        return path;
    }

    private sealed class Session(ProjectInfo? project) : IProjectSession
    {
        public ProjectInfo? Project => project;
        public ProjectItem? Root => null;
        public Task OpenAsync(ProjectInfo project, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RefreshAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ProjectItem? Find(string path) => null;
    }

    [Fact]
    public void ContentCenterPart_StoredUnderOtherRoot_FoundByRelativeTail()
    {
        var actual = Create(Path.Combine(ContentCenter, "ru-RU", "Заклёпки", "Шестигранная резьбовая заклепка M5.ipt"));
        var stored = Path.Combine(_root, "Users", "ilyap", "Documents", "Content Center Files", "ru-RU", "Заклёпки", "Шестигранная резьбовая заклепка M5.ipt");

        Assert.Equal(actual, ProjectSearchPaths.Locate(stored, [Project, ContentCenter]));
    }

    [Fact]
    public void LongestTailWins()
    {
        var deep = Create(Path.Combine(ContentCenter, "A", "B", "x.ipt"));
        Create(Path.Combine(ContentCenter, "B", "x.ipt"));
        Assert.Equal(deep, ProjectSearchPaths.Locate(Path.Combine(_root, "old", "A", "B", "x.ipt"), [Project, ContentCenter]));
    }

    [Fact]
    public void BareFileName_InProjectRoot_NotFound_InLibraryRoot_Found()
    {
        Create(Path.Combine(Project, "x.ipt"));
        var reference = Path.Combine(_root, "old", "x.ipt");
        Assert.Null(ProjectSearchPaths.Locate(reference, [Project]));
        var library = Create(Path.Combine(ContentCenter, "x.ipt"));
        Assert.Equal(library, ProjectSearchPaths.Locate(reference, [Project, ContentCenter]));
    }

    [Fact]
    public void ProjectMovedToOtherDisk_FoundInWorkspace()
    {
        var actual = Create(Path.Combine(Project, "Узлы", "Корпус.iam"));
        Assert.Equal(actual, ProjectSearchPaths.Locate(Path.Combine(_root, "Старый диск", "Проект", "Узлы", "Корпус.iam"), [Project]));
    }

    [Theory]
    [InlineData("")]
    [InlineData("x.ipt")]
    [InlineData("::??")]
    public void Degenerate_NoThrow(string reference) => Assert.Null(ProjectSearchPaths.Locate(reference, [Project, ContentCenter]));

    [Fact]
    public void Folders_FromIpj_AndProjectRoot()
    {
        Directory.CreateDirectory(Project);
        Directory.CreateDirectory(ContentCenter);
        var ipj = Path.Combine(Project, "Проект.ipj");
        File.WriteAllText(ipj, $"""
            <InventorProject>
              <Workspace><Path>.\</Path></Workspace>
              <Libraries><Library><LibraryName>PdmLite</LibraryName><Path>{ContentCenter}</Path></Library></Libraries>
              <ContentCenterPath>{Path.Combine(_root, "нет такой")}</ContentCenterPath>
            </InventorProject>
            """);

        var folders = new ProjectSearchPaths(new Session(new ProjectInfo("Проект", Project, ipj))).Folders();

        Assert.Equal(Project, folders[0]);
        Assert.Contains(ContentCenter, folders);
        Assert.DoesNotContain(folders, f => f.EndsWith("нет такой"));
        Assert.Equal(folders.Count, folders.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    private ProjectSearchPaths Search() => new(new Session(new ProjectInfo("Проект", Project)));

    [Fact]
    public void NextToReferencingDocument_Found()
    {
        var assembly = Create(Path.Combine(Project, "Сборка платформа", "Ящик.iam"));
        var rivet = Create(Path.Combine(Project, "Сборка платформа", "Заклепка M5.ipt"));
        Create(Path.Combine(Project, "Другое", "Заклепка M5.ipt")); // по имени — неоднозначно, но рядом со сборкой есть
        Assert.Equal(rivet, Search().Locate(Path.Combine(Project, "Старая папка", "Заклепка M5.ipt"), [assembly]));
    }

    [Fact]
    public void UniqueNameInProjectSubfolder_Found_Ambiguous_NotFound()
    {
        var rivet = Create(Path.Combine(Project, "Крепёж", "Покупные", "Заклепка M6.ipt"));
        var search = Search();
        Assert.Equal(rivet, search.Locate(Path.Combine(Project, "Сборка", "Заклепка M6.ipt")));

        Create(Path.Combine(Project, "Ещё", "Заклепка M8.ipt"));
        Create(Path.Combine(Project, "Ещё2", "Заклепка M8.ipt"));
        Assert.Null(new ProjectSearchPaths(new Session(new ProjectInfo("Проект", Project))).Locate(Path.Combine(Project, "Сборка", "Заклепка M8.ipt")));
    }

    [Fact]
    public void OldVersions_NotUsedForNameSearch()
    {
        Create(Path.Combine(Project, "OldVersions", "Заклепка M5.0001.ipt"));
        Create(Path.Combine(Project, "OldVersions", "Заклепка M5.ipt"));
        Assert.Null(Search().Locate(Path.Combine(Project, "Сборка", "Заклепка M5.ipt")));
    }

    [Fact]
    public void NoProject_NothingFound() =>
        Assert.Null(new ProjectSearchPaths(new Session(null)).Locate(Path.Combine(_root, "a", "b.ipt")));

    [PostgresFact]
    public async Task Index_ContentCenterPart_NotMissing()
    {
        await using var db = await TestDatabase.CreateAsync();
        var actual = Create(Path.Combine(ContentCenter, "ru-RU", "Заклёпки", "M5.ipt"));
        var stored = Path.Combine(_root, "Users", "Documents", "Content Center Files", "ru-RU", "Заклёпки", "M5.ipt");
        Directory.CreateDirectory(Project);
        File.WriteAllLines(Path.Combine(Project, "Узел.iam"), ["ref:" + stored]);
        var ipj = Path.Combine(Project, "Проект.ipj");
        File.WriteAllText(ipj, $"<InventorProject><ContentCenterPath>{ContentCenter}</ContentCenterPath></InventorProject>");
        var store = new FileBackedStore();
        var index = new ReferenceIndexService(db.Database, store, NullLogger<ReferenceIndexService>.Instance, [],
            new ProjectSearchPaths(new Session(new ProjectInfo("Проект", Project, ipj))));
        await index.ScanAsync(Project);

        var file = Assert.Single(await index.MissingFilesAsync(Project));

        Assert.Equal(actual, file.FoundAt);
    }

    public void Dispose() => Directory.Delete(_root, true);
}
