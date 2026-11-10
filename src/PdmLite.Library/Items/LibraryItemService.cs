using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PdmLite.Core.Documents;
using PdmLite.Core.Naming;
using PdmLite.Data;
using PdmLite.Data.Library;
using PdmLite.Library.Catalog;
using PdmLite.Library.Designations;
using PdmLite.Library.Import;
using PdmLite.Library.Storage;

namespace PdmLite.Library.Items;

/// <summary>Компонент библиотеки для списка каталога.</summary>
public sealed record LibraryItemSummary(
    Guid Id,
    string Designation,
    LibraryItemKind Kind,
    LibraryDocumentType DocumentType,
    string? Standard,
    string? Manufacturer,
    int CategoryId,
    string CategoryPath,
    string RelativePath,
    int Version,
    DateTime ModifiedAt,
    string ModifiedBy,
    bool StandardIsManual = false);

/// <summary>Расхождение одного свойства: сейчас в файле и как должно быть.</summary>
public sealed record PropertyChange(string Property, string? Current, string Expected);

/// <summary>Результат проверки файла компонента: что изменится (пусто — всё правильно) или ошибка чтения.</summary>
public sealed record PropertyDiff(Guid ItemId, string Designation, string RelativePath, IReadOnlyList<PropertyChange> Changes, string? Error)
{
    public bool NeedsUpdate => Error is null && Changes.Count > 0;
}

/// <summary>Итог обновления свойств: проверено, записано, уже в порядке, ошибки.</summary>
public sealed record RewriteResult(int Checked, int Updated, int UpToDate, IReadOnlyList<string> Failed);

/// <summary>Компонент в группе возможных дубликатов.</summary>
/// <param name="UsedIn">В скольких сборках библиотеки он стоит.</param>
public sealed record DuplicateEntry(LibraryItemSummary Item, int UsedIn, DateTime CreatedAt);

/// <summary>Компоненты, которые, вероятно, одно и то же.</summary>
/// <param name="GeometryOnly">Совпадает только геометрия — возможно, законная копия (другое покрытие, класс прочности).</param>
public sealed record DuplicateGroup(string Reason, IReadOnlyList<DuplicateEntry> Items, bool GeometryOnly = false);

/// <summary>Новые значения карточки компонента.</summary>
/// <param name="ManualStandard">Стандарт вручную; <c>null</c> — распознаётся из обозначения.</param>
public sealed record LibraryItemEdit(string Designation, LibraryItemKind Kind, string? Manufacturer, int? CategoryId, string? ManualStandard = null);

/// <summary>Результат проверки изменения: замечания и путь файла после изменения.</summary>
public sealed record LibraryItemEditCheck(IReadOnlyList<ImportIssue> Issues, string? NewRelativePath, string CanonicalDesignation, string? Standard)
{
    public bool HasErrors => Issues.Any(i => i.Severity == ImportSeverity.Error);
}

