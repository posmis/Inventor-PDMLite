using System.Data;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PdmLite.Data;
using PdmLite.Data.Library;
using PdmLite.Library.Storage;

namespace PdmLite.Library.Backup;

/// <summary>Зачем сделана копия (часть имени файла).</summary>
public enum BackupReason
{
    Manual,
    Scheduled,
    BeforeMaintenance,
    BeforeMerge,
    BeforeRestore,
}

/// <summary>Описание копии — <c>manifest.json</c> в архиве.</summary>
public sealed record BackupManifest
{
    public const int CurrentFormat = 1;

    public int Format { get; init; } = CurrentFormat;
    public DateTime CreatedAt { get; init; }
    public string User { get; init; } = "";
    public string Machine { get; init; } = "";
    public BackupReason Reason { get; init; }
    public int Items { get; init; }
    public int Files { get; init; }
    public long FilesSize { get; init; }

    /// <summary>Папка библиотеки на момент копии (для справки: восстанавливается в текущую).</summary>
    public string LibraryRoot { get; init; } = "";

    /// <summary>Контрольные суммы (SHA-256) файлов архива: путь в библиотеке → сумма. Для проверки целостности копии.</summary>
    public Dictionary<string, string> Checksums { get; init; } = [];
}

/// <summary>Копия в папке резервных копий.</summary>
public sealed record BackupInfo(string Path, BackupManifest Manifest, long Size)
{
    public string FileName => System.IO.Path.GetFileName(Path);
    public bool IsAutomatic => Manifest.Reason != BackupReason.Manual;
}

/// <summary>Что изменится при восстановлении (по сравнению с текущей библиотекой).</summary>
/// <param name="Added">Компоненты, которых сейчас нет — появятся.</param>
/// <param name="Removed">Компоненты, которых нет в копии — пропадут.</param>
/// <param name="Changed">Компоненты, у которых изменились обозначение, путь или файл — вернутся к состоянию копии.</param>
/// <param name="MissingFiles">Компоненты копии без файла в архиве или с изменённым файлом (контрольная сумма не совпала) — копия повреждена.</param>
public sealed record RestorePlan(BackupInfo Backup, IReadOnlyList<string> Added, IReadOnlyList<string> Removed,
    IReadOnlyList<string> Changed, int ItemsNow, int FilesNow, IReadOnlyList<string> MissingFiles)
{
    public bool IsDamaged => MissingFiles.Count > 0;
}

/// <summary>Где хранить копии и сколько.</summary>
public sealed record BackupOptions(string Folder, int Keep, int IntervalDays);

