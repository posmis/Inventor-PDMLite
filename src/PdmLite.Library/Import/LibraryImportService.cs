using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PdmLite.Core.Documents;
using PdmLite.Core.Naming;
using PdmLite.Data;
using PdmLite.Data.Library;
using PdmLite.Library.Catalog;
using PdmLite.Library.Designations;
using PdmLite.Library.Items;
using PdmLite.Library.Rules;
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
            session.Candidates.Add(await AnalyzeFileAsync(files[i], session, ct).ConfigureAwait(false));
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
            .Select(i => new { i.DesignationKey, i.Designation, i.FileHash, i.SourceHash, i.RelativePath, i.DocumentType, i.Volume, i.Area })
            .ToListAsync(ct).ConfigureAwait(false);

        var keys = items.ToDictionary(i => i.DesignationKey, i => i.Designation);
        var hashes = new Dictionary<string, string>();
        var paths = new Dictionary<string, string>();
        foreach (var item in items)
        {
            hashes.TryAdd(item.FileHash, item.Designation);
            paths.TryAdd(item.FileHash, item.RelativePath);
            if (item.SourceHash is not null)
            {
                hashes.TryAdd(item.SourceHash, item.Designation);
                paths.TryAdd(item.SourceHash, item.RelativePath);
            }
        }

        var rules = await db.DesignationRules.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
        var session = new ImportSession(catalog, keys, hashes, paths, properties.IsAvailable, properties.UnavailableReason)
        {
            Rules = new DesignationRuleSet(rules),
        };
        foreach (var item in items.Where(i => LibraryGeometry.Known(i.Volume, i.Area)))
            session.ExistingGeometry.Add((item.Designation, item.DocumentType, item.Volume!.Value, item.Area!.Value));
        return session;
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
                    .Order(NaturalComparer.Instance);
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

    private async Task<ImportCandidate> AnalyzeFileAsync(string path, ImportSession session, CancellationToken ct)
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

        // Раздел спецификации уже записан в файле (например, другим плагином) — тип берём из него.
        var kindFromFile = props is null ? null : LibraryProperties.KindFromSpecSection(props.Custom);

        var candidate = new ImportCandidate
        {
            SourcePath = path,
            DocumentType = documentType,
            Properties = props,
            PropertiesError = error,
            FileHash = hash,
            FileSize = size,
            ReferenceHashes = referenceHashes,
            KindFromFile = kindFromFile is not null,
        };

        // Обозначение — из «Обозначения», если оно заполнено, иначе из имени файла.
        Prepare(candidate, session, candidate.SourceText(DesignationSource.PartNumber) is not null
            ? DesignationSource.PartNumber
            : DesignationSource.FileName, resetKind: kindFromFile is null);
        if (kindFromFile is { } kind)
            candidate.Kind = kind;
        return candidate;
    }

    /// <summary>
    /// Готовит обозначение из источника: единый вид → правила замены → единый вид. Обновляет
    /// стандарт и, если раздел не менял пользователь, предложенный раздел.
    /// </summary>
    private static void Prepare(ImportCandidate c, ImportSession session, DesignationSource source, bool resetKind)
    {
        var text = c.SourceText(source) ?? "";
        var (replaced, applied) = session.Rules.ApplyReplacements(DesignationNormalizer.ToCanonical(text));
        c.Source = source;
        c.OriginalDesignation = text;
        c.AppliedRules = applied;
        c.Designation = DesignationNormalizer.ToCanonical(replaced);
        Suggest(c, session);
        if (resetKind && !c.KindFromFile)
            c.Kind = c.Standard is not null ? LibraryItemKind.Standard : LibraryItemKind.Purchased;
    }

    /// <summary>Стандарт по обозначению (или ручной) и предлагаемый раздел — если раздел не менял пользователь.</summary>
    private static void Suggest(ImportCandidate c, ImportSession session)
    {
        var categoryUntouched = c.CategoryId == c.SuggestedCategoryId;
        c.Standard = c.ManualStandard ?? StandardParser.Find(c.Designation);
        var suggested = session.Catalog.Suggest(c.Designation, c.Standard);
        if (categoryUntouched)
            c.CategoryId = suggested?.Id;
        c.SuggestedCategoryId = suggested?.Id;
    }

    /// <summary>
    /// Берёт обозначение выбранных строк из другого источника (свойство или имя файла).
    /// Строки, где источник пуст, не меняются. Возвращает число изменённых строк.
    /// </summary>
    public int SetSource(ImportSession session, IEnumerable<ImportCandidate> candidates, DesignationSource source)
    {
        if (source == DesignationSource.Manual)
            throw new ArgumentException("Ручной ввод выбирается правкой обозначения.", nameof(source));
        var changed = 0;
        foreach (var c in candidates.Where(c => c.SourceText(source) is not null))
        {
            Prepare(c, session, source, resetKind: true);
            changed++;
        }
        Validate(session);
        return changed;
    }

    /// <summary>
    /// Перечитывает правила из базы и заново готовит обозначения всех строк, кроме исправленных
    /// вручную (после изменения правил).
    /// </summary>
    public async Task ReloadRulesAsync(ImportSession session, CancellationToken ct = default)
    {
        await using (var db = database.CreateContext())
            session.Rules = new DesignationRuleSet(await db.DesignationRules.AsNoTracking().ToListAsync(ct).ConfigureAwait(false));
        foreach (var c in session.Candidates.Where(c => c.Source != DesignationSource.Manual))
            Prepare(c, session, c.Source, resetKind: true);
        Validate(session);
    }

    /// <summary>
    /// Поиск и замена в обозначениях выбранных строк (обычный текст или регулярное выражение).
    /// Изменённые строки считаются исправленными вручную. Возвращает число изменённых строк.
    /// </summary>
    public int ReplaceInDesignations(ImportSession session, IEnumerable<ImportCandidate> candidates, string find, string replace, bool isRegex)
    {
        if (string.IsNullOrEmpty(find))
            throw new ArgumentException("Не указано, что искать.", nameof(find));
        var regex = isRegex ? new Regex(find, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250)) : null;

        var changed = 0;
        foreach (var c in candidates)
        {
            string replaced;
            try
            {
                replaced = regex is null ? c.Designation.Replace(find, replace, StringComparison.Ordinal) : regex.Replace(c.Designation, replace);
            }
            catch (RegexMatchTimeoutException)
            {
                continue; // выражение «зависло» на этой строке — её не меняем
            }

            var next = DesignationNormalizer.ToCanonical(replaced);
            if (next == c.Designation)
                continue;
            c.Designation = next;
            c.Source = DesignationSource.Manual;
            c.AppliedRules = [];
            changed++;
        }
        Validate(session);
        return changed;
    }

    /// <summary>
    /// Исключает строки, файлы которых уже внесены в библиотеку (то же содержимое): например,
    /// компоненты подсборки, добавленные вместе с ней. Возвращает их источники → файл в библиотеке.
    /// </summary>
    public IReadOnlyDictionary<string, string> ExcludeAlreadyInLibrary(ImportSession session)
    {
        var storage = storageFactory();
        var known = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in session.Candidates.Where(c => c.Include && c.FileHash.Length > 0))
        {
            if (session.ExistingPaths.TryGetValue(c.FileHash, out var relative))
            {
                c.Include = false;
                known[c.SourcePath] = storage.GetFullPath(relative);
            }
        }
        Validate(session);
        return known;
    }

    /// <summary>Сохраняет список подготовки с правками пользователя.</summary>
    public void SaveList(ImportSession session, string path) => ImportList.From(session).Save(path);

    /// <summary>
    /// Открывает сохранённый список: файлы анализируются заново (они могли измениться),
    /// затем восстанавливаются правки. Пропавшие файлы попадают в «Пропущенные».
    /// </summary>
    public async Task<ImportSession> LoadListAsync(string path, IProgress<ImportProgress>? progress = null, CancellationToken ct = default)
    {
        var list = ImportList.Load(path);
        var session = await CreateSessionAsync(ct).ConfigureAwait(false);

        var present = new List<ImportListEntry>();
        foreach (var entry in list.Items)
        {
            if (File.Exists(entry.Path))
                present.Add(entry);
            else
                session.Skipped.Add(new SkippedFile(entry.Path, "Файл из списка не найден"));
        }

        await AddAsync(session, present.Select(e => e.Path), progress, ct).ConfigureAwait(false);

        var byPath = session.Candidates.ToDictionary(c => c.SourcePath, StringComparer.OrdinalIgnoreCase);
        foreach (var entry in present)
        {
            if (!byPath.TryGetValue(Path.GetFullPath(entry.Path), out var c))
                continue; // файл теперь в папке библиотеки и т.п. — причина уже в «Пропущенных»

            c.Include = entry.Include;
            // Ручной стандарт — до подготовки обозначения: от стандарта зависит предлагаемый раздел.
            c.ManualStandard = LibraryRules.NormalizeStandard(entry.ManualStandard);
            if (entry.Source == DesignationSource.Manual)
            {
                c.Designation = DesignationNormalizer.ToCanonical(entry.Designation);
                c.Source = DesignationSource.Manual;
                c.AppliedRules = [];
                Suggest(c, session);
            }
            else if (c.SourceText(entry.Source) is not null && (entry.Source != c.Source || c.ManualStandard is not null))
            {
                Prepare(c, session, entry.Source, resetKind: true);
            }

            if (!c.KindFromFile)
                c.Kind = entry.Kind;
            c.Manufacturer = entry.Manufacturer;
            if (entry.CategoryChosen && entry.CategoryPath is not null && session.Catalog.FindByPath(entry.CategoryPath) is { } category)
                c.CategoryId = category.Id;
        }

        Validate(session);
        return session;
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
            c.ManualStandard = LibraryRules.NormalizeStandard(c.ManualStandard);
            var placement = LibraryRules.Place(session.Catalog, storage, c.Designation, c.Kind, c.Manufacturer,
                c.CategoryId, Path.GetExtension(c.SourcePath), c.ManualStandard);
            c.Designation = placement.Designation;
            c.Standard = placement.Standard;
            c.Issues.Clear();
            c.TargetRelativePath = placement.RelativePath;
            c.LibraryMatch = null;
            placements[c] = placement;
        }

        var byKey = included.Where(c => c.Designation.Length > 0)
            .GroupBy(c => DesignationNormalizer.ToKey(c.Designation)).ToDictionary(g => g.Key, g => g.ToList());
        var byHash = included.Where(c => c.FileHash.Length > 0)
            .GroupBy(c => c.FileHash).ToDictionary(g => g.Key, g => g.ToList());
        var byTarget = included.Where(c => c.TargetRelativePath is not null)
            .GroupBy(c => c.TargetRelativePath!, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        // Проверяются и исключённые строки: видно, есть ли компонент в библиотеке и что с ним не так.
        // Совпадения внутри списка — только между включёнными.
        foreach (var c in session.Candidates)
        {
            var inList = c.Include;
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
                    Info("Файл создан из Content Center — при внесении метка Content Center будет снята, компонент станет самостоятельным.");
            }

            if (c.KindFromFile)
                Info($"Тип «{(c.Kind == LibraryItemKind.Standard ? "стандартное" : "покупное")}» взят из свойства {LibraryProperties.SpecSection} в файле.");
            if (c.AppliedRules.Count > 0)
                Info("Применены правила: " + string.Join("; ", c.AppliedRules) + ".");

            if (DesignationNormalizer.ToKey(Path.GetFileNameWithoutExtension(c.SourcePath)) != key)
                Info("Имя файла не совпадает с обозначением — в библиотеке файл будет назван по обозначению.");

            // Дубликаты
            if (c.Properties is { } geometry && LibraryGeometry.Known(geometry.Volume, geometry.Area))
            {
                var sameShape = session.ExistingGeometry
                    .Where(g => g.Type == c.DocumentType && DesignationNormalizer.ToKey(g.Designation) != key
                                && LibraryGeometry.Same(g.Volume, g.Area, geometry.Volume, geometry.Area))
                    .Select(g => $"«{g.Designation}» из библиотеки")
                    .Concat(included.Where(o => inList && o != c && o.DocumentType == c.DocumentType && o.Properties is { } p2
                                                && LibraryGeometry.Same(p2.Volume, p2.Area, geometry.Volume, geometry.Area))
                        .Select(o => $"«{o.Designation}» ({o.FileName}) в списке"))
                    .ToList();
                var libraryShape = session.ExistingGeometry.FirstOrDefault(g => g.Type == c.DocumentType
                    && DesignationNormalizer.ToKey(g.Designation) != key && LibraryGeometry.Same(g.Volume, g.Area, geometry.Volume, geometry.Area));
                if (libraryShape.Designation is not null)
                    c.LibraryMatch = $"Похожий (та же геометрия): «{libraryShape.Designation}»";
                if (sameShape.Count > 0)
                    Warn("Такая же геометрия (объём и площадь поверхности), как у " + string.Join(", ", sameShape.Take(5))
                         + (sameShape.Count > 5 ? $" и ещё {sameShape.Count - 5}" : "")
                         + ". Проверьте, не дубликат ли это. Если отличается только обозначение (класс прочности, покрытие) — всё в порядке.");
            }
            string? sameFile = null;
            if (c.FileHash.Length > 0 && session.ExistingHashes.TryGetValue(c.FileHash, out var fileOwner))
            {
                sameFile = fileOwner;
                c.LibraryMatch = $"Этот файл уже внесён: «{sameFile}»";
            }
            if (session.ExistingKeys.TryGetValue(key, out var existing))
            {
                Error($"В библиотеке уже есть «{existing}».");
                c.LibraryMatch = sameFile is not null ? $"Есть: «{existing}» (тот же файл)" : $"Есть: «{existing}»";
            }
            if (inList && byKey.TryGetValue(key, out var sameKey) && sameKey.Count > 1)
                Error("Обозначение повторяется в списке: " + string.Join(", ", sameKey.Where(x => x != c).Select(x => x.FileName)) + ".");
            if (sameFile is not null)
                Error($"Этот файл уже добавлен в библиотеку как «{sameFile}».");
            if (inList && c.FileHash.Length > 0 && byHash.TryGetValue(c.FileHash, out var sameHash) && sameHash.Count > 1)
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
                if (inList && byTarget.TryGetValue(target, out var sameTarget) && sameTarget > 1)
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
        await using var lease = await LibraryWriteLease.EnterAsync(database, storage.Root,
            ex => logger.LogWarning(ex, "Импорт: очистка ресурсов"), ct).ConfigureAwait(false);
        var user = Environment.UserName;

        var eligible = session.Candidates.Where(c => c.CanImport)
            .ToDictionary(c => PathKey(c.SourcePath), StringComparer.OrdinalIgnoreCase);
        var degrees = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var parents = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, candidate) in eligible)
        {
            var dependencies = candidate.DocumentType == LibraryDocumentType.Assembly
                ? (candidate.Properties?.References ?? []).Select(PathKey).Where(eligible.ContainsKey)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase)
                : [];
            degrees[path] = dependencies.Count;
            foreach (var dependency in dependencies)
            {
                if (!parents.TryGetValue(dependency, out var list)) parents[dependency] = list = [];
                list.Add(path);
            }
        }
        var ready = new Queue<string>(eligible.Keys.Where(p => degrees[p] == 0));
        var queue = new List<ImportCandidate>();
        // Kahn ordering in O(files + references), including shared nested assemblies.
        while (ready.TryDequeue(out var path))
        {
            queue.Add(eligible[path]);
            foreach (var parent in parents.GetValueOrDefault(path) ?? [])
                if (--degrees[parent] == 0) ready.Enqueue(parent);
        }
        var pending = eligible.Where(p => degrees[p.Key] > 0).Select(p => p.Value);
        var committed = new Dictionary<string, (Guid Id, string FullPath)>(StringComparer.OrdinalIgnoreCase);
        var failed = pending.Select(c => (c, "Цикл зависимостей сборок или зависимость от цикла. Импорт компонента пропущен.")).ToList();

        for (var i = 0; i < queue.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var c = queue[i];
            progress?.Report(new ImportProgress(i, queue.Count, c.FileName));

            string? copied = null;
            var id = Guid.NewGuid();
            var registered = false;
            var commit = new LibraryMutationCommit(lease, LibraryMutationCommit.CreateContext);
            try
            {
                var category = session.Catalog.Get(c.CategoryId)!;
                copied = storage.CopyIn(c.SourcePath, c.TargetRelativePath!);
                lease.SetCopies([new RecoveryCopy(copied, copied)]);

                await using var db = database.CreateContext();
                var componentIds = c.DocumentType == LibraryDocumentType.Assembly
                    ? await RelinkAssemblyAsync(db, storage, c, copied, committed, ct).ConfigureAwait(false)
                    : [];

                await properties.WriteAsync(copied, LibraryProperties.For(c.Designation, c.Kind, c.Standard, c.Manufacturer, category.Path, id), ct)
                    .ConfigureAwait(false);
                if (c.DocumentType == LibraryDocumentType.Assembly)
                    await VerifyAssemblyAsync(db, storage, copied, componentIds, ct).ConfigureAwait(false);
                LibraryStorage.MakeReadOnly(copied);
                var (hash, size) = await LibraryStorage.HashAsync(copied, ct).ConfigureAwait(false);
                var now = DateTime.UtcNow;
                var key = DesignationNormalizer.ToKey(c.Designation);

                db.LibraryItems.Add(new LibraryItem
                {
                    Id = id,
                    CategoryId = category.Id,
                    Kind = c.Kind,
                    DocumentType = c.DocumentType,
                    Designation = c.Designation,
                    DesignationKey = key,
                    Standard = c.Standard,
                    StandardIsManual = c.ManualStandard is not null && c.Kind == LibraryItemKind.Standard,
                    Manufacturer = string.IsNullOrWhiteSpace(c.Manufacturer) ? null : c.Manufacturer.Trim(),
                    RelativePath = c.TargetRelativePath!,
                    FileHash = hash,
                    FileSize = size,
                    SourceHash = c.FileHash,
                    Volume = c.Properties?.Volume,
                    Area = c.Properties?.Area,
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

                await commit.SaveAsync(db, ct).ConfigureAwait(false);
                registered = true;

                committed[PathKey(c.SourcePath)] = (id, copied);
                session.Remember(key, c.Designation, c.TargetRelativePath!, c.FileHash, hash);
                if (c.Properties is { } shape && LibraryGeometry.Known(shape.Volume, shape.Area))
                    session.ExistingGeometry.Add((c.Designation, c.DocumentType, shape.Volume!.Value, shape.Area!.Value));
                ct.ThrowIfCancellationRequested();
                logger.LogInformation("В библиотеку добавлен {Designation} ({Path})", c.Designation, c.TargetRelativePath);
            }
            catch (Exception ex)
            {
                if (registered)
                {
                    committed[PathKey(c.SourcePath)] = (id, copied!);
                    if (ex is OperationCanceledException) throw;
                    logger.LogWarning(ex, "Компонент {Path} уже сохранён; его файл не удаляется", copied);
                    continue;
                }
                if (!await commit.CanRollbackAsync().ConfigureAwait(false))
                    throw commit.Preserve(ex, copied is null ? [] : [new RecoveryCopy(copied, copied)]);
                var cleanup = copied is not null ? TryDelete(copied) : null;
                var detail = cleanup is null ? null : $"Не удалось очистить копию {copied}: {cleanup.Message}";
                if (ex is OperationCanceledException cancelled)
                {
                    if (detail is null) throw;
                    throw new OperationCanceledException($"Импорт отменён. {detail}",
                        cleanup is null ? ex : new AggregateException(ex, cleanup), cancelled.CancellationToken);
                }
                logger.LogError(ex, "Не удалось добавить {Path}", c.SourcePath);
                var message = ex is DbUpdateException { InnerException: { } inner } ? inner.Message : ex.Message;
                failed.Add((c, detail is null ? message : message + " " + detail));
            }
        }

        progress?.Report(new ImportProgress(queue.Count, queue.Count, null));
        return new ImportResult(committed.Count, failed)
        {
            Committed = session.Candidates.Where(c => committed.ContainsKey(PathKey(c.SourcePath)))
                .ToDictionary(c => c.SourcePath, c => committed[PathKey(c.SourcePath)].FullPath, StringComparer.OrdinalIgnoreCase),
        };
    }

    /// <summary>Переводит ссылки скопированной сборки на файлы библиотеки; возвращает Id компонентов.</summary>
    private async Task<List<Guid>> RelinkAssemblyAsync(PdmDbContext db, LibraryStorage storage, ImportCandidate c, string copied,
        Dictionary<string, (Guid Id, string FullPath)> committed, CancellationToken ct)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var ids = new List<Guid>();
        foreach (var reference in c.Properties?.References ?? [])
        {
            if (committed.TryGetValue(PathKey(reference), out var done))
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

    private static string PathKey(string path) => Path.GetFullPath(path).Replace('\\', '/');

    private async Task VerifyAssemblyAsync(PdmDbContext db, LibraryStorage storage, string copied,
        List<Guid> componentIds, CancellationToken ct)
    {
        var expected = (await db.LibraryItems.AsNoTracking().Where(i => componentIds.Contains(i.Id))
            .Select(i => i.RelativePath).ToListAsync(ct).ConfigureAwait(false))
            .Select(p => PathKey(storage.GetFullPath(p))).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var actual = (await properties.ReadWithoutGeometryAsync(copied, ct).ConfigureAwait(false))
            .References.Select(PathKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!expected.SetEquals(actual) || expected.Any(p => !File.Exists(p)))
            throw new InvalidOperationException("Не подтверждена замена ссылок сборки. "
                + "Отсутствуют: " + string.Join("; ", expected.Except(actual, StringComparer.OrdinalIgnoreCase))
                + ". Неожиданные ссылки: " + string.Join("; ", actual.Except(expected, StringComparer.OrdinalIgnoreCase)));
    }

    private Exception? TryDelete(string path)
    {
        try
        {
            LibraryStorage.Delete(path);
            if (File.Exists(path) || Directory.Exists(path)) throw new IOException("Путь остался после удаления.");
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Не удалось удалить {Path} после ошибки", path);
            return ex;
        }
    }
}
