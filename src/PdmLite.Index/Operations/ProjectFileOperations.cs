using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PdmLite.Core.Documents;
using PdmLite.Core.Domain;
using PdmLite.Core.Naming;
using PdmLite.Data;

namespace PdmLite.Index.Operations;

/// <summary>Замена ссылок в документе.</summary>
/// <param name="Document">Документ по пути до операции.</param>
/// <param name="Target">Где он будет к моменту замены (новый путь, если сам переименовывается).</param>
/// <param name="Map">Старый полный путь ссылки → новый.</param>
public sealed record ReferenceUpdate(string Document, string Target, IReadOnlyDictionary<string, string> Map);

/// <summary>Что будет сделано. Выполняется, только если нет <see cref="Problems"/>.</summary>
public sealed record FileOperationPlan(
    string Root,
    IReadOnlyList<FileMove> Moves,
    IReadOnlyList<ReferenceUpdate> References,
    IReadOnlyList<PropertyUpdate> Properties,
    IReadOnlyList<string> Problems,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<FileMove>? Folders = null)
{
    /// <summary>Папки, которые переносятся или переименовываются целиком (после файлов в них переезжает остальное, старая удаляется).</summary>
    public IReadOnlyList<FileMove> FolderMoves => Folders ?? [];

    public bool CanExecute => Problems.Count == 0 && (Moves.Count > 0 || Properties.Count > 0 || References.Count > 0 || FolderMoves.Count > 0);

    /// <summary>Все файлы, которые операция трогает (по путям до операции).</summary>
    public IReadOnlyList<string> InvolvedFiles =>
        Moves.Select(m => m.From).Concat(References.Select(r => r.Document)).Concat(Properties.Select(p => p.Path))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Новый путь файла после операции (или прежний, если файл не переносится).</summary>
    public string After(string path) =>
        Moves.FirstOrDefault(m => m.From.Equals(path, StringComparison.OrdinalIgnoreCase))?.To ?? path;
}

/// <param name="BackupFolder">Копии изменённых на месте документов и журнал операции.</param>
public sealed record FileOperationResult(int Moved, int ReferencesUpdated, int PropertiesWritten, string BackupFolder, IReadOnlyList<string> Warnings);

/// <summary>Потерянная ссылка: файла нет по пути, который записан в документах.</summary>
/// <param name="Missing">Путь, по которому документы ищут файл.</param>
/// <param name="UsedIn">Документы проекта с этой ссылкой.</param>
/// <param name="Candidates">Файлы проекта с тем же именем (куда, вероятно, его перенесли).</param>
/// <param name="Library">Файл найден в библиотеке (компонент переехал или переименован) — он и предлагается.</param>
public sealed record BrokenLink(string Missing, IReadOnlyList<string> UsedIn, IReadOnlyList<string> Candidates, string? Library = null)
{
    public string Name => Path.GetFileName(Missing);

    /// <summary>Что предложить: файл библиотеки, иначе единственный файл проекта с тем же именем.</summary>
    public string? Suggested => Library ?? (Candidates.Count == 1 ? Candidates[0] : null);
}

/// <summary>Операция не выполнена; всё, что успело измениться, возвращено.</summary>
public sealed class FileOperationException(string message, Exception inner) : Exception(message, inner);

