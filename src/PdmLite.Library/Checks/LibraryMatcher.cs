using System.Text.RegularExpressions;
using PdmLite.Library.Designations;
using PdmLite.Library.Items;

namespace PdmLite.Library.Checks;

/// <summary>Вариант замены: компонент библиотеки и насколько он похож.</summary>
/// <param name="Exact">Обозначение совпадает (без учёта пробелов, регистра и похожих букв).</param>
/// <param name="Extra">Сколько лишних чисел у библиотечного (класс прочности, покрытие…): меньше — ближе.</param>
public sealed record LibraryMatch(LibraryItemSummary Item, bool Exact, int Extra);

/// <summary>
/// Подбор компонента библиотеки по обозначению из сборки. Обозначения из Content Center записаны
/// иначе: «Болт ГОСТ 7798-70 M8-6g×30(S13)» против «Болт M8-6gx30.109(S13) ГОСТ 7798-70». Похожим
/// считается компонент с тем же наименованием и стандартом, в котором есть все числа исходного
/// обозначения в том же порядке (лишние числа — класс прочности, покрытие — допустимы).
/// </summary>
public sealed partial class LibraryMatcher
{
    private readonly Dictionary<string, LibraryItemSummary> _byKey = [];
    private readonly List<(LibraryItemSummary Item, string Name, string? Standard, IReadOnlyList<string> Numbers)> _items = [];

    public LibraryMatcher(IEnumerable<LibraryItemSummary> library)
    {
        foreach (var item in library)
        {
            _byKey.TryAdd(DesignationNormalizer.ToKey(item.Designation), item);
            var (name, standard, numbers) = Parse(item.Designation);
            _items.Add((item, name, standard, numbers));
        }
    }

    /// <summary>Варианты замены, лучшие первыми; пусто — ничего похожего.</summary>
    public IReadOnlyList<LibraryMatch> Find(string designation, int max = 10)
    {
        var canonical = DesignationNormalizer.ToCanonical(designation);
        var exact = _byKey.GetValueOrDefault(DesignationNormalizer.ToKey(canonical));
        var (name, standard, numbers) = Parse(canonical);

        IEnumerable<LibraryMatch> similar = numbers.Count == 0 || name.Length == 0
            ? []
            : _items.Where(i => i.Item != exact && i.Name == name && i.Standard == standard && IsSubsequence(numbers, i.Numbers))
                .Select(i => new LibraryMatch(i.Item, false, i.Numbers.Count - numbers.Count))
                .OrderBy(m => m.Extra)
                .ThenBy(m => m.Item.Designation, Core.Naming.NaturalComparer.Instance);

        return (exact is null ? similar : similar.Prepend(new LibraryMatch(exact, true, 0))).Take(max).ToList();
    }

    /// <summary>Наименование (начальные слова), ключ стандарта и числа обозначения без чисел стандарта.</summary>
    private static (string Name, string? Standard, IReadOnlyList<string> Numbers) Parse(string designation)
    {
        var canonical = DesignationNormalizer.ToCanonical(designation);
        var standard = StandardParser.Find(canonical);
        var rest = standard is null ? canonical : RemoveFirst(canonical, standard);
        var name = string.Join(' ', DesignationNormalizer.LeadingWords(canonical));
        var numbers = Number().Matches(DesignationNormalizer.ToKey(rest)).Select(m => m.Value.TrimStart('0') is { Length: > 0 } n ? n : "0").ToList();
        return (name, standard is null ? null : DesignationNormalizer.ToKey(standard), numbers);
    }

    private static string RemoveFirst(string text, string part)
    {
        var index = text.IndexOf(part, StringComparison.Ordinal);
        return index < 0 ? text : text.Remove(index, part.Length);
    }

    /// <summary>Все числа <paramref name="needle"/> есть в <paramref name="haystack"/> в том же порядке.</summary>
    private static bool IsSubsequence(IReadOnlyList<string> needle, IReadOnlyList<string> haystack)
    {
        var j = 0;
        foreach (var number in haystack)
            if (j < needle.Count && number == needle[j])
                j++;
        return j == needle.Count;
    }

    [GeneratedRegex(@"\d+")]
    private static partial Regex Number();
}