/// <summary>
/// Резервные копии библиотеки: база (все таблицы схемы library) и файлы папки библиотеки — в одном
/// ZIP-архиве. Восстановление заменяет базу и папку целиком; перед ним делается копия текущего состояния,
/// при ошибке всё возвращается как было.
/// </summary>
public sealed class LibraryBackupService(
    IPdmDatabase database,
    Func<LibraryStorage> storageFactory,
    Func<BackupOptions> options,
    ILogger<LibraryBackupService> logger)
{
    private const string ManifestEntry = "manifest.json";
    private const string DataEntry = "data/library.json";
    private const string FilesPrefix = "files/";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string ReasonText(BackupReason reason) => reason switch
    {
        BackupReason.Manual => "вручную",
        BackupReason.Scheduled => "по расписанию",
        BackupReason.BeforeMaintenance => "перед обслуживанием",
        BackupReason.BeforeMerge => "перед объединением",
        BackupReason.BeforeRestore => "перед восстановлением",
        _ => reason.ToString(),
    };

    // ---------- Создание ----------

    /// <summary>Делает копию: база и все файлы библиотеки. Затем удаляет старые автоматические копии сверх лимита.</summary>
    public async Task<BackupInfo> CreateAsync(BackupReason reason, IProgress<(int Done, int Total, string? Current)>? progress = null,
        CancellationToken ct = default)
    {
        var opts = options();
        Directory.CreateDirectory(opts.Folder);
        var storage = storageFactory();

        var snapshot = await LoadSnapshotAsync(ct).ConfigureAwait(false);
        var files = Directory.Exists(storage.Root)
            ? Directory.EnumerateFiles(storage.Root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToList()
            : [];

        var now = DateTime.Now;
        var path = UniquePath(opts.Folder, $"PdmLite-Library_{now:yyyy-MM-dd_HH-mm-ss}_{ReasonText(reason).Replace(' ', '-')}");
        var temp = path + ".part";
        long size = 0;
        var checksums = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            using (var zip = ZipFile.Open(temp, ZipArchiveMode.Create))
            {
                for (var i = 0; i < files.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var relative = Path.GetRelativePath(storage.Root, files[i]).Replace(Path.DirectorySeparatorChar, '/');
                    progress?.Report((i, files.Count, relative));
                    var entry = zip.CreateEntry(FilesPrefix + relative, CompressionLevel.Fastest);
                    entry.LastWriteTime = File.GetLastWriteTime(files[i]);
                    // Файл может быть открыт в Inventor — читаем, не мешая ему.
                    await using var source = new FileStream(files[i], FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    await using var target = entry.Open();
                    checksums[relative] = await CopyWithHashAsync(source, target, ct).ConfigureAwait(false);
                    size += source.Length;
                }

                await WriteEntryAsync(zip, DataEntry, snapshot, ct).ConfigureAwait(false);
                var manifest = new BackupManifest
                {
                    CreatedAt = DateTime.UtcNow,
                    User = Environment.UserName,
                    Machine = Environment.MachineName,
                    Reason = reason,
                    Items = snapshot.Items.Count,
                    Files = files.Count,
                    FilesSize = size,
                    LibraryRoot = storage.Root,
                    Checksums = checksums,
                };
                await WriteEntryAsync(zip, ManifestEntry, manifest, ct).ConfigureAwait(false);
            }

            File.Move(temp, path);
        }
        catch
        {
            TryDeleteFile(temp);
            throw;
        }

        progress?.Report((files.Count, files.Count, null));
        logger.LogInformation("Резервная копия библиотеки: {Path} ({Items} компонентов, {Files} файлов)", path, snapshot.Items.Count, files.Count);
        Prune();
        return Read(path) ?? throw new InvalidDataException("Созданная копия не читается.");
    }

    /// <summary>Пора ли делать копию по расписанию (последняя копия любого вида старше интервала).</summary>
    public bool IsScheduledDue()
    {
        var opts = options();
        if (opts.IntervalDays <= 0)
            return false;
        var last = List().FirstOrDefault();
        return last is null || DateTime.UtcNow - last.Manifest.CreatedAt >= TimeSpan.FromDays(opts.IntervalDays);
    }

    // ---------- Список ----------

    /// <summary>Копии в папке, новые первыми. Нечитаемые архивы пропускаются.</summary>
    public IReadOnlyList<BackupInfo> List()
    {
        var folder = options().Folder;
        if (!Directory.Exists(folder))
            return [];
        return Directory.EnumerateFiles(folder, "*.zip")
            .Select(Read)
            .OfType<BackupInfo>()
            .OrderByDescending(b => b.Manifest.CreatedAt)
            .ToList();
    }

    /// <summary>Удаляет автоматические копии сверх лимита (ручные не трогает). Возвращает число удалённых.</summary>
    public int Prune()
    {
        var keep = Math.Max(1, options().Keep);
        var extra = List().Where(b => b.IsAutomatic).Skip(keep).ToList();
        foreach (var backup in extra)
            TryDeleteFile(backup.Path);
        return extra.Count;
    }

    public void Delete(BackupInfo backup) => File.Delete(backup.Path);

    /// <summary>Описание копии или <c>null</c>, если файл — не копия PdmLite.</summary>
    public BackupInfo? Read(string path)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            var entry = zip.GetEntry(ManifestEntry);
            if (entry is null)
                return null;
            using var stream = entry.Open();
            var manifest = JsonSerializer.Deserialize<BackupManifest>(stream, Json);
            return manifest is null ? null : new BackupInfo(path, manifest, new FileInfo(path).Length);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Не читается резервная копия {Path}", path);
            return null;
        }
    }

    // ---------- Восстановление ----------

    /// <summary>Только чтение: что изменится при восстановлении копии.</summary>
    public async Task<RestorePlan> PlanRestoreAsync(string path, CancellationToken ct = default)
    {
        var info = Read(path) ?? throw new InvalidDataException("Файл не является резервной копией PdmLite.");
        if (info.Manifest.Format > BackupManifest.CurrentFormat)
            throw new InvalidDataException("Копия сделана более новой версией PdmLite — обновите программу.");

        var (snapshot, _) = ReadArchive(path);
        await database.MigrateAsync(ct).ConfigureAwait(false);
        await using var db = database.CreateContext();
        var now = await db.LibraryItems.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
        var nowById = now.ToDictionary(i => i.Id);
        var backupById = snapshot.Items.ToDictionary(i => i.Id);

        var added = snapshot.Items.Where(i => !nowById.ContainsKey(i.Id)).Select(i => i.Designation).ToList();
        var removed = now.Where(i => !backupById.ContainsKey(i.Id)).Select(i => i.Designation).ToList();
        var changed = snapshot.Items.Where(i => nowById.TryGetValue(i.Id, out var n)
                && (n.Designation != i.Designation || n.RelativePath != i.RelativePath || n.FileHash != i.FileHash))
            .Select(i => nowById[i.Id].Designation == i.Designation ? i.Designation : $"{nowById[i.Id].Designation} → {i.Designation}")
            .ToList();
        var missing = await VerifyFilesAsync(path, snapshot, info.Manifest, ct).ConfigureAwait(false);

        var storage = storageFactory();
        var filesNow = Directory.Exists(storage.Root) ? Directory.EnumerateFiles(storage.Root, "*", SearchOption.AllDirectories).Count() : 0;
        return new RestorePlan(info, added, removed, changed, now.Count, filesNow, missing);
    }

    /// <summary>
    /// Восстанавливает копию: сначала копия текущего состояния, затем файлы распаковываются рядом,
    /// папка библиотеки подменяется, база заменяется в одной транзакции. При ошибке — откат.
    /// </summary>
    /// <returns>Копия, сделанная перед восстановлением.</returns>
    public async Task<BackupInfo> RestoreAsync(string path, IProgress<(int Done, int Total, string? Current)>? progress = null,
        CancellationToken ct = default)
    {
        var plan = await PlanRestoreAsync(path, ct).ConfigureAwait(false);
        if (plan.IsDamaged)
            throw new InvalidDataException("Копия повреждена: " + string.Join("; ", plan.MissingFiles.Take(5)) + ".");

        var before = await CreateAsync(BackupReason.BeforeRestore, progress, ct).ConfigureAwait(false);
        var (snapshot, _) = ReadArchive(path);

        var storage = storageFactory();
        var root = storage.Root;
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var staging = root + ".restore-" + stamp;
        var previous = root + ".before-restore-" + stamp;

        // 1. Файлы — во временную папку рядом с библиотекой (тот же диск: подмена — переименованием).
        try
        {
            Extract(path, staging, snapshot, progress, ct);
        }
        catch
        {
            TryDeleteFolder(staging);
            throw;
        }

        // 2. Подмена папки. Занята (файлы открыты в Inventor) — ничего не меняем.
        var hadRoot = Directory.Exists(root);
        try
        {
            if (hadRoot)
                Directory.Move(root, previous);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDeleteFolder(staging);
            throw new InvalidOperationException(
                "Папка библиотеки занята — закройте в Inventor документы из библиотеки (или Inventor) и повторите. " + ex.Message, ex);
        }

        var staged = false;
        try
        {
            Directory.Move(staging, root);
            staged = true;
            await ReplaceDatabaseAsync(snapshot, Path.GetFileName(path), ct).ConfigureAwait(false);
        }
        catch
        {
            // Откат: восстановленная папка убирается, прежняя — на место; база не изменилась (транзакция).
            TryDeleteFolder(staged ? root : staging);
            if (hadRoot && Directory.Exists(previous) && !Directory.Exists(root))
                Directory.Move(previous, root);
            throw;
        }

        if (hadRoot)
            TryDeleteFolder(previous);
        logger.LogInformation("Библиотека восстановлена из {Path}; копия перед восстановлением: {Before}", path, before.Path);
        return before;
    }

    // ---------- Внутреннее ----------

    /// <summary>Все таблицы библиотеки на один момент (одна транзакция).</summary>
    private async Task<LibrarySnapshot> LoadSnapshotAsync(CancellationToken ct)
    {
        await database.MigrateAsync(ct).ConfigureAwait(false);
        await using var db = database.CreateContext();
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct).ConfigureAwait(false);
        var snapshot = new LibrarySnapshot
        {
            Categories = await db.LibraryCategories.AsNoTracking().OrderBy(c => c.Id).ToListAsync(ct).ConfigureAwait(false),
            CategoryRules = await db.LibraryCategoryRules.AsNoTracking().OrderBy(r => r.Id).ToListAsync(ct).ConfigureAwait(false),
            Items = await db.LibraryItems.AsNoTracking().OrderBy(i => i.Designation).ToListAsync(ct).ConfigureAwait(false),
            Versions = await db.Set<LibraryItemVersion>().AsNoTracking().OrderBy(v => v.Id).ToListAsync(ct).ConfigureAwait(false),
            Properties = await db.Set<LibraryItemProperty>().AsNoTracking().ToListAsync(ct).ConfigureAwait(false),
            References = await db.LibraryItemReferences.AsNoTracking().ToListAsync(ct).ConfigureAwait(false),
            DesignationRules = await db.DesignationRules.AsNoTracking().OrderBy(r => r.Id).ToListAsync(ct).ConfigureAwait(false),
            Log = await db.LibraryLog.AsNoTracking().OrderBy(l => l.Id).ToListAsync(ct).ConfigureAwait(false),
        };
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return snapshot;
    }

    /// <summary>Заменяет все таблицы библиотеки данными копии; счётчики Id — после наибольших.</summary>
    private async Task ReplaceDatabaseAsync(LibrarySnapshot snapshot, string backupName, CancellationToken ct)
    {
        await using var db = database.CreateContext();
        var tables = new[]
        {
            typeof(LibraryItemReference), typeof(LibraryItemProperty), typeof(LibraryItemVersion), typeof(LibraryItem),
            typeof(LibraryCategoryRule), typeof(LibraryCategory), typeof(DesignationRule), typeof(LibraryLogEntry),
        }.Select(t => Table(db, t)).ToList();

        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        // Имена таблиц — из модели EF (не ввод пользователя) и экранированы как идентификаторы:
        // идентификаторы в SQL нельзя передать параметрами.
        var truncate = "TRUNCATE " + string.Join(", ", tables) + " RESTART IDENTITY";
        await db.Database.ExecuteSqlRawAsync(truncate, ct).ConfigureAwait(false);

        // Сначала разделы-родители, потом вложенные.
        db.LibraryCategories.AddRange(OrderByDepth(snapshot.Categories));
        db.LibraryCategoryRules.AddRange(snapshot.CategoryRules);
        db.LibraryItems.AddRange(snapshot.Items);
        db.Set<LibraryItemVersion>().AddRange(snapshot.Versions);
        db.Set<LibraryItemProperty>().AddRange(snapshot.Properties);
        db.LibraryItemReferences.AddRange(snapshot.References);
        db.DesignationRules.AddRange(snapshot.DesignationRules);
        db.LibraryLog.AddRange(snapshot.Log);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        foreach (var type in new[] { typeof(LibraryCategory), typeof(LibraryCategoryRule), typeof(LibraryItemVersion), typeof(DesignationRule), typeof(LibraryLogEntry) })
        {
            // Имя таблицы для pg_get_serial_sequence — параметром ({0}); в FROM — экранированный идентификатор.
            var table = Table(db, type);
            var reset = "SELECT setval(pg_get_serial_sequence({0}, 'id'), COALESCE((SELECT MAX(id) FROM " + table + "), 0) + 1, false)";
            await db.Database.ExecuteSqlRawAsync(reset, [table], ct).ConfigureAwait(false);
        }

        db.LibraryLog.Add(new LibraryLogEntry
        {
            At = DateTime.UtcNow,
            User = Environment.UserName,
            Action = "restore",
            Details = JsonSerializer.Serialize(new { backup = backupName, items = snapshot.Items.Count }),
        });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Таблица сущности как идентификатор SQL: «"схема"."таблица"».</summary>
    private static string Table(PdmDbContext db, Type type)
    {
        var entity = db.Model.FindEntityType(type) ?? throw new InvalidOperationException($"Нет таблицы для {type.Name}.");
        return QuoteIdentifier(entity.GetSchema() ?? "public") + "." + QuoteIdentifier(entity.GetTableName()!);
    }

    /// <summary>Идентификатор PostgreSQL в кавычках; кавычки внутри удваиваются.</summary>
    internal static string QuoteIdentifier(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";

    private static IEnumerable<LibraryCategory> OrderByDepth(IReadOnlyList<LibraryCategory> categories)
    {
        var byId = categories.ToDictionary(c => c.Id);
        int Depth(LibraryCategory c) => c.ParentId is { } p && byId.TryGetValue(p, out var parent) ? Depth(parent) + 1 : 0;
        return categories.OrderBy(Depth).ThenBy(c => c.Id);
    }

    /// <summary>
    /// Целостность копии: у каждого компонента есть файл в архиве, и суммы файлов совпадают с записанными
    /// при создании копии. Возвращает описания проблем.
    /// </summary>
    private static async Task<List<string>> VerifyFilesAsync(string path, LibrarySnapshot snapshot, BackupManifest manifest, CancellationToken ct)
    {
        var problems = new List<string>();
        using var zip = ZipFile.OpenRead(path);
        foreach (var item in snapshot.Items.Where(i => zip.GetEntry(FilesPrefix + i.RelativePath) is null))
            problems.Add($"{item.Designation} — нет файла");

        foreach (var (relative, expected) in manifest.Checksums)
        {
            ct.ThrowIfCancellationRequested();
            var entry = zip.GetEntry(FilesPrefix + relative);
            if (entry is null)
            {
                problems.Add($"{relative} — нет файла");
                continue;
            }
            await using var stream = entry.Open();
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false));
            if (!string.Equals(hash, expected, StringComparison.OrdinalIgnoreCase))
                problems.Add($"{relative} — файл повреждён (контрольная сумма не совпадает)");
        }
        return problems;
    }

    /// <summary>Копирует поток и считает SHA-256 скопированного.</summary>
    private static async Task<string> CopyWithHashAsync(Stream source, Stream target, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        int read;
        while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            hash.AppendData(buffer, 0, read);
            await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static (LibrarySnapshot Snapshot, HashSet<string> Entries) ReadArchive(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var data = zip.GetEntry(DataEntry) ?? throw new InvalidDataException("В копии нет данных базы.");
        using var stream = data.Open();
        var snapshot = JsonSerializer.Deserialize<LibrarySnapshot>(stream, Json) ?? throw new InvalidDataException("Данные базы в копии пусты.");
        return (snapshot, zip.Entries.Select(e => e.FullName).ToHashSet(StringComparer.Ordinal));
    }

    private static void Extract(string path, string folder, LibrarySnapshot snapshot, IProgress<(int, int, string?)>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(folder);
        var fullFolder = Path.GetFullPath(folder) + Path.DirectorySeparatorChar;
        var componentFiles = snapshot.Items.Select(i => i.RelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        using var zip = ZipFile.OpenRead(path);
        var files = zip.Entries.Where(e => e.FullName.StartsWith(FilesPrefix, StringComparison.Ordinal) && e.Name.Length > 0).ToList();
        for (var i = 0; i < files.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var relative = files[i].FullName[FilesPrefix.Length..];
            progress?.Report((i, files.Count, relative));
            var target = Path.GetFullPath(Path.Combine(folder, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!target.StartsWith(fullFolder, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Недопустимый путь в копии: {relative}."); // защита от «../» в архиве
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            files[i].ExtractToFile(target, overwrite: true);
            if (componentFiles.Contains(relative))
                LibraryStorage.MakeReadOnly(target);
        }
    }

    private static async Task WriteEntryAsync<T>(ZipArchive zip, string name, T value, CancellationToken ct)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        await using var stream = entry.Open();
        await JsonSerializer.SerializeAsync(stream, value, Json, ct).ConfigureAwait(false);
    }

    private static string UniquePath(string folder, string name)
    {
        var path = Path.Combine(folder, name + ".zip");
        for (var i = 2; File.Exists(path); i++)
            path = Path.Combine(folder, $"{name}_{i}.zip");
        return path;
    }

    private void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Не удалось удалить {Path}", path);
        }
    }

    private void TryDeleteFolder(string folder)
    {
        try
        {
            if (!Directory.Exists(folder))
                return;
            foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Не удалось удалить папку {Folder}", folder);
        }
    }
}

/// <summary>Все таблицы библиотеки — содержимое <c>data/library.json</c>.</summary>
public sealed class LibrarySnapshot
{
    public List<LibraryCategory> Categories { get; init; } = [];
    public List<LibraryCategoryRule> CategoryRules { get; init; } = [];
    public List<LibraryItem> Items { get; init; } = [];
    public List<LibraryItemVersion> Versions { get; init; } = [];
    public List<LibraryItemProperty> Properties { get; init; } = [];
    public List<LibraryItemReference> References { get; init; } = [];
    public List<DesignationRule> DesignationRules { get; init; } = [];
    public List<LibraryLogEntry> Log { get; init; } = [];
}
