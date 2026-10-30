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

/// <summary>Новые значения карточки компонента.</summary>
public sealed record LibraryItemEdit(string Designation, LibraryItemKind Kind, string? Manufacturer, int? CategoryId);

/// <summary>Результат проверки изменения: замечания и путь файла после изменения.</summary>
public sealed record LibraryItemEditCheck(IReadOnlyList<ImportIssue> Issues, string? NewRelativePath, string CanonicalDesignation, string? Standard)
{
    public bool HasErrors => Issues.Any(i => i.Severity == ImportSeverity.Error);
}

/// <summary>
/// Просмотр, изменение и удаление компонентов библиотеки.
/// </summary>
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
        var issues = new List<ImportIssue>();
        void Error(string m) => issues.Add(new ImportIssue(ImportSeverity.Error, m));
        void Warn(string m) => issues.Add(new ImportIssue(ImportSeverity.Warning, m));

        var designation = DesignationNormalizer.ToCanonical(edit.Designation ?? "");
        var standard = edit.Kind == LibraryItemKind.Standard ? StandardParser.Find(designation) : null;

        if (!properties.IsAvailable)
            Error($"Нет доступа к свойствам файлов Inventor: {properties.UnavailableReason}");
        if (designation.Length == 0)
        {
            Error("Не указано обозначение.");
            return new LibraryItemEditCheck(issues, null, designation, standard);
        }

        var key = DesignationNormalizer.ToKey(designation);
        var other = await db.LibraryItems.AsNoTracking().Where(i => i.DesignationKey == key && i.Id != id)
            .Select(i => i.Designation).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (other is not null)
            Error($"В библиотеке уже есть «{other}».");

        if (edit.Kind == LibraryItemKind.Standard && standard is null)
            Error("В обозначении не найден стандарт (ГОСТ, ISO, DIN…). Исправьте обозначение или выберите тип «Покупное».");
        if (edit.Kind == LibraryItemKind.Purchased && string.IsNullOrWhiteSpace(edit.Manufacturer))
            Warn($"Не указан производитель — компонент попадёт в папку «{LibraryStorage.NoManufacturerFolder}».");

        var category = catalog.Get(edit.CategoryId);
        string? newPath = null;
        if (category is null)
            Error("Не выбран раздел библиотеки.");
        else if (!category.IsLeaf)
            Error($"«{category.Path}» — раздел; выберите группу внутри него.");
        else
        {
            var subfolder = edit.Kind == LibraryItemKind.Standard
                ? standard
                : string.IsNullOrWhiteSpace(edit.Manufacturer) ? LibraryStorage.NoManufacturerFolder : edit.Manufacturer.Trim();
            newPath = LibraryStorage.BuildRelativePath(category.PathParts, subfolder, designation, Path.GetExtension(item.RelativePath));

            var storage = storageFactory();
            var full = storage.GetFullPath(newPath);
            var moved = !string.Equals(newPath, item.RelativePath, StringComparison.OrdinalIgnoreCase);
            if (full.Length > LibraryStorage.MaxPathLength)
                Error($"Слишком длинный путь в библиотеке ({full.Length} символов, допустимо {LibraryStorage.MaxPathLength}).");
            else if (moved && File.Exists(full))
                Error($"В папке библиотеки уже есть файл {newPath}.");

            if (moved)
            {
                var usedIn = await UsedInAsync(id, ct).ConfigureAwait(false);
                if (usedIn.Count > 0)
                    issues.Add(new ImportIssue(ImportSeverity.Info,
                        "Сборки библиотеки, в которые входит компонент, будут перевязаны на новый файл: " + string.Join(", ", usedIn) + "."));
                Warn("Файл будет переименован или перенесён. Если вы уже вставляли этот компонент в сборки проектов, " +
                     "в них придётся заменить ссылку (автоматическое обновление — в следующих шагах).");
            }
        }

        return new LibraryItemEditCheck(issues, newPath, designation, standard);
    }

    /// <summary>Применяет изменение карточки. При ошибке всё возвращается как было.</summary>
    public async Task ApplyEditAsync(LibraryCatalog catalog, Guid id, LibraryItemEdit edit, CancellationToken ct = default)
    {
        var check = await CheckEditAsync(catalog, id, edit, ct).ConfigureAwait(false);
        if (check.HasErrors)
            throw new InvalidOperationException(string.Join(" ", check.Issues.Where(i => i.Severity == ImportSeverity.Error).Select(i => i.Message)));

        var storage = storageFactory();
        var user = Environment.UserName;
        var category = catalog.Get(edit.CategoryId)!;

        await using var db = database.CreateContext();
        var item = await db.LibraryItems.Include(i => i.Versions).SingleAsync(i => i.Id == id, ct).ConfigureAwait(false);
        var before = new { item.Designation, item.Kind, item.Standard, item.Manufacturer, Category = catalog.Get(item.CategoryId)?.Path, item.RelativePath };

        var oldFull = storage.GetFullPath(item.RelativePath);
        var newFull = storage.GetFullPath(check.NewRelativePath!);
        var moved = !string.Equals(oldFull, newFull, StringComparison.OrdinalIgnoreCase);
        var manufacturer = string.IsNullOrWhiteSpace(edit.Manufacturer) ? null : edit.Manufacturer.Trim();

        // Резервная копия файла на время операции: свойства в нём перезаписываются.
        var backup = Path.Combine(Path.GetTempPath(), $"pdmlite-{Guid.NewGuid():N}{Path.GetExtension(oldFull)}");
        File.Copy(oldFull, backup);
        var relinked = new List<(string Path, string Backup)>();
        try
        {
            ClearReadOnly(oldFull);
            if (moved)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(newFull)!);
                File.Move(oldFull, newFull);
            }

            await properties.WriteAsync(newFull, new DocumentPropertyChanges
            {
                PartNumber = check.CanonicalDesignation,
                Description = check.CanonicalDesignation,
                Title = check.CanonicalDesignation,
                Custom = new Dictionary<string, string>
                {
                    [LibraryImportService.PropertyStandard] = check.Standard ?? "",
                    [LibraryImportService.PropertyManufacturer] = manufacturer ?? "",
                    [LibraryImportService.PropertyCategory] = category.Path,
                },
            }, ct).ConfigureAwait(false);
            LibraryStorage.MakeReadOnly(newFull);
            var (hash, size) = await LibraryStorage.HashAsync(newFull, ct).ConfigureAwait(false);

            // Сборки библиотеки, ссылающиеся на компонент, — на новый путь.
            if (moved)
            {
                var assemblies = await db.LibraryItemReferences.Where(r => r.ComponentId == id)
                    .Join(db.LibraryItems, r => r.AssemblyId, i => i.Id, (_, i) => i).ToListAsync(ct).ConfigureAwait(false);
                foreach (var assembly in assemblies)
                {
                    var asmPath = storage.GetFullPath(assembly.RelativePath);
                    var asmBackup = Path.Combine(Path.GetTempPath(), $"pdmlite-{Guid.NewGuid():N}.iam");
                    File.Copy(asmPath, asmBackup);
                    relinked.Add((asmPath, asmBackup));
                    ClearReadOnly(asmPath);
                    await properties.ReplaceReferencesAsync(asmPath, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [oldFull] = newFull }, ct).ConfigureAwait(false);
                    LibraryStorage.MakeReadOnly(asmPath);
                    var (asmHash, asmSize) = await LibraryStorage.HashAsync(asmPath, ct).ConfigureAwait(false);
                    assembly.FileHash = asmHash;
                    assembly.FileSize = asmSize;
                }
            }

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
                Details = JsonSerializer.Serialize(new
                {
                    before,
                    after = new { item.Designation, item.Kind, item.Standard, item.Manufacturer, Category = category.Path, item.RelativePath },
                }),
            });
            if (categoryChanged)
                await LibraryCatalog.LearnAsync(db, item.Designation, item.Standard, category.Id, ct).ConfigureAwait(false);

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            logger.LogInformation("Компонент библиотеки изменён: {Before} → {After}", before.Designation, item.Designation);
        }
        catch
        {
            // Откат: исходный файл и перевязанные сборки — из резервных копий.
            TryRestore(backup, oldFull, moved ? newFull : null);
            foreach (var (path, copy) in relinked)
                TryRestore(copy, path, null);
            throw;
        }
        finally
        {
            TryDelete(backup);
            foreach (var (_, copy) in relinked)
                TryDelete(copy);
        }

        if (moved)
            RemoveEmptyFolders(storage, Path.GetDirectoryName(oldFull)!);
    }

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

        storage.Remove(full);
        RemoveEmptyFolders(storage, Path.GetDirectoryName(full)!);
        logger.LogInformation("Компонент удалён из библиотеки: {Designation}", item.Designation);
    }

    private static void ClearReadOnly(string path) =>
        File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);

    private void TryRestore(string backup, string original, string? moved)
    {
        try
        {
            if (moved is not null && File.Exists(moved))
            {
                ClearReadOnly(moved);
                File.Delete(moved);
            }
            if (File.Exists(original))
                ClearReadOnly(original);
            File.Copy(backup, original, overwrite: true);
            LibraryStorage.MakeReadOnly(original);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Не удалось восстановить {Path} из резервной копии {Backup}", original, backup);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>Убирает опустевшие папки до корня библиотеки.</summary>
    private static void RemoveEmptyFolders(LibraryStorage storage, string folder)
    {
        var dir = new DirectoryInfo(folder);
        while (dir is not null && storage.Contains(dir.FullName) && dir.Exists && !dir.EnumerateFileSystemInfos().Any())
        {
            var parent = dir.Parent;
            dir.Delete();
            dir = parent;
        }
    }
}
