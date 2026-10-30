using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PdmLite.Core.Documents;
using PdmLite.Data;
using PdmLite.Data.Library;
using PdmLite.Library.Catalog;
using PdmLite.Library.Designations;
using PdmLite.Library.Storage;

namespace PdmLite.Library.Import;

/// <summary>
/// Добавление компонентов в библиотеку: анализ файлов → проверки → внесение
/// (копирование в папку библиотеки, запись свойств, запись в базу).
/// </summary>
public sealed class LibraryImportService(
    IPdmDatabase database,
    IDocumentPropertyStore properties,
    Func<LibraryStorage> storageFactory,
    ILogger<LibraryImportService> logger)
{
    private static readonly HashSet<string> SkippedFolders = new(StringComparer.OrdinalIgnoreCase) { "OldVersions" };
    private static readonly string[] InventorExtensions = [".ipt", ".iam"];

    /// <summary>Сканирует файлы и папки, читает свойства, предлагает обозначение и раздел, проверяет.</summary>
    public async Task<ImportSession> AnalyzeAsync(IEnumerable<string> inputs, IProgress<ImportProgress>? progress = null,
        CancellationToken ct = default)
    {
        var session = await CreateSessionAsync(ct).ConfigureAwait(false);
        await AddAsync(session, inputs, progress, ct).ConfigureAwait(false);
        return session;
    }

    /// <summary>Добавляет файлы к сеансу (уже добавленные пропускаются) и перепроверяет список.</summary>
    public async Task AddAsync(ImportSession session, IEnumerable<string> inputs, IProgress<ImportProgress>? progress = null,
        CancellationToken ct = default)
    {
        var known = session.Candidates.Select(c => c.SourcePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var files = Enumerate(inputs, storageFactory(), session.Skipped).Where(known.Add).ToList();

        for (var i = 0; i < files.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new ImportProgress(i, files.Count, Path.GetFileName(files[i])));
            session.Candidates.Add(await AnalyzeFileAsync(files[i], session.Catalog, ct).ConfigureAwait(false));
        }

        progress?.Report(new ImportProgress(files.Count, files.Count, null));
        Validate(session);
    }

    private async Task<ImportSession> CreateSessionAsync(CancellationToken ct)
    {
        await database.MigrateAsync(ct).ConfigureAwait(false);
        await using var db = database.CreateContext();
        await LibraryCatalog.SeedAsync(db, ct).ConfigureAwait(false);

        var catalog = await LibraryCatalog.LoadAsync(db, ct).ConfigureAwait(false);
        var items = await db.LibraryItems.AsNoTracking()
            .Select(i => new { i.DesignationKey, i.Designation, i.FileHash, i.SourceHash })
            .ToListAsync(ct).ConfigureAwait(false);

        var keys = items.ToDictionary(i => i.DesignationKey, i => i.Designation);
        var hashes = new Dictionary<string, string>();
        foreach (var item in items)
        {
            hashes.TryAdd(item.FileHash, item.Designation);
            if (item.SourceHash is not null)
                hashes.TryAdd(item.SourceHash, item.Designation);
        }

        return new ImportSession(catalog, keys, hashes, properties.IsAvailable, properties.UnavailableReason);
    }

    private static List<string> Enumerate(IEnumerable<string> inputs, LibraryStorage storage, List<SkippedFile> skipped)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Consider(string file)
        {
            var full = Path.GetFullPath(file);
            if (!seen.Add(full))
                return;

            if (!InventorExtensions.Contains(Path.GetExtension(full), StringComparer.OrdinalIgnoreCase))
                skipped.Add(new SkippedFile(full, "Не деталь и не сборка Inventor"));
            else if (full.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(SkippedFolders.Contains))
                skipped.Add(new SkippedFile(full, "Резервная копия Inventor (OldVersions)"));
            else if (storage.Contains(full))
                skipped.Add(new SkippedFile(full, "Уже в папке библиотеки"));
            else
                result.Add(full);
        }

        foreach (var input in inputs)
        {
            if (Directory.Exists(input))
            {
                var files = Directory.EnumerateFiles(input, "*.*", new EnumerationOptions
                    {
                        RecurseSubdirectories = true,
                        IgnoreInaccessible = true,
                        // Ярлыки на папки (junction) могут зациклить обход.
                        AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Hidden | FileAttributes.System,
                    })
                    .Where(f => InventorExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                    .Order(StringComparer.CurrentCultureIgnoreCase);
                foreach (var file in files)
                    Consider(file);
            }
            else if (File.Exists(input))
            {
                Consider(input);
            }
        }

        return result;
    }

    private async Task<ImportCandidate> AnalyzeFileAsync(string path, LibraryCatalog catalog, CancellationToken ct)
    {
        var documentType = Path.GetExtension(path).Equals(".iam", StringComparison.OrdinalIgnoreCase)
            ? LibraryDocumentType.Assembly
            : LibraryDocumentType.Part;

        // Занятый или недоступный файл не должен прерывать анализ остальных.
        string hash;
        long size;
        try
        {
            (hash, size) = await LibraryStorage.HashAsync(path, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Не удалось прочитать файл {Path}", path);
            var name = DesignationNormalizer.ToCanonical(Path.GetFileNameWithoutExtension(path));
            return new ImportCandidate { SourcePath = path, DocumentType = documentType, ReadError = ex.Message, OriginalDesignation = name, Designation = name };
        }

        DocumentProperties? props = null;
        string? error = null;
        if (properties.IsAvailable)
        {
            try
            {
                props = await properties.ReadAsync(path, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Не удалось прочитать свойства {Path}", path);
                error = ex.Message;
            }
        }

        var referenceHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var reference in props?.References ?? [])
        {
            try
            {
                if (File.Exists(reference))
                    referenceHashes[reference] = (await LibraryStorage.HashAsync(reference, ct).ConfigureAwait(false)).Hash;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Не удалось прочитать компонент сборки {Path}", reference);
            }
        }

        var original = !string.IsNullOrWhiteSpace(props?.PartNumber) ? props.PartNumber! : Path.GetFileNameWithoutExtension(path);
        var designation = DesignationNormalizer.ToCanonical(original);
        var standard = StandardParser.Find(designation);
        var suggested = catalog.Suggest(designation, standard);
        // Раздел спецификации уже записан в файле (например, другим плагином) — тип берём из него.
        var kindFromFile = props is null ? null : LibraryProperties.KindFromSpecSection(props.Custom);

        return new ImportCandidate
        {
            SourcePath = path,
            DocumentType = documentType,
            Properties = props,
            PropertiesError = error,
            FileHash = hash,
            FileSize = size,
            ReferenceHashes = referenceHashes,
            OriginalDesignation = original.Trim(),
            Designation = designation,
            Kind = kindFromFile ?? (standard is not null ? LibraryItemKind.Standard : LibraryItemKind.Purchased),
            KindFromFile = kindFromFile is not null,
            Standard = standard,
            CategoryId = suggested?.Id,
            SuggestedCategoryId = suggested?.Id,
        };
    }

    /// <summary>Проверяет все кандидаты. Вызывается после анализа и после каждой правки пользователя.</summary>
    public void Validate(ImportSession session)
    {
        var storage = storageFactory();
        var included = session.Candidates.Where(c => c.Include).ToList();
        var bySource = included.Select(c => c.SourcePath).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Размещение и обозначение — общими правилами (как при изменении карточки).
        var placements = new Dictionary<ImportCandidate, LibraryPlacement>();
        foreach (var c in session.Candidates)
        {
            var placement = LibraryRules.Place(session.Catalog, storage, c.Designation, c.Kind, c.Manufacturer,
                c.CategoryId, Path.GetExtension(c.SourcePath));
            c.Designation = placement.Designation;
            c.Standard = placement.Standard;
            c.Issues.Clear();
            c.TargetRelativePath = c.Include ? placement.RelativePath : null;
            placements[c] = placement;
        }

        var byKey = included.Where(c => c.Designation.Length > 0)
            .GroupBy(c => DesignationNormalizer.ToKey(c.Designation)).ToDictionary(g => g.Key, g => g.ToList());
        var byHash = included.Where(c => c.FileHash.Length > 0)
            .GroupBy(c => c.FileHash).ToDictionary(g => g.Key, g => g.ToList());
        var byTarget = included.Where(c => c.TargetRelativePath is not null)
            .GroupBy(c => c.TargetRelativePath!, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        foreach (var c in included)
        {
            void Error(string m) => c.Issues.Add(new ImportIssue(ImportSeverity.Error, m));
            void Warn(string m) => c.Issues.Add(new ImportIssue(ImportSeverity.Warning, m));
            void Info(string m) => c.Issues.Add(new ImportIssue(ImportSeverity.Info, m));

            // Чтение файла и свойств
            if (c.ReadError is not null)
                Error($"Файл не прочитан (занят другой программой или нет доступа): {c.ReadError}");
            else if (!session.CanWriteProperties)
                Error($"Нет доступа к свойствам файлов Inventor: {session.PropertiesUnavailableReason}");
            else if (c.Properties is null)
                Error($"Не удалось прочитать файл: {c.PropertiesError}");

            c.Issues.AddRange(placements[c].Issues);
            if (c.Designation.Length == 0)
                continue;

            var key = DesignationNormalizer.ToKey(c.Designation);
            if (c.OriginalDesignation.Length > 0 && c.OriginalDesignation != c.Designation)
                Info($"Обозначение приведено к единому виду: «{c.OriginalDesignation}» → «{c.Designation}».");

            if (c.Properties is { } p)
            {
                var fields = new[] { ("Обозначение", p.PartNumber), ("Описание", p.Description), ("Наименование", p.Title) }
                    .Where(f => !string.IsNullOrWhiteSpace(f.Item2)).ToList();
                if (fields.Any(f => DesignationNormalizer.ToKey(f.Item2!) != key))
                    Warn("Свойства файла расходятся с обозначением: "
                         + string.Join(", ", fields.Select(f => $"{f.Item1} «{f.Item2}»"))
                         + ". При внесении во все три свойства запишется обозначение.");
                if (p.IsContentCenter)
                    Warn("Файл создан из Content Center. В библиотеку он попадёт как самостоятельный компонент.");
            }

            if (c.KindFromFile)
                Info($"Тип «{(c.Kind == LibraryItemKind.Standard ? "стандартное" : "покупное")}» взят из свойства {LibraryProperties.SpecSection} в файле.");

            if (DesignationNormalizer.ToKey(Path.GetFileNameWithoutExtension(c.SourcePath)) != key)
                Info("Имя файла не совпадает с обозначением — в библиотеке файл будет назван по обозначению.");

            // Дубликаты
            if (session.ExistingKeys.TryGetValue(key, out var existing))
                Error($"В библиотеке уже есть «{existing}».");
            if (byKey.TryGetValue(key, out var sameKey) && sameKey.Count > 1)
                Error("Обозначение повторяется в списке: " + string.Join(", ", sameKey.Where(x => x != c).Select(x => x.FileName)) + ".");
            if (c.FileHash.Length > 0 && session.ExistingHashes.TryGetValue(c.FileHash, out var sameFile))
                Error($"Этот файл уже добавлен в библиотеку как «{sameFile}».");
            if (c.FileHash.Length > 0 && byHash.TryGetValue(c.FileHash, out var sameHash) && sameHash.Count > 1)
                Error("Такой же файл в списке: " + string.Join(", ", sameHash.Where(x => x != c).Select(x => x.FileName)) + ".");

            // Компоненты сборки: в папке библиотеки, в этом же списке или уже внесены из того же файла.
            if (c.DocumentType == LibraryDocumentType.Assembly && c.Properties is { } asm)
            {
                var missing = asm.References.Where(r => !storage.Contains(r) && !bySource.Contains(r)
                    && !(c.ReferenceHashes.TryGetValue(r, out var h) && session.ExistingHashes.ContainsKey(h))).ToList();
                if (missing.Count > 0)
                    Error("Компоненты сборки не в библиотеке и не в списке: "
                          + string.Join(", ", missing.Select(Path.GetFileName)) + ". Добавьте их вместе со сборкой.");
            }

            // Путь в библиотеке
            if (c.TargetRelativePath is { } target)
            {
                if (File.Exists(storage.GetFullPath(target)))
                    Error($"В папке библиотеки уже есть файл {target}.");
                if (byTarget[target] > 1)
                    Error($"Совпадает путь в библиотеке с другим файлом списка: {target}.");
            }
        }
    }

    /// <summary>
    /// Вносит готовые компоненты. Каждый — отдельно: ошибка одного не отменяет остальные,
    /// а частично скопированный файл удаляется.
    /// </summary>
    public async Task<ImportResult> CommitAsync(ImportSession session, IProgress<ImportProgress>? progress = null,
        CancellationToken ct = default)
    {
        Validate(session);
        var storage = storageFactory();
        var user = Environment.UserName;

        // Детали раньше сборок: сборке нужны уже внесённые компоненты.
        var queue = session.Candidates.Where(c => c.CanImport)
            .OrderBy(c => c.DocumentType == LibraryDocumentType.Assembly ? 1 : 0).ToList();
        var committed = new Dictionary<string, (Guid Id, string FullPath)>(StringComparer.OrdinalIgnoreCase);
        var failed = new List<(ImportCandidate, string)>();

        for (var i = 0; i < queue.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var c = queue[i];
            progress?.Report(new ImportProgress(i, queue.Count, c.FileName));

            string? copied = null;
            try
            {
                var category = session.Catalog.Get(c.CategoryId)!;
                copied = storage.CopyIn(c.SourcePath, c.TargetRelativePath!);
                var id = Guid.NewGuid();

                await using var db = database.CreateContext();
                var componentIds = c.DocumentType == LibraryDocumentType.Assembly
                    ? await RelinkAssemblyAsync(db, storage, c, copied, committed, ct).ConfigureAwait(false)
                    : [];

                await properties.WriteAsync(copied, LibraryProperties.For(c.Designation, c.Kind, c.Standard, c.Manufacturer, category.Path, id), ct)
                    .ConfigureAwait(false);
                LibraryStorage.MakeReadOnly(copied);
                var (hash, size) = await LibraryStorage.HashAsync(copied, ct).ConfigureAwait(false);
                var now = DateTime.UtcNow;
                var key = DesignationNormalizer.ToKey(c.Designation);

                await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
                db.LibraryItems.Add(new LibraryItem
                {
                    Id = id,
                    CategoryId = category.Id,
                    Kind = c.Kind,
                    DocumentType = c.DocumentType,
                    Designation = c.Designation,
                    DesignationKey = key,
                    Standard = c.Standard,
                    Manufacturer = string.IsNullOrWhiteSpace(c.Manufacturer) ? null : c.Manufacturer.Trim(),
                    RelativePath = c.TargetRelativePath!,
                    FileHash = hash,
                    FileSize = size,
                    SourceHash = c.FileHash,
                    Version = 1,
                    CreatedAt = now,
                    CreatedBy = user,
                    ModifiedAt = now,
                    ModifiedBy = user,
                    Versions =
                    [
                        new LibraryItemVersion
                        {
                            Version = 1, RelativePath = c.TargetRelativePath!, FileHash = hash, FileSize = size,
                            CreatedAt = now, CreatedBy = user, Comment = "Добавлен в библиотеку",
                        },
                    ],
                    References = componentIds.Distinct().Select(cid => new LibraryItemReference { AssemblyId = id, ComponentId = cid }).ToList(),
                });
                db.LibraryLog.Add(new LibraryLogEntry
                {
                    At = now,
                    User = user,
                    Action = "add",
                    ItemId = id,
                    Details = JsonSerializer.Serialize(new { source = c.SourcePath, path = c.TargetRelativePath, designation = c.Designation }),
                });
                if (c.CategoryId != c.SuggestedCategoryId)
                    await LibraryCatalog.LearnAsync(db, c.Designation, c.Standard, category.Id, ct).ConfigureAwait(false);

                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                await tx.CommitAsync(ct).ConfigureAwait(false);

                committed[c.SourcePath] = (id, copied);
                session.Remember(key, c.Designation, c.FileHash, hash);
                logger.LogInformation("В библиотеку добавлен {Designation} ({Path})", c.Designation, c.TargetRelativePath);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Не удалось добавить {Path}", c.SourcePath);
                if (copied is not null)
                    TryDelete(copied);
                failed.Add((c, ex is DbUpdateException { InnerException: { } inner } ? inner.Message : ex.Message));
            }
        }

        progress?.Report(new ImportProgress(queue.Count, queue.Count, null));
        return new ImportResult(committed.Count, failed);
    }

    /// <summary>Переводит ссылки скопированной сборки на файлы библиотеки; возвращает Id компонентов.</summary>
    private async Task<List<Guid>> RelinkAssemblyAsync(PdmDbContext db, LibraryStorage storage, ImportCandidate c, string copied,
        Dictionary<string, (Guid Id, string FullPath)> committed, CancellationToken ct)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var ids = new List<Guid>();
        foreach (var reference in c.Properties?.References ?? [])
        {
            if (committed.TryGetValue(reference, out var done))
            {
                map[reference] = done.FullPath;
                ids.Add(done.Id);
            }
            else if (storage.Contains(reference))
            {
                var relative = Path.GetRelativePath(storage.Root, reference).Replace(Path.DirectorySeparatorChar, '/');
                var item = await db.LibraryItems.AsNoTracking().FirstOrDefaultAsync(x => x.RelativePath == relative, ct).ConfigureAwait(false)
                           ?? throw new InvalidOperationException($"Файл {relative} лежит в папке библиотеки, но не зарегистрирован в ней.");
                ids.Add(item.Id);
            }
            else if (c.ReferenceHashes.TryGetValue(reference, out var hash)
                     && await db.LibraryItems.AsNoTracking().FirstOrDefaultAsync(x => x.SourceHash == hash || x.FileHash == hash, ct).ConfigureAwait(false) is { } known)
            {
                // Деталь внесли раньше из этого же файла — сборка будет ссылаться на её копию в библиотеке.
                map[reference] = storage.GetFullPath(known.RelativePath);
                ids.Add(known.Id);
            }
            else
            {
                throw new InvalidOperationException($"Компонент сборки «{Path.GetFileName(reference)}» не внесён в библиотеку.");
            }
        }

        if (map.Count > 0)
            await properties.ReplaceReferencesAsync(copied, map, ct).ConfigureAwait(false);
        return ids;
    }

    private void TryDelete(string path)
    {
        try
        {
            LibraryStorage.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Не удалось удалить {Path} после ошибки", path);
        }
    }
}
