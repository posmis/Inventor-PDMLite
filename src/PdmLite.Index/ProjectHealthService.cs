using System.Security.Cryptography;
using PdmLite.Core.Documents;
using PdmLite.Core.Domain;

namespace PdmLite.Index;

public sealed record ProjectHealthIssue(string Severity, string Category, string Path, string Details);
public sealed record ProjectHealthCard(
    int Version, ProjectInfo Project, DateTime CreatedUtc, int Files, int Read,
    int Parts, int Assemblies, int Drawings, int Presentations,
    Dictionary<string, string> Inputs, List<ProjectHealthIssue> Issues, int AnalysisVersion = 1)
{
    public int Errors => Issues.Count(i => i.Severity == "Ошибка");
    public int Warnings => Issues.Count(i => i.Severity == "Предупреждение");
    public string Summary => Errors > 0 ? "Обнаружены ошибки" : Warnings > 0 ? "Есть замечания" : "Проверки пройдены";
}

/// <summary>Полный анализ файлов проекта, независимый от индекса PostgreSQL. Одна последняя карточка на проект.</summary>
public sealed class ProjectHealthService(
    IDocumentPropertyStore properties, string folder,
    Func<ProjectInfo, string, IEnumerable<string>, string?>? locate = null)
{
    private const int CurrentAnalysisVersion = 3;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ProjectHealthCardStore _cards = new(folder);
    public string CardPath(ProjectInfo project) => _cards.CardPath(project);
    public Task<ProjectHealthCard?> LoadAsync(ProjectInfo project, CancellationToken ct = default) => _cards.LoadAsync(project, ct);

    public async Task<bool> IsCurrentAsync(ProjectHealthCard card, CancellationToken ct = default)
    {
        ProjectHealthCardStore.Validate(card);
        if (card.AnalysisVersion != CurrentAnalysisVersion) return false;
        var current = await SnapshotAsync(card.Project, ct);
        foreach (var input in card.Inputs.Where(i => !current.ContainsKey(i.Key)))
            current[input.Key] = Stamp(input.Key);
        return Equal(card.Inputs, current);
    }

    public async Task<ProjectHealthCard> AnalyzeAsync(ProjectInfo project, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (!properties.IsAvailable)
            throw new InvalidOperationException(properties.UnavailableReason ?? "Нет доступа к свойствам Inventor.");
        await _gate.WaitAsync(ct);
        try
        {
            progress?.Report("Проверка файлов проекта…");
            var before = await SnapshotAsync(project, ct);
            var files = before.Keys.Where(IsDocument).ToList();
            var issues = new List<ProjectHealthIssue>();
            var documents = new List<(string Path, DocumentProperties Properties)>();
            var usedModels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var usageComplete = true;
            var inputs = new Dictionary<string, string>(before, StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < files.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var path = files[i];
                progress?.Report($"Чтение {i + 1} из {files.Count}: {Path.GetFileName(path)}");
                DocumentProperties props;
                try { props = await properties.ReadWithoutGeometryAsync(path, ct); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    usageComplete = false;
                    issues.Add(new("Ошибка", "Не прочитан", path, ex.Message));
                    continue;
                }
                documents.Add((path, props));
                if (string.IsNullOrWhiteSpace(props.PartNumber))
                    issues.Add(new("Предупреждение", "Нет обозначения", path, "Не заполнено свойство Part Number."));
                if (string.IsNullOrWhiteSpace(props.Title))
                    issues.Add(new("Предупреждение", "Нет наименования", path, "Не заполнено свойство Title."));
                if (props.IsContentCenter)
                    issues.Add(new("Предупреждение", "Content Center", path, "В файле осталась метка Content Center."));
                foreach (var reference in props.References.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        var target = Path.GetFullPath(reference);
                        if (!inputs.ContainsKey(target)) inputs[target] = Stamp(target);
                        if (File.Exists(target))
                        {
                            if (ReferenceIndexService.Key(target) != ReferenceIndexService.Key(path))
                                usedModels.Add(ReferenceIndexService.Key(target));
                            continue;
                        }
                        var found = locate?.Invoke(project, target, [path]);
                        if (found is not null && File.Exists(found))
                        {
                            found = Path.GetFullPath(found);
                            if (!inputs.ContainsKey(found)) inputs[found] = Stamp(found);
                            if (ReferenceIndexService.Key(found) != ReferenceIndexService.Key(path))
                                usedModels.Add(ReferenceIndexService.Key(found));
                            issues.Add(new("Предупреждение", "Ссылка по другому пути", path, $"{target} → {found}"));
                        }
                        else
                        {
                            usageComplete = false;
                            issues.Add(new("Ошибка", "Ненайденная ссылка", path, target));
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    { usageComplete = false; issues.Add(new("Ошибка", "Проверка ссылки", path, $"{reference}: {ex.Message}")); }
                }
            }
            progress?.Report("Поиск деталей и сборок без использований…");
            if (usageComplete)
            {
                foreach (var path in files.Where(p => DocumentKinds.FromPath(p) is DocumentKind.Part or DocumentKind.Assembly))
                {
                    ct.ThrowIfCancellationRequested();
                    if (usedModels.Contains(ReferenceIndexService.Key(path))) continue;
                    var assembly = DocumentKinds.FromPath(path) == DocumentKind.Assembly;
                    issues.Add(new("Предупреждение", assembly ? "Неиспользуемые сборки" : "Неиспользуемые детали", path,
                        "На этот файл не ссылается ни один другой прочитанный документ проекта (сборка, чертёж, презентация или деталь). " +
                        (assembly ? "Это может быть самостоятельная или главная сборка проекта. " : "Это может быть самостоятельная деталь. ") +
                        "Использование в других проектах не проверялось. Кандидат для проверки, не рекомендация к удалению."));
                }
            }
            else
                issues.Add(new("Предупреждение", "Поиск неиспользуемых не завершён", project.RootFolder,
                    "Не все документы или ссылки удалось прочитать. Список неиспользуемых деталей и сборок не сформирован, " +
                    "чтобы не пометить используемые файлы как лишние. Исправьте ошибки чтения и повторите анализ."));
            foreach (var group in files.GroupBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
                foreach (var path in group)
                    issues.Add(new("Предупреждение", "Одинаковое имя файла", path, string.Join(Environment.NewLine, group.Where(p => p != path))));
            // Модель и её чертёж могут иметь одно обозначение: сравниваем только модели одного типа.
            foreach (var group in documents.Where(d => !string.IsNullOrWhiteSpace(d.Properties.PartNumber)
                         && DocumentKinds.FromPath(d.Path) is DocumentKind.Part or DocumentKind.Assembly)
                         .GroupBy(d => (DocumentKinds.FromPath(d.Path), d.Properties.PartNumber!.Trim().ToUpperInvariant()))
                         .Where(g => g.Count() > 1))
                foreach (var doc in group)
                    issues.Add(new("Предупреждение", "Повтор обозначения", doc.Path,
                        string.Join(Environment.NewLine, group.Where(d => d.Path != doc.Path).Select(d => d.Path))));
            if (files.Count == 0) issues.Add(new("Предупреждение", "Пустой проект", project.RootFolder, "Документы Inventor не найдены."));
            var after = await SnapshotAsync(project, ct);
            if (!Equal(before, after) || inputs.Any(i => !before.ContainsKey(i.Key) && Stamp(i.Key) != i.Value))
                throw new IOException("Проект изменился во время анализа. Повторите анализ; предыдущая карточка сохранена.");
            int Count(DocumentKind kind) => files.Count(p => DocumentKinds.FromPath(p) == kind);
            var card = new ProjectHealthCard(1, project, DateTime.UtcNow, files.Count, documents.Count,
                Count(DocumentKind.Part), Count(DocumentKind.Assembly), Count(DocumentKind.Drawing), Count(DocumentKind.Presentation),
                inputs, issues.OrderBy(i => i.Severity == "Ошибка" ? 0 : 1).ThenBy(i => i.Category).ThenBy(i => i.Path).ToList(), CurrentAnalysisVersion);
            ct.ThrowIfCancellationRequested();
            await _cards.SaveAsync(card, ct);
            return card;
        }
        finally { _gate.Release(); }
    }

    private static bool IsDocument(string path) => DocumentKinds.FromPath(path) is
        DocumentKind.Part or DocumentKind.Assembly or DocumentKind.Drawing or DocumentKind.Presentation;
    private static bool Equal(Dictionary<string, string> a, Dictionary<string, string> b) =>
        a.Count == b.Count && a.All(i => b.TryGetValue(i.Key, out var value) && value == i.Value);
    private static string Stamp(string path)
    {
        var f = new FileInfo(path);
        return f.Exists ? $"{f.Length}:{f.LastWriteTimeUtc.Ticks}" : "missing";
    }
    private static async Task<Dictionary<string, string>> SnapshotAsync(ProjectInfo project, CancellationToken ct)
    {
        if (!Directory.Exists(project.RootFolder)) throw new DirectoryNotFoundException(project.RootFolder);
        // Не пропускаем недоступные каталоги молча: неполный обход не является полным анализом.
        var options = new EnumerationOptions { IgnoreInaccessible = false,
            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Hidden | FileAttributes.System };
        var paths = new List<string>();
        var pending = new Stack<string>();
        pending.Push(project.RootFolder);
        while (pending.TryPop(out var directory))
        {
            ct.ThrowIfCancellationRequested();
            paths.AddRange(Directory.EnumerateFiles(directory, "*", options).Where(p => IsDocument(p) || Path.GetExtension(p).Equals(".ipj", StringComparison.OrdinalIgnoreCase)));
            foreach (var child in Directory.EnumerateDirectories(directory, "*", options))
                if (!Path.GetFileName(child).Equals("OldVersions", StringComparison.OrdinalIgnoreCase)) pending.Push(child);
        }
        if (project.ProjectFile is not null) paths.Add(project.ProjectFile);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            var full = Path.GetFullPath(path);
            await using var stream = File.OpenRead(full);
            result[full] = "sha256:" + Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
        }
        return result;
    }
}
