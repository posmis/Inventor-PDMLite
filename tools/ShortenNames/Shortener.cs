using System.Security.Cryptography;
using System.Text;

namespace ShortenNames;

/// <summary>Переименование: полный путь было → станет. <see cref="To"/> = <c>null</c> — сократить нельзя (длинна сама папка).</summary>
internal sealed record NameChange(string From, string? To, string Reason)
{
    public string FromName => Path.GetFileName(From);
    public string? ToName => To is null ? null : Path.GetFileName(To);
}

/// <summary>
/// Подбор коротких имён. Имена из STEP похожи на «Q1~TO-263-3_L10.2-…-BR~TO-263-3_L10.2-…-BR~GJMo_stp»: части
/// через «~» повторяются — повторы убираются. Если всё равно длинно — имя обрезается, а в конец добавляется
/// короткий код из исходного имени (у разных файлов разные имена, у «X.iam» и «X.stp» — одинаковые).
/// </summary>
internal static class Shortener
{
    private static readonly string[] InventorExtensions = [".ipt", ".iam", ".idw", ".dwg", ".ipn"];

    /// <summary>Самое короткое допустимое имя без расширения.</summary>
    public const int MinStem = 12;

    public static bool IsInventorFile(string path) => InventorExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public static bool InOldVersions(string root, string path) =>
        Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(p => p.Equals("OldVersions", StringComparison.OrdinalIgnoreCase));

    /// <summary>Файлы, у которых полный путь длиннее <paramref name="maxPath"/> или имя длиннее <paramref name="maxName"/>, и новые имена.</summary>
    public static List<NameChange> Plan(string root, IEnumerable<string> files, int maxPath, int maxName)
    {
        var all = files.Where(f => !InOldVersions(root, f)).ToList();
        var changes = new List<NameChange>();
        // Занятые имена по папкам: существующие (кроме переименовываемых) и уже выданные.
        var taken = all.GroupBy(f => Path.GetDirectoryName(f)!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(f => Path.GetFileName(f)).ToHashSet(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);

        var longOnes = all.Where(f => f.Length > maxPath || Path.GetFileName(f).Length > maxName).ToList();
        foreach (var file in longOnes)
            taken[Path.GetDirectoryName(file)!].Remove(Path.GetFileName(file));

        foreach (var file in longOnes.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            var folder = Path.GetDirectoryName(file)!;
            var extension = Path.GetExtension(file);
            var stem = Path.GetFileNameWithoutExtension(file);
            var allowed = Math.Min(maxName - extension.Length, maxPath - folder.Length - 1 - extension.Length);
            var reason = file.Length > maxPath ? $"путь {file.Length} символов" : $"имя {Path.GetFileName(file).Length} символов";
            if (allowed < MinStem)
            {
                changes.Add(new NameChange(file, null, reason + " — слишком длинная сама папка, переименуйте папки выше"));
                taken[folder].Add(Path.GetFileName(file));
                continue;
            }

            var shortStem = ShortenStem(stem, allowed);
            var name = Unique(shortStem, extension, allowed, taken[folder]);
            taken[folder].Add(name);
            changes.Add(new NameChange(file, Path.Combine(folder, name), reason));
        }
        return changes;
    }

    /// <summary>Короткое имя без расширения не длиннее <paramref name="max"/>.</summary>
    public static string ShortenStem(string stem, int max)
    {
        // 1. Повторы частей «~» (вложенность сборок STEP) — один раз; приписки формата («_stp», «_step», «_igs») — лишние.
        var parts = stem.Split('~').Select(p => WithoutFormat(p.Trim())).Where(p => p.Length > 0).ToList();
        var distinct = new List<string>();
        foreach (var part in parts)
            if (!distinct.Contains(part, StringComparer.OrdinalIgnoreCase))
                distinct.Add(part);
        var result = string.Join("~", distinct);
        if (result.Length == 0)
            result = stem;
        if (result.Length <= max)
            return result;

        // 2. Обрезать и добавить код исходного имени (чтобы разные длинные имена не совпали).
        var code = "_" + Code(stem);
        return Clean(result[..Math.Max(1, max - code.Length)]) + code;
    }

    /// <summary>Часть имени без приписки формата импорта в конце: «GJMo_stp» → «GJMo».</summary>
    public static string WithoutFormat(string part)
    {
        var match = FormatSuffix.Match(part);
        return match.Success && match.Index > 0 ? part[..match.Index].TrimEnd(' ', '.', '-', '_') : part;
    }

    private static readonly System.Text.RegularExpressions.Regex FormatSuffix =
        new(@"[ ._\-](stp|step|igs|iges|sat|x_t|x_b)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>Свободное имя в папке: при совпадении — «_2», «_3»… (с сохранением длины).</summary>
    public static string Unique(string stem, string extension, int max, HashSet<string> taken)
    {
        var name = stem + extension;
        for (var n = 2; taken.Contains(name); n++)
        {
            var suffix = "_" + n;
            name = Clean(stem[..Math.Min(stem.Length, max - suffix.Length)]) + suffix + extension;
        }
        return name;
    }

    /// <summary>Пять символов из SHA-1 исходного имени (без учёта регистра) — одинаковы для одинаковых имён.</summary>
    public static string Code(string stem) =>
        Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(stem.ToUpperInvariant())))[..5];

    /// <summary>Конец обрезанного имени без разделителей, точек и пробелов.</summary>
    private static string Clean(string value) => value.TrimEnd(' ', '.', '~', '-', '_');
}
