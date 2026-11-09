using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PdmLite.Core.Documents;
using PdmLite.Core.Domain;
using PdmLite.Core.Naming;
using PdmLite.Data;
using PdmLite.Data.Projects;

namespace PdmLite.Index;

/// <summary>Итог сканирования.</summary>
/// <param name="Files">Сколько документов в проекте.</param>
/// <param name="Read">Сколько прочитано заново (новые и изменённые).</param>
/// <param name="Removed">Сколько удалено из индекса (файлов больше нет).</param>
/// <param name="Errors">Документы, которые не удалось прочитать.</param>
public sealed record IndexScanResult(int Files, int Read, int Removed, IReadOnlyList<string> Errors);

/// <summary>Документ в результатах: путь, тип, обозначение и наименование (если проиндексирован).</summary>
/// <param name="Level">Уровень вложенности (1 — непосредственно).</param>
/// <param name="Exists">Файл есть на диске.</param>
public sealed record IndexedDocument(string Path, DocumentKind Kind, string? PartNumber, string? Title, int Level, bool Exists)
{
    public string Name => System.IO.Path.GetFileName(Path);
}

/// <summary>Ссылка на файл, которого нет на диске.</summary>
public sealed record BrokenReference(string Document, string Target);

/// <summary>Файл, которого нет на диске, и документы, которые на него ссылаются.</summary>
/// <param name="Target">Путь, по которому его ищут.</param>
/// <param name="UsedIn">Документы со ссылкой на него (полные пути).</param>
/// <param name="FoundAt">
/// Файла нет по записанному пути, но он найден (например, компонент библиотеки переехал или переименован):
/// Inventor с плагином PdmLite подставит его сам, а записать новый путь в документы — «Обновить связи проекта».
/// </param>
public sealed record MissingFile(string Target, IReadOnlyList<string> UsedIn, string? FoundAt = null)
{
    public string Name => System.IO.Path.GetFileName(Target);
    public string Folder => System.IO.Path.GetDirectoryName(Target) ?? "";
    public bool IsFound => FoundAt is not null;
}

/// <summary>
/// Индекс ссылок проекта в базе: для каждого документа (.ipt, .iam, .idw, .dwg, .ipn) — на какие
/// файлы он ссылается. Отсюда «из чего состоит» и «где используется». Документы читаются через
/// <see cref="IDocumentPropertyStore"/> (Apprentice); повторно — только изменившиеся.
/// </summary>
public sealed class ReferenceIndexService
{
    private readonly IPdmDatabase database;
    private readonly IDocumentPropertyStore properties;
    private readonly ILogger<ReferenceIndexService> logger;
    private readonly IReadOnlyList<IMissingFileResolver> resolvers;
    private readonly ProjectSearchPaths? searchPaths;

    public ReferenceIndexService(IPdmDatabase database, IDocumentPropertyStore properties, ILogger<ReferenceIndexService> logger)
        : this(database, properties, logger, [])
    {
    }

    public ReferenceIndexService(IPdmDatabase database, IDocumentPropertyStore properties, ILogger<ReferenceIndexService> logger,
        IEnumerable<IMissingFileResolver> resolvers)
        : this(database, properties, logger, resolvers, null)
    {
    }

    /// <param name="resolvers">Кто ещё умеет найти файл по ссылке (библиотека) — как плагин в Inventor при открытии.</param>
    /// <param name="searchPaths">Папки поиска проекта Inventor (Content Center, библиотеки проекта) — как ищет сам Inventor.</param>
    public ReferenceIndexService(IPdmDatabase database, IDocumentPropertyStore properties, ILogger<ReferenceIndexService> logger,
        IEnumerable<IMissingFileResolver> resolvers, ProjectSearchPaths? searchPaths)
    {
        this.database = database;
        this.properties = properties;
        this.logger = logger;
        this.resolvers = resolvers.ToList();
        this.searchPaths = searchPaths;
    }

    /// <summary>
    /// Где на самом деле файл, которого нет по ссылке, или <c>null</c>: в папках поиска проекта Inventor
    /// (так его находит сам Inventor) или в библиотеке PdmLite (переехал или переименован).
    /// </summary>
    /// <param name="usedIn">Документы со ссылкой (Inventor ищет файл и рядом с ними).</param>
    public string? Locate(string reference, IEnumerable<string>? usedIn = null, ProjectInfo? project = null)
    {
        try
        {
            if (searchPaths?.Locate(reference, usedIn, project) is { } inProject)
                return inProject;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Поиск файла {Reference} в папках проекта", reference);
        }
        foreach (var resolver in resolvers)
        {
            try
            {
                if (resolver.Resolve(reference, null) is { } found && File.Exists(found))
                    return found;
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Поиск файла {Reference}", reference);
            }
        }
        return null;
    }

