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
        Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));

    public bool Contains(string fullPath) =>
        Path.GetFullPath(fullPath).StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    /// <summary>Относительный путь файла компонента (разделитель — «/»).</summary>
    public static string BuildRelativePath(IEnumerable<string> categoryPath, string? subfolder, string designation, string extension)
    {
        var parts = categoryPath.Select(SafeName).ToList();
        if (!string.IsNullOrWhiteSpace(subfolder))
            parts.Add(SafeName(subfolder));
        parts.Add(SafeName(designation) + extension.ToLowerInvariant());
        return string.Join('/', parts);
    }

    /// <summary>Имя файла или папки: запрещённые в Windows символы → «-», без точек и пробелов в конце.</summary>
    public static string SafeName(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var c in name.Trim())
            sb.Append(Array.IndexOf(Forbidden, c) >= 0 || char.IsControl(c) ? '-' : c);
        return sb.ToString().TrimEnd('.', ' ');
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

    /// <summary>Удаляет файл (откат неудачного добавления), включая «только чтение».</summary>
    public void Remove(string fullPath)
    {
        if (!File.Exists(fullPath))
            return;
        File.SetAttributes(fullPath, FileAttributes.Normal);
        File.Delete(fullPath);
    }

    public static async Task<(string Hash, long Size)> HashAsync(string path, CancellationToken ct = default)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        var hash = await SHA256.HashDataAsync(stream, ct);
        return (Convert.ToHexString(hash), stream.Length);
    }
}