/// <summary>Просмотр, изменение и удаление компонентов библиотеки.</summary>
/// <remarks>
/// Изменение обозначения, типа, производителя или раздела переименовывает и при необходимости
/// переносит файл, перезаписывает свойства в нём и перевязывает сборки библиотеки, которые
/// ссылаются на компонент. Сборки проектов (вне библиотеки) здесь не обновляются.
/// </remarks>
public sealed class LibraryItemService(
    IPdmDatabase database,
    IDocumentPropertyStore properties,
    Func<LibraryStorage> storageFactory,
    ILogger<LibraryItemService> logger)
{
    internal Func<string, PdmDbContext> CreateVerificationContext { get; init; } = LibraryMutationCommit.CreateContext;
    private Task<LibraryWriteLease> EnterWriteAsync(string root, CancellationToken ct) =>
        LibraryWriteLease.EnterAsync(database, root, ex => logger.LogWarning(ex, "Операция библиотеки: очистка ресурсов"), ct);

    public async Task<LibraryCatalog> LoadCatalogAsync(CancellationToken ct = default)
    {
        await database.MigrateAsync(ct).ConfigureAwait(false);
        await using var db = database.CreateContext();
        await LibraryCatalog.SeedAsync(db, ct).ConfigureAwait(false);
        return await LibraryCatalog.LoadAsync(db, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<LibraryItemSummary>> ListAsync(LibraryCatalog catalog, CancellationToken ct = default)
    {
        await using var db = database.CreateContext();
        var items = (await db.LibraryItems.AsNoTracking().ToListAsync(ct).ConfigureAwait(false))
            .OrderBy(i => i.Designation, NaturalComparer.Instance).ToList();
        return items.Select(i => Summary(i, catalog)).ToList();
    }

    /// <summary>Текущие и прежние пути компонентов — для подстановки ненайденных ссылок в сборках.</summary>
    public async Task<LibraryPathIndex> LoadPathIndexAsync(CancellationToken ct = default)
    {
        await using var db = database.CreateContext();
        var items = await db.LibraryItems.AsNoTracking().Select(i => new { i.Id, i.RelativePath, i.Designation })
            .ToListAsync(ct).ConfigureAwait(false);
        var edits = await db.LibraryLog.AsNoTracking().Where(e => (e.Action == "edit" || e.Action == "merge") && e.ItemId != null)
            .ToListAsync(ct).ConfigureAwait(false);
        return new LibraryPathIndex(items.Select(i => (i.Id, i.RelativePath, i.Designation)), LibraryPathIndex.MovesFromLog(edits));
    }

    /// <summary>Обозначения сборок библиотеки, в которые входит компонент.</summary>
    public async Task<IReadOnlyList<string>> UsedInAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = database.CreateContext();
        return await db.LibraryItemReferences.Where(r => r.ComponentId == id)
            .Join(db.LibraryItems, r => r.AssemblyId, i => i.Id, (_, i) => i.Designation)
            .ToListAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Проверяет изменение карточки — те же правила, что при добавлении.</summary>
    public async Task<LibraryItemEditCheck> CheckEditAsync(LibraryCatalog catalog, Guid id, LibraryItemEdit edit, CancellationToken ct = default)
    {
        await using var db = database.CreateContext();
        var item = await db.LibraryItems.AsNoTracking().SingleAsync(i => i.Id == id, ct).ConfigureAwait(false);
        var storage = storageFactory();

        var placement = LibraryRules.Place(catalog, storage, edit.Designation, edit.Kind, edit.Manufacturer, edit.CategoryId,
            Path.GetExtension(item.RelativePath), edit.ManualStandard);
        var issues = new List<ImportIssue>();
        if (!properties.IsAvailable)
            issues.Add(new ImportIssue(ImportSeverity.Error, $"Нет доступа к свойствам файлов Inventor: {properties.UnavailableReason}"));
        issues.AddRange(placement.Issues);

        if (placement.Designation.Length > 0)
        {
            var key = DesignationNormalizer.ToKey(placement.Designation);
            var other = await db.LibraryItems.AsNoTracking().Where(i => i.DesignationKey == key && i.Id != id)
                .Select(i => i.Designation).FirstOrDefaultAsync(ct).ConfigureAwait(false);
            if (other is not null)
                issues.Add(new ImportIssue(ImportSeverity.Error, $"В библиотеке уже есть «{other}»."));
        }

        if (placement.RelativePath is { } newPath && !SamePath(newPath, item.RelativePath))
        {
            if (File.Exists(storage.GetFullPath(newPath)))
                issues.Add(new ImportIssue(ImportSeverity.Error, $"В папке библиотеки уже есть файл {newPath}."));

            var usedIn = await UsedInAsync(id, ct).ConfigureAwait(false);
            if (usedIn.Count > 0)
                issues.Add(new ImportIssue(ImportSeverity.Info,
                    "Сборки библиотеки, в которые входит компонент, будут перевязаны на новый файл: " + string.Join(", ", usedIn) + "."));
            issues.Add(new ImportIssue(ImportSeverity.Warning,
                "Файл будет переименован или перенесён. Если вы уже вставляли этот компонент в сборки проектов, " +
                "в них придётся заменить ссылку (автоматическое обновление — в следующих шагах)."));
        }

        return new LibraryItemEditCheck(issues, placement.RelativePath, placement.Designation, placement.Standard);
    }

    /// <summary>Применяет изменение карточки. При ошибке файл, сборки и база остаются как были.</summary>
    public async Task ApplyEditAsync(LibraryCatalog catalog, Guid id, LibraryItemEdit edit, CancellationToken ct = default)
    {
        var storage = storageFactory();
        await using var lease = await EnterWriteAsync(storage.Root, ct).ConfigureAwait(false);
        var check = await CheckEditAsync(catalog, id, edit, ct).ConfigureAwait(false);
        if (check.HasErrors)
            throw new InvalidOperationException(string.Join(" ", check.Issues.Where(i => i.Severity == ImportSeverity.Error).Select(i => i.Message)));

        var user = Environment.UserName;
        var category = catalog.Get(edit.CategoryId)!;
        var manufacturer = string.IsNullOrWhiteSpace(edit.Manufacturer) ? null : edit.Manufacturer.Trim();

        await using var db = database.CreateContext();
        var item = await db.LibraryItems.Include(i => i.Versions).SingleAsync(i => i.Id == id, ct).ConfigureAwait(false);
        var before = Snapshot(item, catalog.Get(item.CategoryId)?.Path);

        var oldFull = storage.GetFullPath(item.RelativePath);
        var newFull = storage.GetFullPath(check.NewRelativePath!);
        var moved = !SamePath(check.NewRelativePath!, item.RelativePath);

        // Резервные копии на время операции: свойства в файле перезаписываются, сборки перевязываются.
        var backups = new List<(string Path, string Backup)> { (oldFull, LibraryStorage.Backup(oldFull)) };
        var commit = new LibraryMutationCommit(lease, CreateVerificationContext);
        var preserveBackups = false;
        var didMove = false;
        try
        {
            lease.SetCopies(backups.Select(b => new RecoveryCopy(b.Path, b.Backup)));
            LibraryStorage.ClearReadOnly(oldFull);
            if (moved)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(newFull)!);
                File.Move(oldFull, newFull);
                didMove = true;
            }

            await properties.WriteAsync(newFull, LibraryProperties.For(check.CanonicalDesignation, edit.Kind, check.Standard, manufacturer, category.Path, id), ct)
                .ConfigureAwait(false);
            LibraryStorage.MakeReadOnly(newFull);
            var (hash, size) = await LibraryStorage.HashAsync(newFull, ct).ConfigureAwait(false);

            if (moved)
                await RelinkAssembliesAsync(db, storage, id, oldFull, newFull, backups, lease, ct).ConfigureAwait(false);

            var now = DateTime.UtcNow;
            var categoryChanged = item.CategoryId != category.Id;
            item.Designation = check.CanonicalDesignation;
            item.DesignationKey = DesignationNormalizer.ToKey(check.CanonicalDesignation);
            item.Kind = edit.Kind;
            item.Standard = check.Standard;
            item.StandardIsManual = edit.Kind == LibraryItemKind.Standard && LibraryRules.NormalizeStandard(edit.ManualStandard) is not null;
            item.Manufacturer = manufacturer;
            item.CategoryId = category.Id;
            item.RelativePath = check.NewRelativePath!;
            item.FileHash = hash;
            item.FileSize = size;
            item.ModifiedAt = now;
            item.ModifiedBy = user;
            if (item.Versions.FirstOrDefault(v => v.Version == item.Version) is { } current)
            {
                current.RelativePath = item.RelativePath;
                current.FileHash = hash;
                current.FileSize = size;
            }

            db.LibraryLog.Add(new LibraryLogEntry
            {
                At = now,
                User = user,
                Action = "edit",
                ItemId = id,
                Details = JsonSerializer.Serialize(new { before, after = Snapshot(item, category.Path) }),
            });
            if (categoryChanged)
                await LibraryCatalog.LearnAsync(db, item.Designation, item.Standard, category.Id, ct).ConfigureAwait(false);

            await commit.SaveAsync(db, ct).ConfigureAwait(false);

        }
        catch (Exception failure)
        {
            if (!await commit.CanRollbackAsync().ConfigureAwait(false))
            {
                preserveBackups = true;
                throw commit.Preserve(failure, backups.Select(b => new RecoveryCopy(b.Path, b.Backup)));
            }
            var errors = new List<Exception>();
            if (didMove) AttemptRollback(() => LibraryStorage.Delete(newFull), newFull, errors);
            RestoreBackups(backups, errors);
            if (errors.Count > 0)
            {
                preserveBackups = true;
                lease.Preserve("RollbackIncomplete", backups.Select(b => new RecoveryCopy(b.Path, b.Backup)));
                throw new LibraryRollbackException(failure, errors, backups.Select(b => new RecoveryCopy(b.Path, b.Backup)));
            }
            throw;
        }
        finally
        {
            if (!preserveBackups)
                foreach (var (_, backup) in backups) TryDelete(backup);
        }

        if (moved) CleanupFolders(storage, Path.GetDirectoryName(oldFull)!);
    }

    /// <summary>
    /// Шаг 1 обслуживания — только чтение: какие свойства PdmLite в файлах расходятся с базой
    /// (например, нет раздела спецификации, осталась метка Content Center). Файлы не меняются.
    /// </summary>
    /// <param name="ids">Какие компоненты проверить; <c>null</c> — вся библиотека.</param>
    public async Task<IReadOnlyList<PropertyDiff>> DiagnosePropertiesAsync(LibraryCatalog catalog, IReadOnlyCollection<Guid>? ids,
        IProgress<(int Done, int Total, string? Current)>? progress = null, CancellationToken ct = default)
    {
        var storage = storageFactory();
        await using var lease = await EnterWriteAsync(storage.Root, ct).ConfigureAwait(false);
        if (!properties.IsAvailable)
            throw new InvalidOperationException($"Нет доступа к свойствам файлов Inventor: {properties.UnavailableReason}");

        var items = await LoadItemsAsync(ids, ct).ConfigureAwait(false);
        var result = new List<PropertyDiff>();

        for (var i = 0; i < items.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var item = items[i];
            progress?.Report((i, items.Count, item.Designation));
            var full = storage.GetFullPath(item.RelativePath);
            try
            {
                if (!File.Exists(full))
                    throw new FileNotFoundException("Файл не найден в папке библиотеки.", full);
                var current = await properties.ReadAsync(full, ct).ConfigureAwait(false);
                if (!LibraryGeometry.Known(item.Volume, item.Area) && LibraryGeometry.Known(current.Volume, current.Area))
                    await SaveGeometryAsync(item.Id, current.Volume!.Value, current.Area!.Value, ct).ConfigureAwait(false);
                result.Add(new PropertyDiff(item.Id, item.Designation, item.RelativePath, Compare(current, Expected(item, catalog)), null));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                result.Add(new PropertyDiff(item.Id, item.Designation, item.RelativePath, [], ex.Message));
            }
        }

        progress?.Report((items.Count, items.Count, null));
        return result;
    }

    /// <summary>
    /// Шаг 2 обслуживания — запись свойств PdmLite в указанные файлы библиотеки.
    /// </summary>
    /// <remarks>
    /// Безопасность на большой библиотеке: перед записью каждый файл проверяется заново и не
    /// изменяется, если свойства уже правильные; перед записью — резервная копия файла, при ошибке
    /// файл восстанавливается, остальные продолжаются; база (контрольная сумма) обновляется после
    /// каждого файла; остановка — между файлами.
    /// </remarks>
    public async Task<RewriteResult> RewritePropertiesAsync(LibraryCatalog catalog, IReadOnlyCollection<Guid>? ids,
        IProgress<(int Done, int Total, string? Current)>? progress = null, CancellationToken ct = default)
    {
        if (!properties.IsAvailable)
            throw new InvalidOperationException($"Нет доступа к свойствам файлов Inventor: {properties.UnavailableReason}");
        var storage = storageFactory();
        await using var lease = await EnterWriteAsync(storage.Root, ct).ConfigureAwait(false);
        var snapshots = await LoadItemsAsync(ids, ct).ConfigureAwait(false);
        var failed = new List<string>();
        int updated = 0, upToDate = 0, done = 0;
        try
        {
            foreach (var snapshot in snapshots)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report((done, snapshots.Count, snapshot.Designation));
                var full = storage.GetFullPath(snapshot.RelativePath);
                string? backup = null;
                var preserve = false;
                var commit = new LibraryMutationCommit(lease, CreateVerificationContext);
                await using var db = database.CreateContext();
                try
                {
                    var item = await db.LibraryItems.Include(i => i.Versions).SingleAsync(i => i.Id == snapshot.Id, ct).ConfigureAwait(false);
                    if (!File.Exists(full)) throw new FileNotFoundException("Файл не найден в папке библиотеки.", full);
                    var expected = Expected(item, catalog);
                    var change = Compare(await properties.ReadAsync(full, ct).ConfigureAwait(false), expected).Count > 0;
                    if (change)
                    {
                        backup = LibraryStorage.Backup(full);
                        lease.SetCopies([new RecoveryCopy(full, backup)]);
                        LibraryStorage.ClearReadOnly(full);
                        await properties.WriteAsync(full, expected, ct).ConfigureAwait(false);
                        LibraryStorage.MakeReadOnly(full);
                    }
                    // Совпадение свойств не означает, что сохранённая контрольная сумма актуальна.
                    var (hash, size) = await LibraryStorage.HashAsync(full, ct).ConfigureAwait(false);
                    var version = item.Versions.FirstOrDefault(v => v.Version == item.Version);
                    if (!change && item.FileHash == hash && item.FileSize == size
                        && (version is null || version.FileHash == hash && version.FileSize == size))
                        upToDate++;
                    else
                    {
                        (item.FileHash, item.FileSize) = (hash, size);
                        if (version is not null) (version.FileHash, version.FileSize) = (hash, size);
                        db.LibraryLog.Add(new LibraryLogEntry { At = DateTime.UtcNow, User = Environment.UserName,
                            Action = "rewrite-properties-file", ItemId = item.Id, Details = JsonSerializer.Serialize(new { item.RelativePath, hash, size }) });
                        await commit.SaveAsync(db, ct).ConfigureAwait(false);
                        updated++;
                    }
                }
                catch (Exception ex)
                {
                    var copies = new[] { new RecoveryCopy(full, backup ?? full) };
                    if (!await commit.CanRollbackAsync().ConfigureAwait(false))
                    {
                        preserve = true;
                        throw commit.Preserve(ex, copies);
                    }
                    if (backup is not null)
                    {
                        var errors = new List<Exception>();
                        RestoreBackups([(full, backup)], errors);
                        if (errors.Count > 0)
                        {
                            preserve = true;
                            lease.Preserve("RollbackIncomplete", copies);
                            throw new LibraryRollbackException(ex, errors, copies);
                        }
                    }
                    if (ex is OperationCanceledException) throw;
                    logger.LogWarning(ex, "Не удалось обновить свойства {Path}", full);
                    failed.Add($"{snapshot.Designation}: {ex.Message}");
                }
                finally { if (backup is not null && !preserve) TryDelete(backup); }
                done++;
            }
        }
        finally
        {
            if (!lease.IsPreserved)
            {
                try
                {
                    await using var log = database.CreateContext();
                    log.LibraryLog.Add(new LibraryLogEntry { At = DateTime.UtcNow, User = Environment.UserName,
                        Action = "rewrite-properties", Details = JsonSerializer.Serialize(new { total = snapshots.Count, done, updated, upToDate, failed = failed.Count }) });
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await log.SaveChangesAsync(timeout.Token).ConfigureAwait(false);
                }
                catch (Exception ex) { logger.LogWarning(ex, "Не удалось сохранить итог обслуживания свойств"); }
            }
        }
        progress?.Report((snapshots.Count, snapshots.Count, null));
        return new RewriteResult(snapshots.Count, updated, upToDate, failed);
    }

    private async Task<List<LibraryItem>> LoadItemsAsync(IReadOnlyCollection<Guid>? ids, CancellationToken ct)
    {
        await using var db = database.CreateContext();
        var query = db.LibraryItems.AsNoTracking();
        if (ids is not null)
            query = query.Where(i => ids.Contains(i.Id));
        return (await query.ToListAsync(ct).ConfigureAwait(false)).OrderBy(i => i.Designation, NaturalComparer.Instance).ToList();
    }

    private static DocumentPropertyChanges Expected(LibraryItem item, LibraryCatalog catalog) =>
        LibraryProperties.For(item.Designation, item.Kind, item.Standard, item.Manufacturer, catalog.Get(item.CategoryId)?.Path ?? "", item.Id);

    /// <summary>Что изменится в файле: свойство, сейчас, станет. Пусто — свойства уже правильные.</summary>
    internal static IReadOnlyList<PropertyChange> Compare(DocumentProperties current, DocumentPropertyChanges expected)
    {
        var changes = new List<PropertyChange>();
        void Check(string name, string? now, string? target)
        {
            if (target is not null && now != target)
                changes.Add(new PropertyChange(name, now, target));
        }

        Check("Обозначение", current.PartNumber, expected.PartNumber);
        Check("Описание", current.Description, expected.Description);
        Check("Наименование", current.Title, expected.Title);
        foreach (var (name, value) in expected.Custom)
            Check(name, current.Custom.TryGetValue(name, out var now) ? now : null, value);
        if (expected.RemoveContentCenterLink && current.IsContentCenter)
            changes.Add(new PropertyChange("Метка Content Center", "есть", "снята"));
        return changes;
    }

    /// <summary>Запоминает геометрию компонента (файл не меняется) — для поиска дубликатов.</summary>
    private async Task SaveGeometryAsync(Guid id, double volume, double area, CancellationToken ct)
    {
        await using var db = database.CreateContext();
        var item = await db.LibraryItems.SingleAsync(i => i.Id == id, ct).ConfigureAwait(false);
        item.Volume = volume;
        item.Area = area;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    // ---------- Дублирование ----------

    /// <summary>Проверка копии компонента с новыми значениями (обозначение должно отличаться).</summary>
    public async Task<LibraryItemEditCheck> CheckDuplicateAsync(LibraryCatalog catalog, Guid sourceId, LibraryItemEdit edit, CancellationToken ct = default)
    {
        await using var db = database.CreateContext();
        var source = await db.LibraryItems.AsNoTracking().SingleAsync(i => i.Id == sourceId, ct).ConfigureAwait(false);
        var storage = storageFactory();
        var placement = LibraryRules.Place(catalog, storage, edit.Designation, edit.Kind, edit.Manufacturer, edit.CategoryId,
            Path.GetExtension(source.RelativePath), edit.ManualStandard);

        var issues = new List<ImportIssue>();
        if (!properties.IsAvailable)
            issues.Add(new ImportIssue(ImportSeverity.Error, $"Нет доступа к свойствам файлов Inventor: {properties.UnavailableReason}"));
        issues.AddRange(placement.Issues);
        if (placement.Designation.Length > 0)
        {
            var key = DesignationNormalizer.ToKey(placement.Designation);
            var other = await db.LibraryItems.AsNoTracking().Where(i => i.DesignationKey == key)
                .Select(i => i.Designation).FirstOrDefaultAsync(ct).ConfigureAwait(false);
            if (other is not null)
                issues.Add(new ImportIssue(ImportSeverity.Error,
                    key == source.DesignationKey ? "Измените обозначение: у копии оно должно быть своё." : $"В библиотеке уже есть «{other}»."));
        }
        if (placement.RelativePath is { } path && File.Exists(storage.GetFullPath(path)))
            issues.Add(new ImportIssue(ImportSeverity.Error, $"В папке библиотеки уже есть файл {path}."));
        if (!File.Exists(storage.GetFullPath(source.RelativePath)))
            issues.Add(new ImportIssue(ImportSeverity.Error, $"Нет файла исходного компонента: {source.RelativePath}."));

        return new LibraryItemEditCheck(issues, placement.RelativePath, placement.Designation, placement.Standard);
    }

    /// <summary>
    /// Копия компонента (тот же файл — те же размеры) с новым обозначением и свойствами, например
    /// с другим покрытием. Возвращает Id нового компонента.
    /// </summary>
    public async Task<Guid> DuplicateAsync(LibraryCatalog catalog, Guid sourceId, LibraryItemEdit edit, CancellationToken ct = default)
    {
        var storage = storageFactory();
        await using var lease = await EnterWriteAsync(storage.Root, ct).ConfigureAwait(false);
        var check = await CheckDuplicateAsync(catalog, sourceId, edit, ct).ConfigureAwait(false);
        if (check.HasErrors)
            throw new InvalidOperationException(string.Join(" ", check.Issues.Where(i => i.Severity == ImportSeverity.Error).Select(i => i.Message)));

        var user = Environment.UserName;
        var category = catalog.Get(edit.CategoryId)!;
        var manufacturer = string.IsNullOrWhiteSpace(edit.Manufacturer) ? null : edit.Manufacturer.Trim();

        await using var db = database.CreateContext();
        var source = await db.LibraryItems.AsNoTracking().SingleAsync(i => i.Id == sourceId, ct).ConfigureAwait(false);
        var componentIds = await db.LibraryItemReferences.Where(r => r.AssemblyId == sourceId).Select(r => r.ComponentId)
            .ToListAsync(ct).ConfigureAwait(false);

        var id = Guid.NewGuid();
        var commit = new LibraryMutationCommit(lease, CreateVerificationContext);
        var copied = storage.CopyIn(storage.GetFullPath(source.RelativePath), check.NewRelativePath!);
        try
        {
            lease.SetCopies([new RecoveryCopy(copied, copied)]);
            LibraryStorage.ClearReadOnly(copied);
            await properties.WriteAsync(copied, LibraryProperties.For(check.CanonicalDesignation, edit.Kind, check.Standard, manufacturer, category.Path, id), ct)
                .ConfigureAwait(false);
            LibraryStorage.MakeReadOnly(copied);
            var (hash, size) = await LibraryStorage.HashAsync(copied, ct).ConfigureAwait(false);
            var now = DateTime.UtcNow;

            db.LibraryItems.Add(new LibraryItem
            {
                Id = id,
                CategoryId = category.Id,
                Kind = edit.Kind,
                DocumentType = source.DocumentType,
                Designation = check.CanonicalDesignation,
                DesignationKey = DesignationNormalizer.ToKey(check.CanonicalDesignation),
                Standard = check.Standard,
                StandardIsManual = edit.Kind == LibraryItemKind.Standard && LibraryRules.NormalizeStandard(edit.ManualStandard) is not null,
                Manufacturer = manufacturer,
                RelativePath = check.NewRelativePath!,
                FileHash = hash,
                FileSize = size,
                Volume = source.Volume,
                Area = source.Area,
                Version = 1,
                CreatedAt = now,
                CreatedBy = user,
                ModifiedAt = now,
                ModifiedBy = user,
                Versions =
                [
                    new LibraryItemVersion
                    {
                        Version = 1, RelativePath = check.NewRelativePath!, FileHash = hash, FileSize = size,
                        CreatedAt = now, CreatedBy = user, Comment = $"Копия «{source.Designation}»",
                    },
                ],
                References = componentIds.Select(cid => new LibraryItemReference { AssemblyId = id, ComponentId = cid }).ToList(),
            });
            db.LibraryLog.Add(new LibraryLogEntry
            {
                At = now,
                User = user,
                Action = "duplicate",
                ItemId = id,
                Details = JsonSerializer.Serialize(new { from = source.Designation, fromId = source.Id, designation = check.CanonicalDesignation, path = check.NewRelativePath }),
            });
            await commit.SaveAsync(db, ct).ConfigureAwait(false);

        }
        catch (Exception failure)
        {
            if (!await commit.CanRollbackAsync().ConfigureAwait(false))
                throw commit.Preserve(failure, [new RecoveryCopy(copied, copied)]);
            TryDelete(copied);
            throw;
        }
        logger.LogInformation("Компонент скопирован: {From} → {To}", source.Designation, check.CanonicalDesignation);
        return id;
    }

    // ---------- Дубликаты ----------

    /// <summary>
    /// Ищет компоненты, которые, вероятно, одно и то же: одинаковый файл или обозначения, отличающиеся
    /// только пробелами, знаками препинания и регистром. Только чтение.
    /// </summary>
    public async Task<IReadOnlyList<DuplicateGroup>> FindDuplicatesAsync(LibraryCatalog catalog, CancellationToken ct = default)
    {
        await using var db = database.CreateContext();
        var items = await db.LibraryItems.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
        var usedIn = (await db.LibraryItemReferences.AsNoTracking().ToListAsync(ct).ConfigureAwait(false))
            .GroupBy(r => r.ComponentId).ToDictionary(g => g.Key, g => g.Count());

        // Объединение в группы по любому из признаков (система непересекающихся множеств).
        var parent = items.ToDictionary(i => i.Id, i => i.Id);
        Guid Root(Guid id) => parent[id] == id ? id : parent[id] = Root(parent[id]);
        var reasons = new Dictionary<Guid, HashSet<string>>();
        void Join(IEnumerable<LibraryItem> same, string reason)
        {
            var list = same.ToList();
            if (list.Count < 2)
                return;
            foreach (var other in list.Skip(1))
                parent[Root(other.Id)] = Root(list[0].Id);
            foreach (var item in list)
                (reasons.TryGetValue(item.Id, out var r) ? r : reasons[item.Id] = []).Add(reason);
        }

        foreach (var group in items.GroupBy(i => LooseKey(i.Designation)).Where(g => g.Key.Length > 0))
        {
            ct.ThrowIfCancellationRequested();
            Join(group, "обозначения отличаются только пробелами, знаками или регистром");
        }
        var byHash = items.SelectMany(i => new[] { i.FileHash, i.SourceHash }.OfType<string>().Distinct().Select(h => (Hash: h, Item: i)))
            .GroupBy(x => x.Hash);
        foreach (var group in byHash)
        {
            ct.ThrowIfCancellationRequested();
            Join(group.Select(x => x.Item).DistinctBy(i => i.Id), "одинаковый файл");
        }

        const string sameShape = "одинаковая геометрия (объём и площадь поверхности)";
        foreach (var (first, second) in LibraryGeometry.MatchingPairs(
                     items.Select(i => (i, (int)i.DocumentType, i.Volume, i.Area)), ct))
            Join([first, second], sameShape);
        ct.ThrowIfCancellationRequested();

        return items.Where(i => reasons.ContainsKey(i.Id))
            .GroupBy(i => Root(i.Id))
            .Select(g =>
            {
                var groupReasons = g.SelectMany(i => reasons[i.Id]).Distinct().ToList();
                return new DuplicateGroup(
                    string.Join("; ", groupReasons),
                    g.OrderBy(i => i.Designation, NaturalComparer.Instance)
                        .Select(i => new DuplicateEntry(Summary(i, catalog), usedIn.GetValueOrDefault(i.Id), i.CreatedAt)).ToList(),
                    groupReasons is [sameShape]);
            })
            .OrderBy(g => g.Items[0].Item.Designation, NaturalComparer.Instance)
            .ToList();
    }

    /// <summary>
    /// Ключ для поиска дубликатов: без пробелов, регистра и знаков препинания — кроме знака между
    /// двумя числами: он разделяет их («1,6» ≠ «16», «20.109» ≠ «2010.9»), поэтому заменяется точкой.
    /// </summary>
    public static string LooseKey(string designation)
    {
        var key = DesignationNormalizer.ToKey(designation);
        var sb = new System.Text.StringBuilder(key.Length);
        for (var i = 0; i < key.Length; i++)
        {
            var c = key[i];
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(c);
                continue;
            }

            // Знаки подряд («-.») — один разделитель; ставим его, только если он между цифрами.
            var next = i + 1;
            while (next < key.Length && !char.IsLetterOrDigit(key[next]))
                next++;
            if (sb.Length > 0 && char.IsDigit(sb[^1]) && next < key.Length && char.IsDigit(key[next]))
                sb.Append('.');
            i = next - 1;
        }
        return sb.ToString();
    }

    /// <summary>
    /// Объединяет дубликаты: сборки библиотеки перевязываются на оставляемый компонент, лишние
    /// удаляются (файл и запись). Путь удалённого запоминается — сборки проектов, где он стоял,
    /// при открытии в Inventor получат оставленный компонент.
    /// </summary>
    public async Task MergeDuplicatesAsync(Guid keepId, IReadOnlyCollection<Guid> removeIds, CancellationToken ct = default)
    {
        var storage = storageFactory();
        await using var lease = await EnterWriteAsync(storage.Root, ct).ConfigureAwait(false);
        if (removeIds.Contains(keepId))
            throw new ArgumentException("Оставляемый компонент не может быть в списке удаляемых.", nameof(removeIds));

        foreach (var removeId in removeIds)
        {
            ct.ThrowIfCancellationRequested();
            await using var db = database.CreateContext();
            var keep = await db.LibraryItems.SingleAsync(i => i.Id == keepId, ct).ConfigureAwait(false);
            var remove = await db.LibraryItems.SingleAsync(i => i.Id == removeId, ct).ConfigureAwait(false);
            if (keep.DocumentType != remove.DocumentType)
                throw new InvalidOperationException($"«{remove.Designation}» и «{keep.Designation}»: деталь нельзя объединить со сборкой.");

            var keepFull = storage.GetFullPath(keep.RelativePath);
            var removeFull = storage.GetFullPath(remove.RelativePath);
            if (!File.Exists(keepFull))
                throw new InvalidOperationException($"Нет файла оставляемого компонента: {keep.RelativePath}.");

            var backups = new List<(string Path, string Backup)>();
            string? staged = null;
            var preserveBackups = false;
            var commit = new LibraryMutationCommit(lease, CreateVerificationContext);
            try
            {
                // Сборки библиотеки: ссылки в файлах и состав в базе — на оставляемый компонент.
                await RelinkAssembliesAsync(db, storage, removeId, removeFull, keepFull, backups, lease, ct).ConfigureAwait(false);
                var references = await db.LibraryItemReferences.Where(r => r.ComponentId == removeId).ToListAsync(ct).ConfigureAwait(false);
                foreach (var reference in references)
                {
                    db.LibraryItemReferences.Remove(reference);
                    if (!await db.LibraryItemReferences.AnyAsync(r => r.AssemblyId == reference.AssemblyId && r.ComponentId == keepId, ct).ConfigureAwait(false))
                        db.LibraryItemReferences.Add(new LibraryItemReference { AssemblyId = reference.AssemblyId, ComponentId = keepId });
                }

                db.LibraryItems.Remove(remove);
                db.LibraryLog.Add(new LibraryLogEntry
                {
                    At = DateTime.UtcNow,
                    User = Environment.UserName,
                    Action = "merge",
                    ItemId = keepId,
                    Details = JsonSerializer.Serialize(new { remove.Designation, remove.RelativePath, mergedId = removeId, into = keep.Designation }),
                });
                if (File.Exists(removeFull) && !SamePath(removeFull, keepFull))
                {
                    staged = removeFull + ".pdmlite-merge-" + Guid.NewGuid().ToString("N");
                    lease.SetCopies(backups.Select(b => new RecoveryCopy(b.Path, b.Backup)).Append(new RecoveryCopy(removeFull, staged)));
                    File.Move(removeFull, staged);
                }
                await commit.SaveAsync(db, ct).ConfigureAwait(false);
            }
            catch (Exception failure)
            {
                var available = backups.Select(b => new RecoveryCopy(b.Path, b.Backup)).ToList();
                if (staged is not null) available.Add(new RecoveryCopy(removeFull, staged));
                if (!await commit.CanRollbackAsync().ConfigureAwait(false))
                {
                    preserveBackups = true;
                    throw commit.Preserve(failure, available);
                }
                var errors = new List<Exception>();
                RestoreBackups(backups, errors);
                if (staged is not null && File.Exists(staged))
                    AttemptRollback(() => File.Move(staged, removeFull), removeFull, errors);
                if (errors.Count > 0)
                {
                    preserveBackups = true;
                    var copies = backups.Select(b => new RecoveryCopy(b.Path, b.Backup)).ToList();
                    if (staged is not null && File.Exists(staged)) copies.Add(new(removeFull, staged));
                    lease.Preserve("RollbackIncomplete", copies);
                    throw new LibraryRollbackException(failure, errors, copies);
                }
                throw;
            }
            finally
            {
                if (!preserveBackups)
                    foreach (var (_, backup) in backups) TryDelete(backup);
            }

            if (staged is not null) TryDelete(staged);
            try { storage.RemoveEmptyFolders(Path.GetDirectoryName(removeFull)!); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { logger.LogWarning(ex, "Не удалось убрать пустую папку после объединения {Path}", removeFull); }
            logger.LogInformation("Дубликат {Removed} объединён с {Kept}", remove.Designation, keep.Designation);
        }
    }

    private static LibraryItemSummary Summary(LibraryItem i, LibraryCatalog catalog) =>
        new(i.Id, i.Designation, i.Kind, i.DocumentType, i.Standard, i.Manufacturer, i.CategoryId, catalog.Get(i.CategoryId)?.Path ?? "?",
            i.RelativePath, i.Version, i.ModifiedAt, i.ModifiedBy, i.StandardIsManual);

    /// <summary>Удаляет компонент: файл и запись в базе. Нельзя, если он входит в сборку библиотеки.</summary>
    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var storage = storageFactory();
        await using var lease = await EnterWriteAsync(storage.Root, ct).ConfigureAwait(false);
        var usedIn = await UsedInAsync(id, ct).ConfigureAwait(false);
        if (usedIn.Count > 0)
            throw new InvalidOperationException("Компонент входит в сборки библиотеки: " + string.Join(", ", usedIn) + ". Сначала удалите их.");

        await using var db = database.CreateContext();
        var item = await db.LibraryItems.SingleAsync(i => i.Id == id, ct).ConfigureAwait(false);
        var full = storage.GetFullPath(item.RelativePath);

        db.LibraryItems.Remove(item);
        db.LibraryLog.Add(new LibraryLogEntry
        {
            At = DateTime.UtcNow,
            User = Environment.UserName,
            Action = "delete",
            ItemId = id,
            Details = JsonSerializer.Serialize(new { item.Designation, item.RelativePath }),
        });
        // До фиксации БД файл остаётся восстановимым. Занятый файл — ошибка до удаления записи.
        var commit = new LibraryMutationCommit(lease, CreateVerificationContext);
        var staged = full + ".pdmlite-delete-" + Guid.NewGuid().ToString("N");
        var hasFile = File.Exists(full);
        lease.SetCopies(hasFile ? [new RecoveryCopy(full, staged)] : []);
        if (hasFile) File.Move(full, staged);
        try
        {
            await commit.SaveAsync(db, ct).ConfigureAwait(false);
        }
        catch (Exception failure)
        {
            if (!await commit.CanRollbackAsync().ConfigureAwait(false))
                throw commit.Preserve(failure, hasFile ? [new RecoveryCopy(full, staged)] : []);
            if (hasFile)
            {
                try { File.Move(staged, full); }
                catch (Exception restore) when (restore is IOException or UnauthorizedAccessException)
                {
                    lease.Preserve("RollbackIncomplete", [new RecoveryCopy(full, staged)]);
                    throw new AggregateException($"Не удалось вернуть файл после сбоя удаления. Копия: {staged}", failure, restore);
                }
            }
            throw;
        }
        // БД уже зафиксирована: ошибка очистки не должна выдавать удаление за неуспешное.
        TryDelete(staged);
        try { storage.RemoveEmptyFolders(Path.GetDirectoryName(full)!); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { logger.LogWarning(ex, "Не удалось убрать пустую папку после удаления {Path}", full); }
        logger.LogInformation("Компонент удалён из библиотеки: {Designation}", item.Designation);
    }

    /// <summary>Сборки библиотеки, в которые входит компонент, — на новый путь файла.</summary>
    private async Task RelinkAssembliesAsync(PdmDbContext db, LibraryStorage storage, Guid componentId, string oldFull, string newFull,
        List<(string Path, string Backup)> backups, LibraryWriteLease lease, CancellationToken ct)
    {
        var assemblies = await db.LibraryItemReferences.Where(r => r.ComponentId == componentId)
            .Join(db.LibraryItems, r => r.AssemblyId, i => i.Id, (_, i) => i).Include(i => i.Versions).ToListAsync(ct).ConfigureAwait(false);
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [oldFull] = newFull };

        foreach (var assembly in assemblies)
        {
            var path = storage.GetFullPath(assembly.RelativePath);
            backups.Add((path, LibraryStorage.Backup(path)));
            lease.SetCopies(backups.Select(b => new RecoveryCopy(b.Path, b.Backup)));
            LibraryStorage.ClearReadOnly(path);
            await properties.ReplaceReferencesAsync(path, map, ct).ConfigureAwait(false);
            var remaining = (await properties.ReadWithoutGeometryAsync(path, ct).ConfigureAwait(false)).References
                .Where(r => ReferenceKey(r) == ReferenceKey(oldFull)).ToList();
            if (remaining.Count > 0)
                throw new InvalidOperationException($"В сборке «{Path.GetFileName(path)}» не заменились ссылки: {oldFull}. Изменение отменено.");
            LibraryStorage.MakeReadOnly(path);
            (assembly.FileHash, assembly.FileSize) = await LibraryStorage.HashAsync(path, ct).ConfigureAwait(false);
            assembly.ModifiedAt = DateTime.UtcNow;
            assembly.ModifiedBy = Environment.UserName;
            if (assembly.Versions.SingleOrDefault(v => v.Version == assembly.Version) is { } current)
                (current.FileHash, current.FileSize) = (assembly.FileHash, assembly.FileSize);
        }
    }

    private static string ReferenceKey(string path) => Path.GetFullPath(path).Replace('\\', '/').ToUpperInvariant();

    /// <summary>Карточка «было/стало» для журнала.</summary>
    private sealed record ItemSnapshot(string Designation, LibraryItemKind Kind, string? Standard, string? Manufacturer, string? Category, string RelativePath);

    private static ItemSnapshot Snapshot(LibraryItem item, string? categoryPath) =>
        new(item.Designation, item.Kind, item.Standard, item.Manufacturer, categoryPath, item.RelativePath);

    private static bool SamePath(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private void RestoreBackups(IEnumerable<(string Path, string Backup)> backups, List<Exception> errors)
    {
        foreach (var (path, backup) in backups)
            AttemptRollback(() => LibraryStorage.Restore(backup, path), path, errors);
    }
    private void AttemptRollback(Action restore, string path, List<Exception> errors)
    {
        try { restore(); }
        catch (Exception ex)
        {
            logger.LogError(ex, "Неполное восстановление {Path}", path);
            errors.Add(new IOException($"{path}: {ex.Message}", ex));
        }
    }
    private void CleanupFolders(LibraryStorage storage, string folder)
    {
        try { storage.RemoveEmptyFolders(folder); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { logger.LogWarning(ex, "Изменение сохранено, но пустая папка не удалена: {Path}", folder); }
    }

    /// <summary>Удаляет файл. Не удалось — не ошибка операции, только запись в журнал.</summary>
    private void TryDelete(string path)
    {
        try
        {
            LibraryStorage.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Не удалось удалить файл {Path}", path);
        }
    }
}
