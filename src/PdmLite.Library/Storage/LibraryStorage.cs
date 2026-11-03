using PdmLite.Core.IO;
using System.Security.Cryptography;
using System.Text;

namespace PdmLite.Library.Storage;

/// <summary>
/// Файлы библиотеки. Раскладка: <c>Раздел\Группа\{Стандарт | Производитель}\{Обозначение}.ipt</c>.
/// Файлы помечаются «только чтение»: менять их можно только через PdmLite.
/// </summary>
public sealed class LibraryStorage(string rootFolder)
{
    /// <summary>Ограничение полного пути (MAX_PATH Windows с запасом).</summary>
    public const int MaxPathLength = 240;

    public const string NoManufacturerFolder = "Без производителя";
    public const string VersionsFolder = "_versions";

    private static readonly char[] Forbidden = ['\\', '/', ':', '*', '?', '"', '<', '>', '|'];

    public string Root { get; } = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootFolder));

    public string GetFullPath(string relativePath) =>
        ContainedPath.Resolve(Root, relativePath);

    public bool Contains(string fullPath) =>
        ContainedPath.Contains(Root, fullPath);

    /// <summary>Относительный путь файла компонента (разделитель — «/»).</summary>
    public static string BuildRelativePath(IEnumerable<string> categoryPath, string? subfolder, string designation, string extension)
    {
        if (string.IsNullOrWhiteSpace(extension) || !extension.StartsWith('.') || extension.Length < 2
            || extension.Skip(1).Any(c => !char.IsLetterOrDigit(c)))
            throw new ArgumentException("Недопустимое расширение файла.", nameof(extension));
        var parts = categoryPath.Select(SafeName).ToList();
        if (!string.IsNullOrWhiteSpace(subfolder))
            parts.Add(SafeName(subfolder));
        parts.Add(SafeName(designation) + extension.ToLowerInvariant());
        return string.Join('/', parts);
    }

    /// <summary>Имена устройств Windows: файл или папку с таким именем (и любым расширением) создать нельзя.</summary>
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM¹", "COM²", "COM³", "LPT¹", "LPT²", "LPT³",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// Имя файла или папки: запрещённые в Windows символы → «-», без точек и пробелов в конце. Пустое после
    /// очистки («...», «  ») → «_» (иначе в пути «//», а «..» — выход из папки); имя устройства Windows
    /// («CON», «NUL»…) → с «_» в конце.
    /// </summary>
    public static string SafeName(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var c in name.Trim())
            sb.Append(Array.IndexOf(Forbidden, c) >= 0 || char.IsControl(c) ? '-' : c);
        var result = sb.ToString().TrimEnd('.', ' ');
        if (result.Length == 0)
            return "_";
        var stem = result.Split('.')[0].TrimEnd(' ');
        return Reserved.Contains(stem) ? stem + "_" + result[stem.Length..] : result;
    }

    /// <summary>Копирует файл в библиотеку. Существующий файл не перезаписывается.</summary>
    public string CopyIn(string sourcePath, string relativePath)
    {
        var target = GetFullPath(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(sourcePath, target, overwrite: false);
        // Копия наследует атрибут «только чтение» исходника — снимаем, чтобы записать свойства.
        File.SetAttributes(target, File.GetAttributes(target) & ~FileAttributes.ReadOnly);
        return target;
    }

    public static void MakeReadOnly(string fullPath) =>
        File.SetAttributes(fullPath, File.GetAttributes(fullPath) | FileAttributes.ReadOnly);

    public static void ClearReadOnly(string fullPath) =>
        File.SetAttributes(fullPath, File.GetAttributes(fullPath) & ~FileAttributes.ReadOnly);

    /// <summary>Удаляет файл, включая «только чтение».</summary>
    public static void Delete(string fullPath)
    {
        if (!File.Exists(fullPath))
            return;
        File.SetAttributes(fullPath, FileAttributes.Normal);
        File.Delete(fullPath);
    }

    /// <summary>
    /// Временная копия файла на случай отката. «Только чтение» с копии снимается —
    /// иначе Windows не даст её удалить.
    /// </summary>
    public static string Backup(string fullPath)
    {
        var backup = Path.Combine(Path.GetTempPath(), $"pdmlite-{Guid.NewGuid():N}{Path.GetExtension(fullPath)}");
        File.Copy(fullPath, backup);
        ClearReadOnly(backup);
        return backup;
    }

    /// <summary>Возвращает файл из резервной копии (поверх текущего, если он есть).</summary>
    public static void Restore(string backup, string fullPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        if (File.Exists(fullPath))
            ClearReadOnly(fullPath);
        File.Copy(backup, fullPath, overwrite: true);
        MakeReadOnly(fullPath);
    }

    /// <summary>Убирает опустевшие папки, поднимаясь до корня библиотеки (сам корень остаётся).</summary>
    public void RemoveEmptyFolders(string folder)
    {
        var dir = new DirectoryInfo(folder);
        while (dir is not null && Contains(dir.FullName) && dir.Exists && !dir.EnumerateFileSystemInfos().Any())
        {
            var parent = dir.Parent;
            dir.Delete();
            dir = parent;
        }
    }

    public static async Task<(string Hash, long Size)> HashAsync(string path, CancellationToken ct = default)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        var hash = await SHA256.HashDataAsync(stream, ct);
        return (Convert.ToHexString(hash), stream.Length);
    }
}
