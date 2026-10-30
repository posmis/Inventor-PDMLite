using System.Text;
using System.Text.RegularExpressions;

namespace PdmLite.Library.Designations;

/// <summary>
/// Приведение обозначений к единому виду.
/// </summary>
/// <remarks>
/// Правило PdmLite: в кодах обозначения — латиница (<c>M</c>, <c>x</c>, <c>A</c>…), слова остаются
/// русскими: «Болт M10-6gx25.109 ГОСТ 7798-70». Кириллические буквы, похожие на латинские,
/// заменяются только в кодах; марки материалов вроде «08кп», «Ст3сп», «65Г» не трогаются.
/// </remarks>
public static partial class DesignationNormalizer
{
    /// <summary>Кириллица → похожая латиница.</summary>
    private static readonly Dictionary<char, char> Lookalikes = new()
    {
        ['А'] = 'A', ['В'] = 'B', ['Е'] = 'E', ['К'] = 'K', ['М'] = 'M', ['Н'] = 'H', ['О'] = 'O',
        ['Р'] = 'P', ['С'] = 'C', ['Т'] = 'T', ['Х'] = 'X', ['У'] = 'Y',
        ['а'] = 'a', ['е'] = 'e', ['о'] = 'o', ['р'] = 'p', ['с'] = 'c', ['у'] = 'y', ['х'] = 'x',
    };

    /// <summary>Слова, которые остаются кириллицей, даже если состоят из «похожих» букв.</summary>
    private static readonly HashSet<string> ProtectedWords = new(StringComparer.Ordinal)
    {
        "ГОСТ", "ОСТ", "Р", "ИСО", "ТУ", "СТО", "СТ", "ОК",
    };

    /// <summary>
    /// Обозначение для хранения и показа: лишние пробелы убраны, коды — латиницей.
    /// </summary>
    public static string ToCanonical(string designation)
    {
        var text = MultipleSpaces().Replace(designation.Trim(), " ");
        text = text.Replace('×', 'x').Replace('–', '-').Replace('—', '-');

        var tokens = text.Split(' ');
        for (var i = 0; i < tokens.Length; i++)
        {
            var previous = i > 0 ? tokens[i - 1] : null;
            tokens[i] = NormalizeToken(tokens[i], previous);
        }

        return string.Join(' ', tokens);
    }

    private static string NormalizeToken(string token, string? previous)
    {
        if (ProtectedWords.Contains(token.TrimEnd('.', ',')) || (token == "Р" && previous is "ГОСТ" or "ОСТ"))
            return token;
        if (!token.Any(IsCyrillic))
            return token;

        // Токен разбирается на серии букв: «М10-6gх25.109» → «М», «gх»; «08кп» → «кп».
        // Серия переводится в латиницу, если все её кириллические буквы похожи на латинские
        // и это код, а не слово: в ней есть латиница или она не длиннее двух букв.
        var chars = token.ToCharArray();
        var i = 0;
        while (i < chars.Length)
        {
            if (!char.IsLetter(chars[i]))
            {
                i++;
                continue;
            }

            var start = i;
            while (i < chars.Length && char.IsLetter(chars[i]))
                i++;

            var run = new ReadOnlySpan<char>(chars, start, i - start);
            var cyrillic = 0;
            var latin = false;
            var allLookalike = true;
            foreach (var c in run)
            {
                if (IsCyrillic(c))
                {
                    cyrillic++;
                    allLookalike &= Lookalikes.ContainsKey(c);
                }
                else if (IsLatin(c))
                {
                    latin = true;
                }
            }

            if (cyrillic > 0 && allLookalike && (latin || run.Length <= 2))
                for (var j = start; j < i; j++)
                    if (Lookalikes.TryGetValue(chars[j], out var mapped))
                        chars[j] = mapped;
        }

        return new string(chars);
    }

    /// <summary>
    /// Ключ для поиска и проверки дубликатов: регистр, пробелы, похожие буквы, запятая/точка
    /// и знак «на» не влияют на сравнение.
    /// </summary>
    public static string ToKey(string designation)
    {
        var sb = new StringBuilder(designation.Length);
        foreach (var raw in designation)
        {
            // Сначала верхний регистр, потом замена: «в» и «В» должны дать одну букву.
            var upper = char.ToUpperInvariant(raw);
            var ch = Lookalikes.TryGetValue(upper, out var latin) ? latin : upper;
            if (char.IsWhiteSpace(ch))
                continue;
            sb.Append(ch switch
            {
                '×' => 'X',
                ',' => '.',
                '–' or '—' => '-',
                _ => ch,
            });
        }
        return sb.ToString();
    }

    /// <summary>
    /// Начальные слова обозначения до первого кода или стандарта, в нижнем регистре:
    /// «Гайка шестигранная - M3…» → «гайка шестигранная»; «Болт M10…» → «болт».
    /// </summary>
    public static IReadOnlyList<string> LeadingWords(string designation)
    {
        var words = new List<string>();
        foreach (var token in designation.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            // Цифры, латиница (коды, артикулы, ISO/DIN) или стандарт — начало кодовой части.
            if (token.Any(char.IsDigit) || token.Any(IsLatin) || token == "-" || ProtectedWords.Contains(token))
                break;
            if (!token.All(c => char.IsLetter(c) || c == '-'))
                break;
            words.Add(token.ToLowerInvariant().Replace('ё', 'е'));
        }
        return words;
    }

    private static bool IsCyrillic(char c) => c is >= 'А' and <= 'я' or 'Ё' or 'ё';

    private static bool IsLatin(char c) => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z';

    [GeneratedRegex(@"\s+")]
    private static partial Regex MultipleSpaces();
}
