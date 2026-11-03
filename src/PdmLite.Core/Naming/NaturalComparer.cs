using System.Globalization;

namespace PdmLite.Core.Naming;

/// <summary>
/// «Естественный» порядок строк: числа сравниваются по величине, а не по символам —
/// «Болт M6-6gx20» раньше «Болт M6-6gx100», «M8» раньше «M12». Текст — без учёта регистра.
/// </summary>
public sealed class NaturalComparer : IComparer<string?>, System.Collections.IComparer
{
    public static NaturalComparer Instance { get; } = new();

    private static CompareInfo Culture => CultureInfo.CurrentCulture.CompareInfo;

    int System.Collections.IComparer.Compare(object? x, object? y) => CompareCore(x?.ToString(), y?.ToString());

    public int Compare(string? x, string? y) => CompareCore(x, y);

    private static int CompareCore(string? x, string? y)
    {
        if (ReferenceEquals(x, y))
            return 0;
        if (x is null)
            return -1;
        if (y is null)
            return 1;

        int i = 0, j = 0;
        while (i < x.Length && j < y.Length)
        {
            if (char.IsAsciiDigit(x[i]) && char.IsAsciiDigit(y[j]))
            {
                var (si, sj) = (i, j);
                while (i < x.Length && char.IsAsciiDigit(x[i]))
                    i++;
                while (j < y.Length && char.IsAsciiDigit(y[j]))
                    j++;

                // Сравнение по величине без переполнения: без ведущих нулей — сначала длина, потом цифры.
                var a = x.AsSpan(si, i - si).TrimStart('0');
                var b = y.AsSpan(sj, j - sj).TrimStart('0');
                if (a.Length != b.Length)
                    return a.Length.CompareTo(b.Length);
                var digits = a.SequenceCompareTo(b);
                if (digits != 0)
                    return Math.Sign(digits);
                // «007» и «7» равны по величине — короткая запись раньше, но только если дальше всё одинаково.
                continue;
            }

            var (ti, tj) = (i, j);
            while (i < x.Length && !char.IsAsciiDigit(x[i]))
                i++;
            while (j < y.Length && !char.IsAsciiDigit(y[j]))
                j++;
            var text = Culture.Compare(x, ti, i - ti, y, tj, j - tj, CompareOptions.IgnoreCase);
            if (text != 0)
                return text;
        }

        var rest = (x.Length - i).CompareTo(y.Length - j);
        return rest != 0 ? rest : string.CompareOrdinal(x, y);
    }
}
