using System.Text.RegularExpressions;

namespace PdmLite.Core.Documents;

/// <summary>Имя вхождения в обозревателе: было → станет.</summary>
public sealed record OccurrenceRename(string Assembly, string OldName, string NewName);

/// <summary>
/// Имена вхождений сборки в обозревателе Inventor: «{имя документа}:{номер}». После замены
/// компонента (например, из Content Center на библиотечный) Inventor оставляет старое имя —
/// здесь вычисляется обычное имя по новому файлу.
/// </summary>
public static class OccurrenceNaming
{
    /// <summary>Имя уже обычное: «{baseName}:число».</summary>
    public static bool IsDefault(string name, string baseName) =>
        Regex.IsMatch(name, "^" + Regex.Escape(baseName) + @":\d+$", RegexOptions.CultureInvariant);

    /// <summary>
    /// Новые имена для вхождений с необычными именами. Номер — наименьший свободный среди имён
    /// этой сборки; обычные имена не меняются.
    /// </summary>
    /// <param name="allNames">Имена всех вхождений сборки (одного уровня).</param>
    /// <param name="candidates">Вхождения, которые нужно проверить: текущее имя и имя документа.</param>
    /// <returns>Пары «текущее имя → новое» только для тех, что меняются (в порядке кандидатов).</returns>
    public static IReadOnlyList<(string OldName, string NewName)> Plan(IEnumerable<string> allNames,
        IEnumerable<(string Current, string BaseName)> candidates)
    {
        var toRename = candidates.Where(c => !string.IsNullOrWhiteSpace(c.BaseName) && !IsDefault(c.Current, c.BaseName)).ToList();
        var renamed = toRename.Select(c => c.Current).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var used = allNames.Where(n => !renamed.Contains(n)).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var result = new List<(string, string)>();
        foreach (var (current, baseName) in toRename)
        {
            var number = 1;
            while (used.Contains($"{baseName}:{number}"))
                number++;
            var name = $"{baseName}:{number}";
            used.Add(name);
            result.Add((current, name));
        }
        return result;
    }
}
