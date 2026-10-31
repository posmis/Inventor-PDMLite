using System.Text;
using System.Text.RegularExpressions;

namespace PdmLite.Core.Naming;

/// <summary>Части имени файла документа: обозначение, код документа по ГОСТ 2.102 и наименование.</summary>
/// <param name="Designation">Обозначение, например <c>АБВГ.00.00.000</c>.</param>
/// <param name="Code">Код документа (<c>СБ</c>, <c>МЭ</c>, <c>Э3</c> …) или пустая строка.</param>
/// <param name="Name">Наименование или пустая строка.</param>
public sealed record DocumentName(string Designation, string Code, string Name);

/// <summary>
/// Имя файла по правилу <c>{Обозначение}{Код} {Наименование}{.расширение}</c>:
/// <c>АБВГ.00.00.000СБ Жгут.iam</c>, <c>АБВГ.00.00.000СБ Жгут.dwg</c>, <c>АБВГ.00.00.000МЭ Жгут.dwg</c>.
/// </summary>
/// <remarks>
/// Обозначение и код пишутся слитно, наименование — через пробел, пробелы внутри наименования
/// сохраняются (повторы схлопываются). Запрещённые в Windows символы удаляются, <c>/</c> и <c>\</c>
/// заменяются на <c>-</c>.
/// </remarks>
public static partial class FileNameBuilder
{
    public const char Separator = ' ';

    private const string ForbiddenChars = ":*?\"<>|";

    /// <summary>Имя файла; обозначение обязательно.</summary>
    /// <param name="designation">Обозначение, например <c>АБВГ.00.00.000</c>.</param>
    /// <param name="name">Наименование, например <c>Жгут</c>.</param>
    /// <param name="documentCode">Код документа по ГОСТ 2.102 (<c>СБ</c>, <c>МЭ</c> …) или <c>null</c>.</param>
    /// <param name="extension">Расширение с точкой или без.</param>
    public static string Build(string designation, string name, string? documentCode, string extension)
    {
        if (Normalize(designation).Length == 0)
            throw new ArgumentException("Обозначение не может быть пустым.", nameof(designation));
        return Compose(designation, documentCode, name, extension.Trim().ToLowerInvariant());
    }

    /// <summary>
    /// Имя файла из частей; обозначение может быть пустым (тогда код не пишется, имя — только наименование).
    /// Расширение — как передано (с точкой или без).
    /// </summary>
    public static string Compose(string? designation, string? documentCode, string? name, string extension)
    {
        var head = Normalize(designation ?? "");
        if (head.Length > 0)
            head += NormalizeCode(documentCode);
        var tail = Normalize(name ?? "");
        var stem = head.Length == 0 ? tail : tail.Length == 0 ? head : head + Separator + tail;
        if (stem.Length == 0)
            throw new ArgumentException("Не указаны ни обозначение, ни наименование.");

        var ext = extension.Trim();
        return stem + (ext.Length == 0 || ext[0] == '.' ? ext : "." + ext);
    }

    /// <summary>
    /// Разбирает имя файла <b>без расширения</b> по правилу (у обозначения точки — расширение отрезать нельзя
    /// угадыванием: «АБВГ.00.00.000»). <c>null</c> — имя не по правилу (нет обозначения с цифрой в начале,
    /// например «Кронштейн»).
    /// </summary>
    public static DocumentName? TryParse(string stem)
    {
        var match = NamePattern().Match(stem.Trim());
        return match.Success
            ? new DocumentName(match.Groups["des"].Value, match.Groups["code"].Value, Collapse(match.Groups["name"].Value))
            : null;
    }

    /// <summary>Приводит строку к виду, допустимому в имени файла.</summary>
    public static string Normalize(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (ch is '/' or '\\')
                sb.Append('-');
            else if (char.IsWhiteSpace(ch))
            {
                if (sb.Length > 0 && sb[^1] != ' ')
                    sb.Append(' ');
            }
            else if (!char.IsControl(ch) && !ForbiddenChars.Contains(ch))
                sb.Append(ch);
        }

        // Windows не допускает точку или пробел в конце имени; повтор дефисов схлопывается.
        var result = Regex.Replace(sb.ToString(), "-{2,}", "-");
        return result.Trim(' ', '.', '-');
    }

    private static string NormalizeCode(string? code) => Normalize(code ?? "").Replace(" ", "");

    private static string Collapse(string value) => Regex.Replace(value.Trim(), @"\s+", " ");

    /// <summary>
    /// Обозначение — слово, оканчивающееся цифрой (самое короткое, после которого допустим код);
    /// код — 1–3 заглавные русские буквы и до двух цифр; дальше через пробел — наименование.
    /// </summary>
    [GeneratedRegex(@"^(?<des>\S*?\d)(?<code>[А-ЯЁ]{1,3}\d{0,2})?(?:\s+(?<name>.*))?$", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();
}