    private static readonly HashSet<string> SkippedFolders = new(StringComparer.OrdinalIgnoreCase) { "OldVersions" };
    private const int BatchSize = 50;

    /// <summary>Одно сканирование за раз: фоновое (при открытии проекта) и из окна индекса не мешают друг другу.</summary>
    private readonly SemaphoreSlim _scanGate = new(1, 1);

    /// <summary>
    /// Ключ пути: без учёта регистра и вида разделителя. Путь, который нельзя разобрать (ссылка с недопустимыми
    /// символами из чужой системы), берётся как есть — без ошибки.
    /// </summary>
    public static string Key(string path)
    {
        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            full = path;
        }
        return full.Replace('\\', '/').TrimEnd('/').ToUpperInvariant();
    }

    /// <summary>Документы папки проекта — по пути (а не по папке, при сканировании которой их нашли): вложенные проекты не конфликтуют.</summary>
    private static IQueryable<IndexedFile> Under(PdmDbContext db, string rootKey) =>
        db.IndexedFiles.Where(f => f.PathKey.StartsWith(rootKey + "/"));

    /// <summary>
    /// Сканирует папку проекта: новые и изменённые документы читаются, удалённые убираются из индекса.
    /// Можно остановить — прочитанное сохраняется пачками.
    /// </summary>
    public async Task<IndexScanResult> ScanAsync(string root, IProgress<(int Done, int Total, string? Current)>? progress = null,
        CancellationToken ct = default)
    {
        if (!properties.IsAvailable)
            throw new InvalidOperationException($"Нет доступа к файлам Inventor: {properties.UnavailableReason}");

        await database.MigrateAsync(ct).ConfigureAwait(false);
        await _scanGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await ScanCoreAsync(root, progress, ct).ConfigureAwait(false);
        }
        finally
        {
            _scanGate.Release();
        }
    }

    private async Task<IndexScanResult> ScanCoreAsync(string root, IProgress<(int Done, int Total, string? Current)>? progress, CancellationToken ct)
    {
        var rootKey = Key(root);
        var files = Enumerate(root);

        await using var db = database.CreateContext();
        var known = await Under(db, rootKey).ToDictionaryAsync(f => f.PathKey, ct).ConfigureAwait(false);

        // Удалённые файлы — из индекса.
        var present = files.Select(f => Key(f.FullName)).ToHashSet();
        var gone = known.Values.Where(f => !present.Contains(f.PathKey)).ToList();
        db.IndexedFiles.RemoveRange(gone);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        var changed = files.Where(f => !known.TryGetValue(Key(f.FullName), out var k) || k.Error != null || k.Size != f.Length || !SameTime(k.ModifiedUtc, f.LastWriteTimeUtc)).ToList();
        var errors = new List<string>();
        for (var i = 0; i < changed.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var file = changed[i];
            progress?.Report((i, changed.Count, file.Name));
            var key = Key(file.FullName);

            DocumentProperties? props = null;
            string? error = null;
            try
            {
                props = await properties.ReadWithoutGeometryAsync(file.FullName, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                error = ex.Message;
                errors.Add($"{file.FullName}: {ex.Message}");
                logger.LogDebug(ex, "Не прочитан {Path}", file.FullName);
            }

            if (!known.TryGetValue(key, out var entry))
            {
                entry = new IndexedFile { Path = file.FullName, PathKey = key, RootKey = rootKey };
                db.IndexedFiles.Add(entry);
                known[key] = entry;
            }
            else if (error is null)
            {
                var oldReferences = await db.IndexedReferences.Where(r => r.FileId == entry.Id).ToListAsync(ct).ConfigureAwait(false);
                db.IndexedReferences.RemoveRange(oldReferences);
                entry.References.Clear();
            }

            entry.Path = file.FullName;
            entry.RootKey = rootKey;
            entry.Kind = (int)DocumentKinds.FromPath(file.FullName);
            entry.Size = file.Length;
            entry.ModifiedUtc = file.LastWriteTimeUtc;
            entry.ScannedAt = DateTime.UtcNow;
            if (error is null)
            {
                entry.PartNumber = Trim(props?.PartNumber, 400);
                entry.Title = Trim(props?.Title, 400);
            }
            entry.Error = Trim(error, 1000);
            foreach (var target in (props?.References ?? []).Distinct(StringComparer.OrdinalIgnoreCase))
                entry.References.Add(new IndexedReference { TargetPath = target, TargetKey = Key(target) });

            if ((i + 1) % BatchSize == 0)
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        progress?.Report((changed.Count, changed.Count, null));
        logger.LogInformation("Индекс ссылок {Root}: документов {Files}, прочитано {Read}, удалено {Removed}, ошибок {Errors}",
            root, files.Count, changed.Count, gone.Count, errors.Count);
        return new IndexScanResult(files.Count, changed.Count, gone.Count, errors);
    }

    /// <summary>Проиндексирована ли папка проекта (хоть один документ).</summary>
    public async Task<bool> IsIndexedAsync(string root, CancellationToken ct = default)
    {
        var rootKey = Key(root);
        await database.MigrateAsync(ct).ConfigureAwait(false);
        await using var db = database.CreateContext();
        return await Under(db, rootKey).AnyAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Когда документ последний раз читался в индекс; <c>null</c> — не проиндексирован.</summary>
    public async Task<DateTime?> ScannedAtAsync(string path, CancellationToken ct = default)
    {
        var key = Key(path);
        await database.MigrateAsync(ct).ConfigureAwait(false);
        await using var db = database.CreateContext();
        return await db.IndexedFiles.Where(f => f.PathKey == key && f.Error == null).Select(f => (DateTime?)f.ScannedAt).FirstOrDefaultAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Ошибка последней попытки чтения: старые связи при ней не являются подтверждёнными.</summary>
    public async Task<string?> ReadErrorAsync(string path, CancellationToken ct = default)
    {
        var key = Key(path);
        await database.MigrateAsync(ct).ConfigureAwait(false);
        await using var db = database.CreateContext();
        return await db.IndexedFiles.Where(f => f.PathKey == key).Select(f => f.Error).FirstOrDefaultAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Где используется документ: сборки, чертежи и другие документы, которые на него ссылаются.
    /// <paramref name="allLevels"/> — и те, что ссылаются на них (до верхней сборки).
    /// </summary>
    public async Task<IReadOnlyList<IndexedDocument>> WhereUsedAsync(string path, bool allLevels, CancellationToken ct = default)
    {
        await database.MigrateAsync(ct).ConfigureAwait(false);
        await using var db = database.CreateContext();
        var result = new List<IndexedDocument>();
        var seen = new HashSet<string> { Key(path) };
        var level = new List<string> { Key(path) };
        for (var depth = 1; level.Count > 0 && (allLevels || depth == 1); depth++)
        {
            var current = level;
            var parents = await db.IndexedReferences.Where(r => current.Contains(r.TargetKey))
                .Join(db.IndexedFiles, r => r.FileId, f => f.Id, (_, f) => f)
                .Distinct().AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
            level = [];
            foreach (var parent in parents.Where(p => seen.Add(p.PathKey)))
            {
                result.Add(ToDocument(parent, depth));
                level.Add(parent.PathKey);
            }
        }
        return result.OrderBy(d => d.Level).ThenBy(d => d.Kind).ThenBy(d => d.Name, NaturalComparer.Instance).ToList();
    }

    /// <summary>Из чего состоит документ (непосредственно): компоненты сборки, модели чертежа. Ненайденные файлы отмечены.</summary>
    public async Task<IReadOnlyList<IndexedDocument>> ComponentsAsync(string path, CancellationToken ct = default)
    {
        var key = Key(path);
        await database.MigrateAsync(ct).ConfigureAwait(false);
        await using var db = database.CreateContext();
        var targets = await db.IndexedReferences.Where(r => db.IndexedFiles.Any(f => f.Id == r.FileId && f.PathKey == key))
            .Select(r => new { r.TargetPath, r.TargetKey }).AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
        var keys = targets.Select(t => t.TargetKey).ToList();
        var indexed = await db.IndexedFiles.Where(f => keys.Contains(f.PathKey)).AsNoTracking()
            .ToDictionaryAsync(f => f.PathKey, ct).ConfigureAwait(false);

        return targets.Select(t => indexed.TryGetValue(t.TargetKey, out var f)
                ? ToDocument(f, 1)
                : new IndexedDocument(t.TargetPath, DocumentKinds.FromPath(t.TargetPath), null, null, 1, File.Exists(t.TargetPath)))
            .OrderBy(d => d.Exists).ThenBy(d => d.Kind).ThenBy(d => d.Name, NaturalComparer.Instance).ToList();
    }

    /// <summary>
    /// Ненайденные файлы сборки (или чертежа) — на всех уровнях вложенности, каждый один раз, по имени.
    /// Подсборки обходятся по индексу; ненайденная подсборка — сама в списке (её состав неизвестен).
    /// </summary>
    public async Task<IReadOnlyList<MissingFile>> MissingComponentsAsync(string path, CancellationToken ct = default)
    {
        await database.MigrateAsync(ct).ConfigureAwait(false);
        await using var db = database.CreateContext();
        var missing = new Dictionary<string, (string Target, HashSet<string> UsedIn)>();
        var visited = new HashSet<string> { Key(path) };
        var level = new List<string> { Key(path) };
        while (level.Count > 0)
        {
            var current = level;
            var references = await db.IndexedReferences
                .Join(db.IndexedFiles.Where(f => current.Contains(f.PathKey)), r => r.FileId, f => f.Id, (r, f) => new { f.Path, r.TargetPath, r.TargetKey })
                .AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
            level = [];
            foreach (var reference in references)
            {
                if (!File.Exists(reference.TargetPath))
                {
                    if (!missing.TryGetValue(reference.TargetKey, out var entry))
                        missing[reference.TargetKey] = entry = (reference.TargetPath, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                    entry.UsedIn.Add(reference.Path);
                }
                else if (visited.Add(reference.TargetKey))
                {
                    level.Add(reference.TargetKey);
                }
            }
        }
        return missing.Values
            .Select(m => new MissingFile(m.Target, m.UsedIn.Order(NaturalComparer.Instance).ToList(), Locate(m.Target, m.UsedIn)))
            .OrderBy(m => m.Name, NaturalComparer.Instance).ToList();
    }

    /// <summary>Ненайденные файлы всего проекта: по одному на файл, с документами, где он используется.</summary>
    public async Task<IReadOnlyList<MissingFile>> MissingFilesAsync(string root, CancellationToken ct = default) =>
        (await BrokenReferencesAsync(root, ct).ConfigureAwait(false))
        .GroupBy(r => Key(r.Target))
        .Select(g =>
        {
            var usedIn = g.Select(r => r.Document).Distinct(StringComparer.OrdinalIgnoreCase).Order(NaturalComparer.Instance).ToList();
            return new MissingFile(g.First().Target, usedIn, Locate(g.First().Target, usedIn));
        })
        .OrderBy(m => m.Name, NaturalComparer.Instance).ToList();

    /// <summary>Ссылки документов проекта на файлы, которых нет на диске.</summary>
    public async Task<IReadOnlyList<BrokenReference>> BrokenReferencesAsync(string root, CancellationToken ct = default)
    {
        var rootKey = Key(root);
        await database.MigrateAsync(ct).ConfigureAwait(false);
        await using var db = database.CreateContext();
        var references = await db.IndexedReferences
            .Join(Under(db, rootKey), r => r.FileId, f => f.Id, (r, f) => new { f.Path, r.TargetPath })
            .AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
        // Документ удалили (а индекс ещё не обновлён) — его ссылки уже не потеря.
        return references.Where(r => !File.Exists(r.TargetPath) && File.Exists(r.Path))
            .Select(r => new BrokenReference(r.Path, r.TargetPath))
            .OrderBy(r => r.Document, NaturalComparer.Instance).ToList();
    }

    /// <summary>Дата файла «та же»: база хранит время с точностью до микросекунды, Windows — до 100 нс.</summary>
    private static bool SameTime(DateTime a, DateTime b) => Math.Abs((a - b).Ticks) < TimeSpan.TicksPerMillisecond;

    private static IndexedDocument ToDocument(IndexedFile f, int level) =>
        new(f.Path, (DocumentKind)f.Kind, f.PartNumber, f.Title, level, File.Exists(f.Path));

    /// <summary>Документы Inventor папки проекта (без OldVersions, скрытых и системных).</summary>
    internal static List<FileInfo> Enumerate(string root)
    {
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"Нет папки проекта: {root}");
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = false,
            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Hidden | FileAttributes.System,
        };
        return new DirectoryInfo(root).EnumerateFiles("*", options)
            .Where(f => DocumentKinds.FromPath(f.FullName) is DocumentKind.Part or DocumentKind.Assembly or DocumentKind.Drawing or DocumentKind.Presentation)
            .Where(f => !f.FullName.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(SkippedFolders.Contains))
            .ToList();
    }

    private static string? Trim(string? value, int max) =>
        value is null ? null : value.Length <= max ? value : value[..max];
}
