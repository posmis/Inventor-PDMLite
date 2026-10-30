using System.Text.RegularExpressions;

namespace PdmLite.Library.Designations;

/// <summary>Распознавание стандарта в обозначении: «Болт M10… ГОСТ 7798-70» → «ГОСТ 7798-70».</summary>
public static partial class StandardParser
{
    /// <summary>Стандарт из обозначения или <c>null</c>. Пробелы в префиксе приводятся к одному.</summary>
    public static string? Find(string designation)
    {
        var match = StandardRegex().Match(designation);
        if (!match.Success)
            return null;
        var prefix = string.Join(' ', match.Groups["prefix"].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return $"{prefix} {match.Groups["number"].Value}";
    }

    // Номер: цифры, группы через точку или дефис (7798-70, 7380-1, 26-04-2000-77, 71547-2024).
    // После дефиса обязательна цифра — так «7040-M6-10» даёт «7040».
    [GeneratedRegex(
        @"(?<![\p{L}])(?<prefix>ГОСТ\s+Р\s+(?:ИСО|ISO)|ГОСТ\s+(?:ИСО|ISO)|ГОСТ\s+Р|ГОСТ|ОСТ|DIN\s+EN\s+ISO|DIN\s+EN|DIN|EN\s+ISO|EN|ISO|ИСО|IEC|ТУ|СТО)\s*(?<number>\d+(?:[.\-]\d+)*)",
        RegexOptions.CultureInvariant)]
    private static partial Regex StandardRegex();
}
