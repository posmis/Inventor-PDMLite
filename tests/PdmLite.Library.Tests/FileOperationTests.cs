using Microsoft.Extensions.Logging.Abstractions;
using PdmLite.Core.Documents;
using PdmLite.Data;
using PdmLite.Index;
using PdmLite.Index.Operations;

namespace PdmLite.Library.Tests;

/// <summary>
/// Хранилище, у которого свойства и ссылки — внутри самого файла (строки «ref:», «pn:», «title:»): копия файла
/// несёт его ссылки, как настоящий документ Inventor.
/// </summary>
public sealed class FileBackedStore : IDocumentPropertyStore
{
    public string? FailReplaceFor { get; set; }
    public string? IgnoreReplaceFor { get; set; }
    public string? FailWriteFor { get; set; }
    public List<string> Replaced { get; } = [];

    public bool IsAvailable => true;
    public string? UnavailableReason => null;

    public static void Create(string path, string? partNumber = null, string? title = null, params string[] references)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllLines(path, references.Select(r => "ref:" + r)
            .Concat(partNumber is null ? [] : ["pn:" + partNumber])
            .Concat(title is null ? [] : ["title:" + title]));
    }

    public static IReadOnlyList<string> References(string path) =>
        File.ReadAllLines(path).Where(l => l.StartsWith("ref:")).Select(l => l[4..]).ToList();

    public Task<DocumentProperties> ReadAsync(string path, CancellationToken ct = default) => ReadWithoutGeometryAsync(path, ct);

    public int Reads { get; private set; }

    public Task<DocumentProperties> ReadWithoutGeometryAsync(string path, CancellationToken ct = default)
    {
        Reads++;
        var lines = File.ReadAllLines(path);
        return Task.FromResult(new DocumentProperties
        {
            References = lines.Where(l => l.StartsWith("ref:")).Select(l => l[4..]).ToList(),
            PartNumber = lines.FirstOrDefault(l => l.StartsWith("pn:"))?[3..],
            Title = lines.FirstOrDefault(l => l.StartsWith("title:"))?[6..],
        });
    }

    public Task WriteAsync(string path, DocumentPropertyChanges changes, CancellationToken ct = default)
    {
        if (FailWriteFor is not null && path.Contains(FailWriteFor))
            throw new IOException("Файл повреждён");
        var lines = File.ReadAllLines(path).ToList();
        if (changes.PartNumber is not null)
        {
            lines.RemoveAll(l => l.StartsWith("pn:"));
            lines.Add("pn:" + changes.PartNumber);
        }
        if (changes.Title is not null)
        {
            lines.RemoveAll(l => l.StartsWith("title:"));
            lines.Add("title:" + changes.Title);
        }
        File.WriteAllLines(path, lines);
        return Task.CompletedTask;
    }

    public Task ReplaceReferencesAsync(string path, IReadOnlyDictionary<string, string> map, CancellationToken ct = default)
    {
        if (FailReplaceFor is not null && path.Contains(FailReplaceFor))
            throw new IOException("Документ не сохраняется");
        Replaced.Add(path);
        if (IgnoreReplaceFor is not null && path.Contains(IgnoreReplaceFor))
            return Task.CompletedTask; // как Inventor API для чертежа: ничего не заменил и не сообщил
        var lines = File.ReadAllLines(path).Select(l => l.StartsWith("ref:") && map.TryGetValue(l[4..], out var to) ? "ref:" + to : l);
        File.WriteAllLines(path, lines.ToList());
        return Task.CompletedTask;
    }
}

/// <summary>Документы, «открытые в Inventor», для проверки отказа.</summary>
public sealed class FakeOpenDocuments(params string[] open) : IOpenDocuments
{
    public bool CanClose => false;

    public Task<IReadOnlyList<OpenDocument>> ListAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<OpenDocument>>(open.Select(p => new OpenDocument(p, false, true)).ToList());

