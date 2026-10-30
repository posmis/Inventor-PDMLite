using System.Text;

namespace PdmLite.Core.Naming;

/// <summary>
/// Формирование имени файла по правилу <c>{Обозначение}{Код}_{Наименование}{.расширение}</c>.
/// </summary>
/// <remarks>
/// Пробелы заменяются на <c>-</c>, повторы схлопываются, запрещённые в Windows символы
/// удаляются (<c>/</c> и <c>\</c> заменяются на <c>-</c>). Имя файла только генерируется:
/// источник истины — iProperties и БД, обратно имя не разбирается.
/// </remarks>
public static class FileNameBuilder
{
    public const char Separator = '_';
    public const char SpaceReplacement = '-';

    private const string ForbiddenChars = ":*?\"<>|";

    /// <param name="designation">Обозначение, например <c>АБВГ.301.001</c>.</param>
    /// <param name="name">Наименование, например <c>Рама сварная</c>.</param>
    /// <param name="documentCode">Код документа по ГОСТ 2.102 (<c>СБ</c>, <c>МЭ</c> …) или <c>null</c>.</param>
    /// <param name="extension">Расширение с точкой или без.</param>
    public static string Build(string designation, string name, string? documentCode, string extension)
    {
        var head = Normalize(designation) + Normalize(documentCode ?? string.Empty);
        if (head.Length == 0)
            throw new ArgumentException("Обозначение не может быть пустым.", nameof(designation));

        var tail = Normalize(name);
        var ext = extension.Length == 0 || extension[0] == '.' ? extension : "." + extension;

        return (tail.Length == 0 ? head : head + Separator + tail) + ext.ToLowerInvariant();
    }

    /// <summary>Приводит строку к виду, допустимому в имени файла.</summary>
    public static string Normalize(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value.Trim())
        {
            if (char.IsWhiteSpace(ch) || ch is '/' or '\\')
                Append(sb, SpaceReplacement);
            else if (!char.IsControl(ch) && !ForbiddenChars.Contains(ch))
                Append(sb, ch);
        }

        // Windows не допускает точку или пробел в конце имени.
        return sb.ToString().Trim(SpaceReplacement, '.');
    }

    private static void Append(StringBuilder sb, char ch)
    {
        if (ch == SpaceReplacement && sb.Length > 0 && sb[^1] == SpaceReplacement)
            return;
        sb.Append(ch);
    }
}
