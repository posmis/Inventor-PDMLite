using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PdmLite.Core.Documents;
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
    string ModifiedBy);

/// <summary>Итог обновления свойств: проверено, записано, уже в порядке, ошибки.</summary>
public sealed record RewriteResult(int Checked, int Updated, int UpToDate, IReadOnlyList<string> Failed);

/// <summary>Новые значения карточки компонента.</summary>
public sealed record LibraryItemEdit(string Designation, LibraryItemKind Kind, string? Manufacturer, int? CategoryId);

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
        var items = await db.LibraryItems.AsNoTracking().OrderBy(i => i.Designation).ToListAsync(ct).ConfigureAwait(false);
        return items.Select(i => new LibraryItemSummary(i.Id, i.Designation, i.Kind, i.DocumentType, i.Standard, i.Manufacturer,
            i.CategoryId, catalog.Get(i.CategoryId)?.Path ?? "?", i.RelativePath, i.Version, i.ModifiedAt, i.ModifiedBy)).ToList();
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
            Path.GetExtension(item.RelativePath));
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
        var check = await CheckEditAsync(catalog, id, edit, ct).ConfigureAwait(false);
        if (check.HasErrors)
            throw new InvalidOperationException(string.Join(" ", check.Issues.Where(i => i.Severity == ImportSeverity.Error).Select(i => i.Message)));

        var storage = storageFactory();
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
        try
        {
            LibraryStorage.ClearReadOnly(oldFull);
            if (moved)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(newFull)!);
                File.Move(oldFull, newFull);
            }

            await properties.WriteAsync(newFull, LibraryProperties.For(check.CanonicalDesignation, edit.Kind, check.Standard, manufacturer, category.Path, id), ct)
                .ConfigureAwait(false);
            LibraryStorage.MakeReadOnly(newFull);
            var (hash, size) = await LibraryStorage.HashAsync(newFull, ct).ConfigureAwait(false);

            if (moved)
                await RelinkAssembliesAsync(db, storage, id, oldFull, newFull, backups, ct).ConfigureAwait(false);

            var now = DateTime.UtcNow;
            var categoryChanged = item.CategoryId != category.Id;
            item.Designation = check.CanonicalDesignation;
            item.DesignationKey = DesignationNormalizer.ToKey(check.CanonicalDesignation);
            item.Kind = edit.Kind;
            item.Standard = check.Standard;
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

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            logger.LogInformation("Компонент библиотеки изменён: {Before} → {After}", before.Designation, item.Designation);
        }
        catch
        {
            if (moved)
                TryDelete(newFull);
            foreach (var (path, backup) in backups)
                TryRestore(backup, path);
            throw;
        }
        finally
        {
            foreach (var (_, backup) in backups)
                TryDelete(backup);
        }

        if (moved)
            storage.RemoveEmptyFolders(Path.GetDirectoryName(oldFull)!);
    }

    /// <summary>
    /// Служебная функция: приводит свойства PdmLite в файлах библиотеки в соответствие с базой
    /// (например, после появления нового свойства — раздела спецификации).
    /// </summary>
    /// <remarks>
    /// Безопасность на большой библиотеке: сначала свойства читаются, и записываются только файлы,
    /// где они отличаются; перед записью — резервная копия файла, при ошибке файл восстанавливается;
    /// база (контрольная сумма) обновляется после каждого файла; остановка — между файлами.
    /// </remarks>
    /// <param name="ids">Какие компоненты обработать; <c>null</c> — вся библиотека.</param>
    public async Task<RewriteResult> RewritePropertiesAsync(LibraryCatalog catalog, IReadOnlyCollection<Guid>? ids,
        IProgress<(int Done, int Total, string? Current)>? progress = null, CancellationToken ct = default)
    {
        if (!properties.IsAvailable)
            throw new InvalidOperationException($"Нет доступа к свойствам файлов Inventor: {properties.UnavailableReason}");

        var storage = storageFactory();
        await using var db = database.CreateContext();
        var query = db.LibraryItems.Include(i => i.Versions).AsQueryable();
        if (ids is not null)
            query = query.Where(i => ids.Contains(i.Id));
        var items = await query.OrderBy(i => i.Designation).ToListAsync(ct).ConfigureAwait(false);

        var failed = new List<string>();
        int updated = 0, upToDate = 0, done = 0;
        try
        {
            foreach (var item in items)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report((done, items.Count, item.Designation));
                var full = storage.GetFullPath(item.RelativePath);
                var expected = LibraryProperties.For(item.Designation, item.Kind, item.Standard, item.Manufacturer,
                    catalog.Get(item.CategoryId)?.Path ?? "", item.Id);
                try
                {
                    if (!File.Exists(full))
                        throw new FileNotFoundException("Файл не найден в папке библиотеки.", full);
                    if (IsUpToDate(await properties.ReadAsync(full, ct).ConfigureAwait(false), expected))
                        upToDate++;
                    else
                    {
                        await WriteSafelyAsync(full, expected, ct).ConfigureAwait(false);
                        (item.FileHash, item.FileSize) = await LibraryStorage.HashAsync(full, ct).ConfigureAwait(false);
                        if (item.Versions.FirstOrDefault(v => v.Version == item.Version) is { } current)
                            (current.FileHash, current.FileSize) = (item.FileHash, item.FileSize);
                        await db.SaveChangesAsync(ct).ConfigureAwait(false);
                        updated++;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Не удалось обновить свойства {Path}", full);
                    failed.Add($"{item.Designation}: {ex.Message}");
                }
                done++;
            }
        }
        finally
        {
            // Журнал пишется и при остановке: видно, сколько успели.
            db.ChangeTracker.Clear();
            db.LibraryLog.Add(new LibraryLogEntry
            {
                At = DateTime.UtcNow,
                User = Environment.UserName,
                Action = "rewrite-properties",
                Details = JsonSerializer.Serialize(new { total = items.Count, done, updated, upToDate, failed = failed.Count }),
            });
            await db.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
        }

        progress?.Report((items.Count, items.Count, null));
        return new RewriteResult(items.Count, updated, upToDate, failed);
    }

    /// <summary>Запись свойств с резервной копией: при ошибке файл возвращается как был.</summary>
    private async Task WriteSafelyAsync(string full, DocumentPropertyChanges changes, CancellationToken ct)
    {
        var backup = LibraryStorage.Backup(full);
        try
        {
            LibraryStorage.ClearReadOnly(full);
            await properties.WriteAsync(full, changes, ct).ConfigureAwait(false);
            LibraryStorage.MakeReadOnly(full);
        }
        catch
        {
            TryRestore(backup, full);
            throw;
        }
        finally
        {
            TryDelete(backup);
        }
    }

    /// <summary>Свойства в файле уже такие, какие записал бы PdmLite.</summary>
    internal static bool IsUpToDate(DocumentProperties current, DocumentPropertyChanges expected) =>
        current.PartNumber == expected.PartNumber
        && current.Description == expected.Description
        && current.Title == expected.Title
        && expected.Custom.All(p => current.Custom.TryGetValue(p.Key, out var value) && value == p.Value);

    /// <summary>Удаляет компонент: файл и запись в базе. Нельзя, если он входит в сборку библиотеки.</summary>
    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var usedIn = await UsedInAsync(id, ct).ConfigureAwait(false);
        if (usedIn.Count > 0)
            throw new InvalidOperationException("Компонент входит в сборки библиотеки: " + string.Join(", ", usedIn) + ". Сначала удалите их.");

        var storage = storageFactory();
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
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        LibraryStorage.Delete(full);
        storage.RemoveEmptyFolders(Path.GetDirectoryName(full)!);
        logger.LogInformation("Компонент удалён из библиотеки: {Designation}", item.Designation);
    }

    /// <summary>Сборки библиотеки, в которые входит компонент, — на новый путь файла.</summary>
    private async Task RelinkAssembliesAsync(PdmDbContext db, LibraryStorage storage, Guid componentId, string oldFull, string newFull,
        List<(string Path, string Backup)> backups, CancellationToken ct)
    {
        var assemblies = await db.LibraryItemReferences.Where(r => r.ComponentId == componentId)
            .Join(db.LibraryItems, r => r.AssemblyId, i => i.Id, (_, i) => i).ToListAsync(ct).ConfigureAwait(false);
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [oldFull] = newFull };

        foreach (var assembly in assemblies)
        {
            var path = storage.GetFullPath(assembly.RelativePath);
            backups.Add((path, LibraryStorage.Backup(path)));
            LibraryStorage.ClearReadOnly(path);
            await properties.ReplaceReferencesAsync(path, map, ct).ConfigureAwait(false);
            LibraryStorage.MakeReadOnly(path);
            (assembly.FileHash, assembly.FileSize) = await LibraryStorage.HashAsync(path, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Карточка «было/стало» для журнала.</summary>
    private sealed record ItemSnapshot(string Designation, LibraryItemKind Kind, string? Standard, string? Manufacturer, string? Category, string RelativePath);

    private static ItemSnapshot Snapshot(LibraryItem item, string? categoryPath) =>
        new(item.Designation, item.Kind, item.Standard, item.Manufacturer, categoryPath, item.RelativePath);

    private static bool SamePath(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private void TryRestore(string backup, string path)
    {
        try
        {
            LibraryStorage.Restore(backup, path);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Не удалось восстановить {Path} из резервной копии {Backup}", path, backup);
        }
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