    public Task<IReadOnlyList<string>> CloseAsync(IReadOnlyCollection<string> affected, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<string>>([]);

    public Task OpenAsync(IReadOnlyList<string> paths, CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>Правило имён: «{Обозначение}{Код} {Наименование}», связанные файлы, списки переименования и перемещения.</summary>
public class FileOperationRequestTests
{
    private static string P(params string[] parts) => Path.Combine([Path.GetTempPath(), "proj", .. parts]);

    [Fact]
    public void RelatedInFolder_ByDesignation_AnyCodeAndExtension()
    {
        var model = P("АБВГ.00.00.000СБ Жгут.iam");
        string[] files =
        [
            model, P("АБВГ.00.00.000СБ Жгут.dwg"), P("АБВГ.00.00.000МЭ Жгут.dwg"), P("АБВГ.00.00.000СБ Жгут.pdf"),
            P("абвг.00.00.000 Жгут старый.ipn"), P("АБВГ.00.00.001СБ Другой.iam"), P("~$АБВГ.00.00.000СБ Жгут.dwg"),
            P("АБВГ.00.00.000СБ Жгут.dwg.bak"), P("АБВГ.00.00.000-01СБ Жгут.dwg"),
        ];
        var related = FileOperationRequests.RelatedInFolder(model, files).Select(Path.GetFileName);
        Assert.Equal(new[] { "абвг.00.00.000 Жгут старый.ipn", "АБВГ.00.00.000МЭ Жгут.dwg", "АБВГ.00.00.000СБ Жгут.dwg", "АБВГ.00.00.000СБ Жгут.pdf" }
            .Order(StringComparer.Ordinal), related.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void RelatedInFolder_NameNotByRule_SameStem()
    {
        var model = P("Кронштейн.ipt");
        var related = FileOperationRequests.RelatedInFolder(model, [model, P("Кронштейн.idw"), P("кронштейн.DWG"), P("Кронштейн левый.idw")]);
        Assert.Equal(["кронштейн.DWG", "Кронштейн.idw"], related.Select(Path.GetFileName)); // естественный порядок
    }

    [Fact]
    public void Rename_KeepsCodesAndExtensions()
    {
        var moves = FileOperationRequests.Rename(
            [P("АБВГ.00.00.000СБ Жгут.iam"), P("АБВГ.00.00.000СБ Жгут.dwg"), P("АБВГ.00.00.000МЭ Жгут.dwg"), P("АБВГ.00.00.000 Жгут.ipn")],
            "АБВГ.01.00.000", "Жгут питания");
        Assert.Equal(["АБВГ.01.00.000СБ Жгут питания.iam", "АБВГ.01.00.000СБ Жгут питания.dwg", "АБВГ.01.00.000МЭ Жгут питания.dwg",
            "АБВГ.01.00.000 Жгут питания.ipn"], moves.Select(m => Path.GetFileName(m.To)));
        Assert.All(moves, m => Assert.Equal(Path.GetDirectoryName(m.From), Path.GetDirectoryName(m.To)));
    }

    [Fact]
    public void Rename_UnchangedSkipped_CaseOnlyKept()
    {
        var same = FileOperationRequests.Rename([P("АБВГ.00.00.000 Жгут.iam")], "АБВГ.00.00.000", "Жгут");
        Assert.Empty(same);
        var caseOnly = Assert.Single(FileOperationRequests.Rename([P("АБВГ.00.00.000 жгут.iam")], "АБВГ.00.00.000", "Жгут"));
        Assert.True(caseOnly.IsCaseOnly);
    }

    [Fact]
    public void Rename_NotByRule_GetsRuleName_WithoutCode()
    {
        var move = Assert.Single(FileOperationRequests.Rename([P("Кронштейн.ipt")], "АБВГ.00.00.005", "Кронштейн"));
        Assert.Equal("АБВГ.00.00.005 Кронштейн.ipt", Path.GetFileName(move.To));
        var noDesignation = Assert.Single(FileOperationRequests.Rename([P("Кронштейн.ipt")], "", "Кронштейн левый"));
        Assert.Equal("Кронштейн левый.ipt", Path.GetFileName(noDesignation.To));
        Assert.Throws<ArgumentException>(() => FileOperationRequests.Rename([P("a.ipt")], " ", " "));
    }

    [Fact]
    public void RenameProperties_DrawingGetsCode_ModelsWithout_OthersSkipped()
    {
        var props = FileOperationRequests.RenameProperties(
            [P("АБВГ.00.00.000СБ Жгут.iam"), P("АБВГ.00.00.000СБ Жгут.dwg"), P("АБВГ.00.00.000МЭ Жгут.idw"), P("АБВГ.00.00.000СБ Жгут.pdf")],
            "АБВГ.01.00.000", "Жгут питания");
        Assert.Equal(["АБВГ.01.00.000", "АБВГ.01.00.000СБ", "АБВГ.01.00.000МЭ"], props.Select(p => p.PartNumber));
        Assert.All(props, p => Assert.Equal("Жгут питания", p.Title));
    }

    [Fact]
    public void Move_FilesAndFolders_OldVersionsSkipped()
    {
        var target = P("Новая");
        var folder = P("Узел");
        var files = new Dictionary<string, string[]>
        {
            [folder] = [P("Узел", "a.ipt"), P("Узел", "sub", "b.ipt"), P("Узел", "OldVersions", "a.ipt")],
        };
        var moves = FileOperationRequests.Move([P("c.iam"), folder], target, f => files[f], files.ContainsKey);
        Assert.Equal([P("Новая", "c.iam"), P("Новая", "Узел", "a.ipt"), P("Новая", "Узел", "sub", "b.ipt")], moves.Select(m => m.To));
    }

    [Fact]
    public void Move_SameFolder_Nothing() => Assert.Empty(FileOperationRequests.Move([P("c.iam")], P(), _ => []));

    [Theory]
    [InlineData("~$a.dwg", true)]
    [InlineData("a.lck", true)]
    [InlineData("a.BAK", true)]
    [InlineData("a.dwg", false)]
    public void IsTemporary(string name, bool expected) => Assert.Equal(expected, FileOperationRequests.IsTemporary(P(name)));
}

/// <summary>Переименование и перемещение с переносом ссылок: без базы (чтение всех документов) и с индексом.</summary>
public sealed class FileOperationTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("pdmlite-ops-").FullName;
    private string Project => Path.Combine(_root, "Проект");
    private string F(params string[] parts) => Path.Combine([Project, .. parts]);

    private ProjectFileOperations Service(FileBackedStore store, IPdmDatabase? database = null, IOpenDocuments? open = null)
    {
        database ??= new PdmDatabase(new DatabaseSettings(), NullLoggerFactory.Instance);
        return new ProjectFileOperations(database, store, new ReferenceIndexService(database, store, NullLogger<ReferenceIndexService>.Instance),
            open ?? new NoOpenDocuments(), NullLogger<ProjectFileOperations>.Instance) { BackupRoot = Path.Combine(_root, "ops") };
    }

    /// <summary>Жгут: сборка, её сборочный и электромонтажный чертежи, деталь; верхняя сборка ссылается на жгут.</summary>
    private (string Harness, string Drawing, string Wiring, string Part, string Top) Harness()
    {
        var part = F("Жгут", "АБВГ.00.00.001 Провод.ipt");
        var harness = F("Жгут", "АБВГ.00.00.000СБ Жгут.iam");
        var drawing = F("Жгут", "АБВГ.00.00.000СБ Жгут.dwg");
        var wiring = F("Жгут", "АБВГ.00.00.000МЭ Жгут.dwg");
        var top = F("АБВГ.10.00.000СБ Изделие.iam");
        FileBackedStore.Create(part, "АБВГ.00.00.001", "Провод");
        FileBackedStore.Create(harness, "АБВГ.00.00.000", "Жгут", part);
        FileBackedStore.Create(drawing, "АБВГ.00.00.000СБ", "Жгут", harness);
        FileBackedStore.Create(wiring, "АБВГ.00.00.000МЭ", "Жгут", harness, part);
        FileBackedStore.Create(top, "АБВГ.10.00.000", "Изделие", harness);
        return (harness, drawing, wiring, part, top);
    }

    private async Task<(FileOperationPlan Plan, FileOperationResult Result)> RenameAsync(ProjectFileOperations service, string model, string des, string name)
    {
        var related = await service.RelatedFilesAsync(Project, model);
        var files = new[] { model }.Concat(related).ToList();
        var plan = await service.PlanAsync(Project, FileOperationRequests.Rename(files, des, name), FileOperationRequests.RenameProperties(files, des, name));
        Assert.True(plan.CanExecute, string.Join("; ", plan.Problems));
        return (plan, await service.ExecuteAsync(plan));
    }

    [Fact]
    public async Task Rename_Harness_RenamesDrawingsTogether_UpdatesAllReferences_AndProperties()
    {
        var (harness, drawing, wiring, part, top) = Harness();
        var store = new FileBackedStore();
        var service = Service(store);

        var (plan, result) = await RenameAsync(service, harness, "АБВГ.01.00.000", "Жгут питания");

        var newHarness = F("Жгут", "АБВГ.01.00.000СБ Жгут питания.iam");
        var newDrawing = F("Жгут", "АБВГ.01.00.000СБ Жгут питания.dwg");
        var newWiring = F("Жгут", "АБВГ.01.00.000МЭ Жгут питания.dwg");
        Assert.Equal(3, result.Moved);
        Assert.False(File.Exists(harness) || File.Exists(drawing) || File.Exists(wiring));
        Assert.Equal([newHarness], FileBackedStore.References(newDrawing));
        Assert.Equal([newHarness, part], FileBackedStore.References(newWiring));
        Assert.Equal([newHarness], FileBackedStore.References(top));
        Assert.Equal([part], FileBackedStore.References(newHarness)); // ссылки на неизменные — как были
        Assert.Equal(3, result.ReferencesUpdated); // верхняя сборка и два чертежа (уже по новым путям)
        Assert.Contains(plan.References, r => r.Document == drawing && r.Target == newDrawing);

        var props = await store.ReadWithoutGeometryAsync(newWiring);
        Assert.Equal("АБВГ.01.00.000МЭ", props.PartNumber);
        Assert.Equal("Жгут питания", props.Title);
        Assert.Equal("АБВГ.01.00.000", (await store.ReadWithoutGeometryAsync(newHarness)).PartNumber);
        Assert.True(File.Exists(Path.Combine(result.BackupFolder, "журнал.txt")));
        Assert.Single(Directory.GetFiles(result.BackupFolder, "0*")); // на месте изменена только верхняя сборка
    }

    [Fact]
    public async Task Move_Part_ToOtherFolder_ReferencesFollow()
    {
        var (harness, _, wiring, part, _) = Harness();
        var store = new FileBackedStore();
        var service = Service(store);
        var target = F("Детали");

        var plan = await service.PlanAsync(Project, FileOperationRequests.Move([part], target, _ => []), []);
        await service.ExecuteAsync(plan);

        var moved = Path.Combine(target, "АБВГ.00.00.001 Провод.ipt");
        Assert.True(File.Exists(moved));
        Assert.False(File.Exists(part));
        Assert.Equal([moved], FileBackedStore.References(harness));
        Assert.Contains(moved, FileBackedStore.References(wiring));
    }

    [Fact]
    public async Task Move_Folder_Whole_EmptySourceRemoved_InnerReferencesFollow()
    {
        var (harness, drawing, _, part, top) = Harness();
        var service = Service(new FileBackedStore());
        var target = F("Изделия");
        Directory.CreateDirectory(target);

        var plan = await service.PlanAsync(Project, FileOperationRequests.Move([F("Жгут")], target,
            f => Directory.EnumerateFiles(f, "*", SearchOption.AllDirectories)), []);
        await service.ExecuteAsync(plan);

        var newHarness = Path.Combine(target, "Жгут", Path.GetFileName(harness));
        Assert.False(Directory.Exists(F("Жгут")));
        Assert.Equal([newHarness], FileBackedStore.References(top));
        Assert.Equal([Path.Combine(target, "Жгут", Path.GetFileName(part))], FileBackedStore.References(newHarness));
        Assert.Equal([newHarness], FileBackedStore.References(Path.Combine(target, "Жгут", Path.GetFileName(drawing))));
    }

    [Fact]
    public async Task ReplaceFails_EverythingRolledBack()
    {
        var (harness, drawing, wiring, _, top) = Harness();
        var topBefore = File.ReadAllText(top);
        var store = new FileBackedStore { FailReplaceFor = "МЭ" };
        var service = Service(store);
        var files = new[] { harness, drawing, wiring };
        var plan = await service.PlanAsync(Project, FileOperationRequests.Rename(files, "АБВГ.02.00.000", "Жгут"), []);

        var error = await Assert.ThrowsAsync<FileOperationException>(() => service.ExecuteAsync(plan));

        Assert.Contains("отменены", error.Message);
        Assert.True(File.Exists(harness) && File.Exists(drawing) && File.Exists(wiring));
        Assert.Empty(Directory.GetFiles(F("Жгут"), "АБВГ.02*"));
        Assert.Equal(topBefore, File.ReadAllText(top)); // верхняя сборка восстановлена из копии
    }

    [Fact]
    public async Task ReferencesSilentlyNotReplaced_Detected_RolledBack()
    {
        var (harness, _, _, _, top) = Harness();
        var service = Service(new FileBackedStore { IgnoreReplaceFor = "Изделие" });
        var plan = await service.PlanAsync(Project, FileOperationRequests.Rename([harness], "АБВГ.03.00.000", "Жгут"), []);

        var error = await Assert.ThrowsAsync<FileOperationException>(() => service.ExecuteAsync(plan));

        Assert.Contains("не заменились ссылки", error.Message);
        Assert.True(File.Exists(harness));
        Assert.Equal([harness], FileBackedStore.References(top));
    }

    [Fact]
    public async Task PropertyWriteFails_RolledBack()
    {
        var (harness, _, _, _, top) = Harness();
        var service = Service(new FileBackedStore { FailWriteFor = "АБВГ.04" });
        var files = new[] { harness };
        var plan = await service.PlanAsync(Project, FileOperationRequests.Rename(files, "АБВГ.04.00.000", "Жгут"),
            FileOperationRequests.RenameProperties(files, "АБВГ.04.00.000", "Жгут"));

        await Assert.ThrowsAsync<FileOperationException>(() => service.ExecuteAsync(plan));

        Assert.True(File.Exists(harness));
        Assert.Equal([harness], FileBackedStore.References(top));
        Assert.Empty(Directory.GetFiles(F("Жгут"), "АБВГ.04*"));
    }

    [Fact]
    public async Task Plan_Problems_BlockExecution()
    {
        var (harness, drawing, _, part, _) = Harness();
        var outside = Path.Combine(_root, "Вне проекта", "x.iam");
        var service = Service(new FileBackedStore());

        var plan = await service.PlanAsync(Project,
        [
            new FileMove(harness, drawing.Replace(".dwg", ".iam").Replace("СБ Жгут", "СБ Занято")), // ок
            new FileMove(part, drawing), // занято
            new FileMove(F("нет.ipt"), F("нет2.ipt")), // нет файла
            new FileMove(drawing, outside), // вне проекта
        ], []);

        Assert.False(plan.CanExecute);
        Assert.Contains(plan.Problems, p => p.Contains("уже есть") || p.Contains("занято"));
        Assert.Contains(plan.Problems, p => p.Contains("не найден"));
        Assert.Contains(plan.Problems, p => p.Contains("внутри папки проекта"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ExecuteAsync(plan));
    }

    [Theory]
    [InlineData("a|b.ipt")]
    [InlineData("a?.ipt")]
    [InlineData("a*.ipt")]
    public async Task Plan_InvalidCharacters(string name)
    {
        var (_, _, _, part, _) = Harness();
        var plan = await Service(new FileBackedStore()).PlanAsync(Project, [new FileMove(part, F(name))], []);
        Assert.Contains(plan.Problems, p => p.Contains("недопустимые"));
    }

    [Fact]
    public async Task Plan_TwoFilesSameTarget_And_TooLong()
    {
        var (harness, _, _, part, _) = Harness();
        var plan = await Service(new FileBackedStore()).PlanAsync(Project,
            [new FileMove(harness, F("x.iam")), new FileMove(part, F("x.iam")), new FileMove(F("Жгут", "АБВГ.00.00.000МЭ Жгут.dwg"), F(new string('a', 300) + ".dwg"))], []);
        Assert.Contains(plan.Problems, p => p.Contains("одно имя"));
        Assert.Contains(plan.Problems, p => p.Contains("длинный"));
    }

    [Fact]
    public async Task Plan_ReadOnlyParent_Problem()
    {
        var (harness, _, _, _, top) = Harness();
        File.SetAttributes(top, FileAttributes.ReadOnly);
        var plan = await Service(new FileBackedStore()).PlanAsync(Project, FileOperationRequests.Rename([harness], "АБВГ.05.00.000", "Жгут"), []);
        Assert.Contains(plan.Problems, p => p.Contains("только для чтения"));
    }

    [Fact]
    public async Task Plan_UnreferencedModel_Warning()
    {
        var lonely = F("АБВГ.99.00.000 Одинокая.ipt");
        FileBackedStore.Create(lonely);
        var plan = await Service(new FileBackedStore()).PlanAsync(Project, FileOperationRequests.Rename([lonely], "АБВГ.99.00.001", "Одинокая"), []);
        Assert.True(plan.CanExecute);
        Assert.Contains(plan.Warnings, w => w.Contains("никто не ссылается"));
    }

    [Fact]
    public async Task Execute_FileOpenInInventor_Refused_NothingChanged()
    {
        var (harness, _, _, _, top) = Harness();
        var service = Service(new FileBackedStore(), open: new FakeOpenDocuments(top.ToUpperInvariant()));
        var plan = await service.PlanAsync(Project, FileOperationRequests.Rename([harness], "АБВГ.06.00.000", "Жгут"), []);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.ExecuteAsync(plan));

        Assert.Contains("Закройте в Inventor", error.Message);
        Assert.True(File.Exists(harness));
    }

    [Fact]
    public async Task CaseOnlyRename_NoReferenceChanges()
    {
        var (harness, _, _, _, top) = Harness();
        var store = new FileBackedStore();
        var service = Service(store);
        var to = F("Жгут", "АБВГ.00.00.000СБ ЖГУТ.iam");

        var plan = await service.PlanAsync(Project, [new FileMove(harness, to)], []);
        Assert.Empty(plan.References);
        await service.ExecuteAsync(plan);

        Assert.Contains(Directory.GetFiles(F("Жгут")), f => Path.GetFileName(f) == "АБВГ.00.00.000СБ ЖГУТ.iam");
        Assert.Empty(store.Replaced);
    }

    [Fact]
    public async Task RelatedFiles_DrawingInOtherFolder_ByDesignation()
    {
        var (harness, _, _, _, _) = Harness();
        var elsewhere = F("Чертежи", "АБВГ.00.00.000ВО Жгут.idw");
        var foreign = F("Чертежи", "Схема общая.idw");
        FileBackedStore.Create(elsewhere, references: harness);
        FileBackedStore.Create(foreign, references: harness);

        var related = await Service(new FileBackedStore()).RelatedFilesAsync(Project, harness);

        Assert.Contains(elsewhere, related);
        Assert.DoesNotContain(foreign, related); // ссылается, но это другой документ — у него только обновятся ссылки
        Assert.Equal(3, related.Count);
    }

    [Fact]
    public async Task OldVersions_NotScannedAsParents()
    {
        var (harness, _, _, _, _) = Harness();
        var old = F("Жгут", "OldVersions", "АБВГ.10.00.000СБ Изделие.0001.iam");
        FileBackedStore.Create(old, references: harness);
        var plan = await Service(new FileBackedStore()).PlanAsync(Project, FileOperationRequests.Rename([harness], "АБВГ.07.00.000", "Жгут"), []);
        Assert.DoesNotContain(plan.References, r => r.Document == old);
    }

    [Fact]
    public async Task WithoutDatabase_SecondScan_ReadsOnlyChangedDocuments()
    {
        var (harness, _, _, part, top) = Harness();
        var store = new FileBackedStore();
        var service = Service(store);
        await service.RelatedFilesAsync(Project, harness);
        var first = store.Reads;

        File.AppendAllText(top, "pn:x" + Environment.NewLine); // изменили один документ
        await service.PlanAsync(Project, FileOperationRequests.Rename([part], "АБВГ.00.00.009", "Провод"), []);

        Assert.Equal(5, first);
        Assert.Equal(first + 1, store.Reads);
    }

    [PostgresFact]
    public async Task WithIndex_SameResult_IndexUpdated()
    {
        await using var db = await TestDatabase.CreateAsync();
        var (harness, _, _, _, top) = Harness();
        var store = new FileBackedStore();
        var service = Service(store, db.Database);

        var (_, result) = await RenameAsync(service, harness, "АБВГ.08.00.000", "Жгут");

        var newHarness = F("Жгут", "АБВГ.08.00.000СБ Жгут.iam");
        Assert.Equal([newHarness], FileBackedStore.References(top));
        var index = new ReferenceIndexService(db.Database, store, NullLogger<ReferenceIndexService>.Instance);
        Assert.Contains(await index.WhereUsedAsync(newHarness, false), d => d.Path == top);
        Assert.Empty(await index.WhereUsedAsync(harness, false));
        Assert.Empty(result.Warnings);
    }

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(_root, true);
    }
}