/// <summary>
/// Переименование и перемещение файлов проекта с переносом ссылок. Порядок, чтобы при сбое всё вернуть:
/// 1) копии изменяемых на месте документов — в резервную папку; 2) файлы копируются по новым путям
/// (старые остаются — ссылки на них ещё находятся); 3) в документах, которые на них ссылаются,
/// ссылки заменяются и проверяются; 4) пишутся iProperties; 5) старые файлы удаляются.
/// Сбой на шагах 2–4 — новые копии удаляются, изменённые документы восстанавливаются из резервной папки.
/// Кто ссылается — по индексу ссылок (если база настроена, перед этим он обновляется) или чтением всех
/// документов проекта.
/// </summary>
public sealed class ProjectFileOperations(
    IPdmDatabase database,
    IDocumentPropertyStore store,
    ReferenceIndexService index,
    IOpenDocuments openDocuments,
    ILogger<ProjectFileOperations> logger)
{
    /// <summary>Длина полного пути, которую Inventor и Windows точно поддерживают.</summary>
    public const int MaxPathLength = 259;

    /// <summary>Папка журналов и резервных копий операций.</summary>
    public string BackupRoot { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PdmLite", "operations");

    /// <summary>
    /// Связанные с моделью файлы: в той же папке — по обозначению (<see cref="FileOperationRequests.RelatedInFolder"/>),
    /// и чертежи из других папок проекта, которые ссылаются на модель и названы по её обозначению.
    /// </summary>
    public async Task<IReadOnlyList<string>> RelatedFilesAsync(string root, string model, IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var folder = Path.GetDirectoryName(model)!;
        var related = FileOperationRequests.RelatedInFolder(model, Directory.EnumerateFiles(folder)).ToList();
        if (!store.IsAvailable || !DocumentKinds.IsKnown(model) || DocumentKinds.FromPath(model) == DocumentKind.Drawing)
            return related;

        var stem = Path.GetFileNameWithoutExtension(model);
        var parsed = FileNameBuilder.TryParse(stem);
        var parents = await ParentsAsync(root, [model], progress, ct).ConfigureAwait(false);
        foreach (var parent in parents.GetValueOrDefault(ReferenceIndexService.Key(model)) ?? [])
            if (DocumentKinds.FromPath(parent) == DocumentKind.Drawing
                && FileOperationRequests.IsRelated(parsed, stem, Path.GetFileNameWithoutExtension(parent))
                && !related.Contains(parent, StringComparer.OrdinalIgnoreCase) && !parent.Equals(model, StringComparison.OrdinalIgnoreCase))
                related.Add(parent);
        return related;
    }

    /// <summary>Только чтение: проверки и список изменений.</summary>
    /// <param name="folders">Папки, переносимые целиком (их файлы — в <paramref name="moves"/>).</param>
    public async Task<FileOperationPlan> PlanAsync(string root, IReadOnlyList<FileMove> moves, IReadOnlyList<PropertyUpdate> properties,
        IProgress<string>? progress = null, CancellationToken ct = default, IReadOnlyList<FileMove>? folders = null)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        folders = (folders ?? []).Select(f => new FileMove(Path.TrimEndingDirectorySeparator(Path.GetFullPath(f.From)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(f.To)))).ToList();
        moves = moves.Select(m => new FileMove(Path.GetFullPath(m.From), Path.GetFullPath(m.To))).ToList();
        properties = properties.Select(p => p with { Path = Path.GetFullPath(p.Path) }).ToList();
        var problems = new List<string>();
        var warnings = new List<string>();

        if (!Directory.Exists(root))
            problems.Add($"Нет папки проекта: {root}");
        if (!store.IsAvailable)
            problems.Add($"Нет доступа к файлам Inventor: {store.UnavailableReason}");

        var sources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var move in moves)
        {
            var name = Path.GetFileName(move.From);
            if (!sources.Add(move.From))
                problems.Add($"{name}: файл указан дважды.");
            if (!File.Exists(move.From))
                problems.Add($"{name}: файл не найден ({move.From}).");
            if (!Inside(root, move.From) || !Inside(root, move.To))
                problems.Add($"{name}: переименовывать и перемещать можно только внутри папки проекта.");
            if (move.To.Split(Path.DirectorySeparatorChar).Any(FileOperationRequests.IsOldVersions))
                problems.Add($"{name}: нельзя переносить в папку OldVersions.");
            if (Path.GetFileName(move.To).IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || Path.GetFileName(move.To).IndexOfAny(['<', '>', ':', '"', '|', '?', '*']) >= 0)
                problems.Add($"{name}: недопустимые символы в новом имени «{Path.GetFileName(move.To)}».");
            if (!targets.Add(move.To))
                problems.Add($"{Path.GetFileName(move.To)}: два файла получают одно имя.");
            if (move.To.Length > MaxPathLength)
                problems.Add($"{Path.GetFileName(move.To)}: слишком длинный путь ({move.To.Length} символов, допустимо {MaxPathLength}).");
            if (!move.IsCaseOnly && (File.Exists(move.To) || Directory.Exists(move.To)) && !sources.Contains(move.To))
                problems.Add($"{Path.GetFileName(move.To)}: файл с таким именем уже есть в {Path.GetDirectoryName(move.To)}.");
        }
        foreach (var folder in folders)
        {
            var name = Path.GetFileName(folder.From);
            if (!Directory.Exists(folder.From))
                problems.Add($"Папка «{name}» не найдена.");
            if (!Inside(root, folder.From) || !Inside(root, folder.To))
                problems.Add($"Папка «{name}»: переименовывать и перемещать можно только внутри папки проекта.");
            if ((folder.To + Path.DirectorySeparatorChar).StartsWith(folder.From + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && !folder.To.Equals(folder.From, StringComparison.OrdinalIgnoreCase))
                problems.Add($"Папку «{name}» нельзя перенести внутрь неё самой.");
            if (File.Exists(folder.To))
                problems.Add($"«{Path.GetFileName(folder.To)}»: файл с таким именем уже есть в {Path.GetDirectoryName(folder.To)}.");
            if (Directory.Exists(folder.To) && !folder.To.Equals(folder.From, StringComparison.OrdinalIgnoreCase))
                warnings.Add($"Папка «{Path.GetFileName(folder.To)}» уже есть в {Path.GetDirectoryName(folder.To)} — содержимое объединится.");
            var newName = Path.GetFileName(folder.To);
            if (newName.Length == 0 || newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || newName.IndexOfAny(['<', '>', ':', '"', '|', '?', '*']) >= 0
                || newName.EndsWith('.') || newName.EndsWith(' '))
                problems.Add($"Недопустимое имя папки «{newName}».");
        }

        // Новое имя совпадает со старым именем другого переносимого файла — цепочки не поддерживаются.
        foreach (var move in moves.Where(m => !m.IsCaseOnly && sources.Contains(m.To) && !m.To.Equals(m.From, StringComparison.OrdinalIgnoreCase)))
            problems.Add($"{Path.GetFileName(move.To)}: имя занято другим переносимым файлом — переименуйте в два шага.");

        // Кто ссылается на переносимые файлы.
        var references = new List<ReferenceUpdate>();
        var real = moves.Where(m => !m.IsCaseOnly).ToList();
        if (problems.Count == 0 && real.Count > 0)
        {
            var parents = await ParentsAsync(root, real.Select(m => m.From).ToList(), progress, ct).ConfigureAwait(false);
            var maps = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var move in real)
            {
                foreach (var parent in parents.GetValueOrDefault(ReferenceIndexService.Key(move.From)) ?? [])
                {
                    if (!maps.TryGetValue(parent, out var map))
                        maps[parent] = map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    map[move.From] = move.To;
                }
            }
            var after = real.ToDictionary(m => m.From, m => m.To, StringComparer.OrdinalIgnoreCase);
            references = maps.OrderBy(m => m.Key, NaturalComparer.Instance)
                .Select(m => new ReferenceUpdate(m.Key, after.GetValueOrDefault(m.Key, m.Key), m.Value))
                .ToList();
        }

        // Документы, которые будут записаны, не должны быть «только для чтения».
        foreach (var path in references.Select(r => r.Document).Concat(properties.Select(p => p.Path)).Distinct(StringComparer.OrdinalIgnoreCase))
            if (File.Exists(path) && File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly))
                problems.Add($"{Path.GetFileName(path)}: файл только для чтения — снимите атрибут и повторите.");
        foreach (var property in properties.Where(p => !File.Exists(p.Path)))
            problems.Add($"{Path.GetFileName(property.Path)}: файл не найден.");

        foreach (var move in real.Where(m => DocumentKinds.IsKnown(m.From)))
            if (!references.Any(r => r.Map.ContainsKey(move.From)) && DocumentKinds.FromPath(move.From) != DocumentKind.Drawing)
                warnings.Add($"{Path.GetFileName(move.From)}: в проекте на него никто не ссылается.");

        return new FileOperationPlan(root, moves, references, properties, problems, warnings, folders);
    }

    /// <summary>
    /// Потерянные ссылки проекта (файлы перенесли или переименовали, например в проводнике Windows) и файлы
    /// проекта с тем же именем — куда их, вероятно, перенесли.
    /// </summary>
    public async Task<IReadOnlyList<BrokenLink>> FindBrokenLinksAsync(string root, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (!store.IsAvailable)
            throw new InvalidOperationException($"Нет доступа к файлам Inventor: {store.UnavailableReason}");

        List<(string Document, string Target)> broken;
        if (database.IsConfigured)
        {
            await ScanIndexAsync(root, progress, ct).ConfigureAwait(false);
            broken = (await index.BrokenReferencesAsync(root, ct).ConfigureAwait(false)).Select(b => (b.Document, b.Target)).ToList();
        }
        else
        {
            broken = (await AllReferencesAsync(root, progress, ct).ConfigureAwait(false))
                .SelectMany(d => d.References.Where(r => !File.Exists(r)).Select(r => (d.Document, r))).ToList();
        }

        var byName = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in ReferenceIndexService.Enumerate(root))
        {
            if (!byName.TryGetValue(file.Name, out var list))
                byName[file.Name] = list = [];
            list.Add(file.FullName);
        }

        return broken.GroupBy(b => ReferenceIndexService.Key(b.Target))
            .Select(g =>
            {
                var target = g.First().Target;
                var library = index.Locate(target);
                var candidates = (byName.GetValueOrDefault(Path.GetFileName(target)) ?? []).Order(NaturalComparer.Instance).ToList();
                if (library is not null && !candidates.Contains(library, StringComparer.OrdinalIgnoreCase))
                    candidates.Insert(0, library);
                return new BrokenLink(target,
                    g.Select(b => b.Document).Distinct(StringComparer.OrdinalIgnoreCase).Order(NaturalComparer.Instance).ToList(),
                    candidates, library);
            })
            .OrderBy(l => l.Name, NaturalComparer.Instance).ToList();
    }

    /// <summary>План восстановления ссылок: в документах с потерянной ссылкой — на выбранный файл.</summary>
    /// <param name="choices">Потерянный путь → найденный файл.</param>
    public FileOperationPlan PlanRepair(string root, IReadOnlyList<BrokenLink> links, IReadOnlyDictionary<string, string> choices)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var problems = new List<string>();
        var maps = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var link in links)
        {
            if (!choices.TryGetValue(link.Missing, out var replacement) || string.IsNullOrWhiteSpace(replacement))
                continue;
            if (!File.Exists(replacement))
            {
                problems.Add($"{link.Name}: выбранный файл не найден ({replacement}).");
                continue;
            }
            if (!DocumentKinds.FromPath(replacement).Equals(DocumentKinds.FromPath(link.Missing)))
                problems.Add($"{link.Name}: выбран файл другого типа ({Path.GetFileName(replacement)}).");
            foreach (var document in link.UsedIn)
            {
                if (!maps.TryGetValue(document, out var map))
                    maps[document] = map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                map[link.Missing] = replacement;
            }
        }
        var references = maps.OrderBy(m => m.Key, NaturalComparer.Instance)
            .Select(m => new ReferenceUpdate(m.Key, m.Key, m.Value)).ToList();
        foreach (var document in references.Select(r => r.Document))
        {
            if (!File.Exists(document))
                problems.Add($"{Path.GetFileName(document)}: документ не найден.");
            else if (File.GetAttributes(document).HasFlag(FileAttributes.ReadOnly))
                problems.Add($"{Path.GetFileName(document)}: файл только для чтения — снимите атрибут и повторите.");
        }
        return new FileOperationPlan(root, [], references, [], problems, []);
    }

    /// <summary>Выполняет план. При сбое всё возвращается и бросается <see cref="FileOperationException"/>.</summary>
    public async Task<FileOperationResult> ExecuteAsync(FileOperationPlan plan, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (!plan.CanExecute)
            throw new InvalidOperationException(plan.Problems.Count > 0 ? string.Join(Environment.NewLine, plan.Problems) : "Нечего менять.");

        // Загруженный в Inventor файл Inventor перезапишет из памяти — такие должны быть закрыты.
        var involved = plan.InvolvedFiles.Select(ReferenceIndexService.Key).ToHashSet();
        var open = (await openDocuments.ListAsync(ct).ConfigureAwait(false)).Where(d => involved.Contains(ReferenceIndexService.Key(d.Path))).ToList();
        if (open.Count > 0)
            throw new InvalidOperationException("Закройте в Inventor документы (или сборки, в которые они входят): "
                                                + string.Join(", ", open.Select(d => Path.GetFileName(d.Path)).Distinct().Take(10)) + ".");

        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
        var backupFolder = Path.Combine(BackupRoot, stamp);
        Directory.CreateDirectory(backupFolder);
        WriteJournal(backupFolder, plan);

        var moved = plan.Moves.Where(m => !m.IsCaseOnly).ToDictionary(m => m.From, m => m.To, StringComparer.OrdinalIgnoreCase);
        var backups = new List<(string Original, string Copy)>();
        var created = new List<string>();
        var createdFolders = new List<string>();
        var referencesDone = 0;
        var propertiesDone = 0;
        try
        {
            // 1. Копии документов, которые изменятся на месте.
            var inPlace = plan.References.Select(r => r.Document).Concat(plan.Properties.Select(p => p.Path))
                .Where(p => !moved.ContainsKey(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            for (var i = 0; i < inPlace.Count; i++)
            {
                var copy = Path.Combine(backupFolder, $"{i + 1:000}_{Path.GetFileName(inPlace[i])}");
                File.Copy(inPlace[i], copy);
                backups.Add((inPlace[i], copy));
            }

            // 2. Файлы — по новым путям (старые пока остаются).
            foreach (var (from, to) in moved)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report($"Копирование {Path.GetFileName(from)}");
                var folder = Path.GetDirectoryName(to)!;
                if (!Directory.Exists(folder))
                {
                    Directory.CreateDirectory(folder);
                    createdFolders.Add(folder);
                }
                File.Copy(from, to);
                created.Add(to);
            }

            // 3. Ссылки — и проверка, что заменились все.
            foreach (var update in plan.References)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report($"Ссылки в {Path.GetFileName(update.Target)}");
                var map = new Dictionary<string, string>(update.Map, StringComparer.OrdinalIgnoreCase);
                await store.ReplaceReferencesAsync(update.Target, map, ct).ConfigureAwait(false);
                var keys = map.Keys.Select(ReferenceIndexService.Key).ToHashSet();
                var left = (await store.ReadWithoutGeometryAsync(update.Target, ct).ConfigureAwait(false)).References
                    .Where(r => keys.Contains(ReferenceIndexService.Key(r))).ToList();
                if (left.Count > 0)
                    throw new InvalidOperationException($"В «{Path.GetFileName(update.Target)}» не заменились ссылки: "
                                                        + string.Join(", ", left.Select(Path.GetFileName)) + ".");
                referencesDone++;
            }

            // 4. iProperties (переименованный — уже по новому пути; только регистр — по старому).
            foreach (var property in plan.Properties)
            {
                ct.ThrowIfCancellationRequested();
                var path = moved.GetValueOrDefault(property.Path, property.Path);
                progress?.Report($"Свойства {Path.GetFileName(path)}");
                await store.WriteAsync(path, new DocumentPropertyChanges { PartNumber = property.PartNumber, Title = property.Title }, ct)
                    .ConfigureAwait(false);
                propertiesDone++;
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Операция с файлами прервана — откат ({Backup})", backupFolder);
            var rollbackProblems = Rollback(created, createdFolders, backups);
            File.AppendAllText(Path.Combine(backupFolder, "журнал.txt"),
                $"{Environment.NewLine}ОТМЕНЕНО: {ex.Message}{Environment.NewLine}" + string.Join(Environment.NewLine, rollbackProblems));
            var message = ex is OperationCanceledException ? "Операция остановлена." : $"Операция не выполнена: {ex.Message}";
            throw new FileOperationException(rollbackProblems.Count == 0
                ? message + " Все изменения отменены."
                : message + $" Не всё удалось вернуть ({string.Join("; ", rollbackProblems)}). Копии — в {backupFolder}.", ex);
        }

        // 5. Старые файлы — удалить; только регистр — переименовать. Ошибка здесь — уже не откат, а замечание.
        var warnings = new List<string>();
        foreach (var from in moved.Keys)
        {
            try
            {
                File.SetAttributes(from, FileAttributes.Normal);
                File.Delete(from);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                warnings.Add($"{Path.GetFileName(from)}: старый файл не удалён ({ex.Message}) — удалите вручную, ссылки уже на новый.");
            }
        }
        foreach (var move in plan.Moves.Where(m => m.IsCaseOnly))
        {
            try
            {
                var temp = move.From + ".pdmlite-rename";
                File.Move(move.From, temp);
                File.Move(temp, move.To);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                warnings.Add($"{Path.GetFileName(move.From)}: регистр букв не изменён ({ex.Message}).");
            }
        }
        foreach (var folder in plan.FolderMoves)
            FinishFolder(folder, warnings);
        RemoveEmptyFolders(plan.Root, plan.Moves.Where(m => !m.IsCaseOnly).Select(m => Path.GetDirectoryName(m.From)!)
            .Concat(plan.FolderMoves.Select(f => Path.GetDirectoryName(f.From)!)));

        if (database.IsConfigured)
        {
            try
            {
                progress?.Report("Обновление индекса ссылок");
                await index.ScanAsync(plan.Root, ct: ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Индекс ссылок не обновлён после операции");
                warnings.Add($"Индекс ссылок не обновлён: {ex.Message}");
            }
        }

        File.AppendAllText(Path.Combine(backupFolder, "журнал.txt"), $"{Environment.NewLine}ВЫПОЛНЕНО {DateTime.Now:dd.MM.yyyy HH:mm:ss}"
            + (warnings.Count == 0 ? "" : Environment.NewLine + string.Join(Environment.NewLine, warnings)));
        logger.LogInformation("Операция с файлами: перенесено {Moved}, ссылок обновлено в {References}, свойств {Properties}",
            plan.Moves.Count, referencesDone, propertiesDone);
        return new FileOperationResult(plan.Moves.Count, referencesDone, propertiesDone, backupFolder, warnings);
    }

    /// <summary>Кто ссылается на файлы: ключ пути файла → документы проекта.</summary>
    private async Task<Dictionary<string, List<string>>> ParentsAsync(string root, IReadOnlyList<string> files, IProgress<string>? progress,
        CancellationToken ct)
    {
        var result = new Dictionary<string, List<string>>();
        var keys = files.Select(ReferenceIndexService.Key).ToHashSet();
        if (database.IsConfigured)
        {
            await ScanIndexAsync(root, progress, ct).ConfigureAwait(false);
            foreach (var file in files)
            {
                var parents = await index.WhereUsedAsync(file, allLevels: false, ct).ConfigureAwait(false);
                result[ReferenceIndexService.Key(file)] = parents.Select(p => p.Path).Where(File.Exists).ToList();
            }
            return result;
        }

        // Без базы — читаем ссылки всех документов проекта.
        foreach (var (document, references) in await AllReferencesAsync(root, progress, ct).ConfigureAwait(false))
        {
            foreach (var key in references.Select(ReferenceIndexService.Key).Where(keys.Contains).Distinct())
            {
                if (!result.TryGetValue(key, out var list))
                    result[key] = list = [];
                list.Add(document);
            }
        }
        return result;
    }

    private Task ScanIndexAsync(string root, IProgress<string>? progress, CancellationToken ct)
    {
        progress?.Report("Обновление индекса ссылок проекта");
        return index.ScanAsync(root, new Progress<(int Done, int Total, string? Current)>(p =>
            progress?.Report(p.Current is null ? "Индекс ссылок обновлён" : $"Индекс ссылок: {p.Done} из {p.Total} — {p.Current}")), ct);
    }

    /// <summary>Ссылки всех документов проекта (без базы); повторно читаются только изменённые с прошлого раза.</summary>
    private async Task<List<(string Document, IReadOnlyList<string> References)>> AllReferencesAsync(string root, IProgress<string>? progress,
        CancellationToken ct)
    {
        var result = new List<(string, IReadOnlyList<string>)>();
        var documents = ReferenceIndexService.Enumerate(root);
        for (var i = 0; i < documents.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var file = documents[i];
            (long Size, DateTime Modified, IReadOnlyList<string> References) cached;
            bool hit;
            lock (_cache)
                hit = _cache.TryGetValue(file.FullName, out cached) && cached.Size == file.Length && cached.Modified == file.LastWriteTimeUtc;
            if (hit)
            {
                result.Add((file.FullName, cached.References));
                continue;
            }
            progress?.Report($"Чтение ссылок: {i + 1} из {documents.Count} — {file.Name}");
            try
            {
                var references = (await store.ReadWithoutGeometryAsync(file.FullName, ct).ConfigureAwait(false)).References;
                lock (_cache)
                    _cache[file.FullName] = (file.Length, file.LastWriteTimeUtc, references);
                result.Add((file.FullName, references));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogDebug(ex, "Ссылки не прочитаны: {Path}", file.FullName);
            }
        }
        return result;
    }

    /// <summary>
    /// После переноса файлов папки: остальное (OldVersions, пустые подпапки) — в новую папку, старая удаляется.
    /// Только регистр букв в имени — переименование через временное имя.
    /// </summary>
    private void FinishFolder(FileMove folder, List<string> warnings)
    {
        try
        {
            if (!Directory.Exists(folder.From))
                return;
            if (folder.IsCaseOnly)
            {
                var temp = folder.From + ".pdmlite-rename";
                Directory.Move(folder.From, temp);
                Directory.Move(temp, folder.To);
                return;
            }
            Directory.CreateDirectory(folder.To);
            foreach (var directory in Directory.EnumerateDirectories(folder.From, "*", SearchOption.AllDirectories))
                Directory.CreateDirectory(Path.Combine(folder.To, Path.GetRelativePath(folder.From, directory)));
            foreach (var file in Directory.EnumerateFiles(folder.From, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(folder.To, Path.GetRelativePath(folder.From, file));
                if (File.Exists(target))
                    warnings.Add($"{Path.GetRelativePath(folder.From, file)}: в новой папке уже есть такой файл — остался в старой.");
                else
                    File.Move(file, target);
            }
            if (!Directory.EnumerateFiles(folder.From, "*", SearchOption.AllDirectories).Any())
                Directory.Delete(folder.From, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warnings.Add($"Папка «{Path.GetFileName(folder.From)}»: не всё перенесено ({ex.Message}).");
        }
    }

    /// <summary>Прочитанные ссылки документов (без базы): путь → размер, время изменения, ссылки.</summary>
    private readonly Dictionary<string, (long Size, DateTime Modified, IReadOnlyList<string> References)> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    private List<string> Rollback(List<string> created, List<string> createdFolders, List<(string Original, string Copy)> backups)
    {
        var problems = new List<string>();
        foreach (var file in created)
        {
            try
            {
                if (File.Exists(file))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                    File.Delete(file);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                problems.Add($"не удалена копия {file}: {ex.Message}");
            }
        }
        foreach (var folder in createdFolders.OrderByDescending(f => f.Length))
        {
            try
            {
                if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
                    Directory.Delete(folder);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogDebug(ex, "Папка не удалена при откате: {Folder}", folder);
            }
        }
        foreach (var (original, copy) in backups)
        {
            try
            {
                if (File.Exists(original))
                    File.SetAttributes(original, File.GetAttributes(original) & ~FileAttributes.ReadOnly);
                File.Copy(copy, original, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                problems.Add($"не восстановлен {original}: {ex.Message}");
            }
        }
        return problems;
    }

    /// <summary>Опустевшие исходные папки (после переноса папки целиком) — удалить, поднимаясь до папки проекта.</summary>
    private void RemoveEmptyFolders(string root, IEnumerable<string> folders)
    {
        foreach (var start in folders.Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(f => f.Length))
        {
            for (var folder = start; folder is not null && Inside(root, folder); folder = Path.GetDirectoryName(folder))
            {
                try
                {
                    if (!Directory.Exists(folder) || Directory.EnumerateFileSystemEntries(folder).Any())
                        break;
                    Directory.Delete(folder);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    logger.LogDebug(ex, "Пустая папка не удалена: {Folder}", folder);
                    break;
                }
            }
        }
    }

    private static readonly JsonSerializerOptions JournalJson = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static void WriteJournal(string folder, FileOperationPlan plan)
    {
        File.WriteAllText(Path.Combine(folder, "план.json"), JsonSerializer.Serialize(plan, JournalJson), new UTF8Encoding(true));
        var text = new StringBuilder()
            .AppendLine($"Операция с файлами: {DateTime.Now:dd.MM.yyyy HH:mm:ss}, {Environment.UserName}")
            .AppendLine($"Проект: {plan.Root}")
            .AppendLine().AppendLine($"ФАЙЛЫ ({plan.Moves.Count}):");
        foreach (var move in plan.Moves)
            text.AppendLine($"  {move.From}  →  {move.To}");
        text.AppendLine().AppendLine($"ССЫЛКИ ({plan.References.Count}):");
        foreach (var update in plan.References)
            text.AppendLine($"  {update.Document}: {update.Map.Count}");
        text.AppendLine().AppendLine($"СВОЙСТВА ({plan.Properties.Count}):");
        foreach (var property in plan.Properties)
            text.AppendLine($"  {property.Path}: обозначение «{property.PartNumber}», наименование «{property.Title}»");
        text.AppendLine().AppendLine("Копии документов, изменённых на месте, — в этой папке (001_…).");
        File.WriteAllText(Path.Combine(folder, "журнал.txt"), text.ToString(), new UTF8Encoding(true));
    }

    private static bool Inside(string root, string path)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
