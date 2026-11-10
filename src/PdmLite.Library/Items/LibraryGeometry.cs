namespace PdmLite.Library.Items;

/// <summary>
/// Сравнение геометрии по объёму и площади поверхности. Копия файла с другим обозначением
/// даёт те же значения; разные типоразмеры — разные.
/// </summary>
public static class LibraryGeometry
{
    private const double Tolerance = 1e-4; // относительная

    public static bool Known(double? volume, double? area) => volume > 0 && area > 0;

    public static bool Same(double? volumeA, double? areaA, double? volumeB, double? areaB) =>
        Known(volumeA, areaA) && Known(volumeB, areaB)
        && Close(volumeA!.Value, volumeB!.Value) && Close(areaA!.Value, areaB!.Value);

    /// <summary>Кандидаты одного типа сравниваются в отсортированном окне объёма, без округления допуска.</summary>
    public static IEnumerable<(T First, T Second)> MatchingPairs<T>(
        IEnumerable<(T Item, int Type, double? Volume, double? Area)> candidates, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        foreach (var group in candidates.Where(c => Known(c.Volume, c.Area)).GroupBy(c => c.Type))
        {
            ct.ThrowIfCancellationRequested();
            var sorted = group.OrderBy(c => c.Volume).ToArray();
            for (var a = 0; a < sorted.Length; a++)
            {
                ct.ThrowIfCancellationRequested();
                for (var b = a + 1; b < sorted.Length; b++)
                {
                    ct.ThrowIfCancellationRequested();
                    if (!Close(sorted[a].Volume!.Value, sorted[b].Volume!.Value)) break;
                    if (Same(sorted[a].Volume, sorted[a].Area, sorted[b].Volume, sorted[b].Area))
                        yield return (sorted[a].Item, sorted[b].Item);
                }
            }
        }
        ct.ThrowIfCancellationRequested();
    }

    private static bool Close(double a, double b) => Math.Abs(a - b) <= Tolerance * Math.Max(Math.Abs(a), Math.Abs(b));
}
