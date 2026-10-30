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

    private static bool Close(double a, double b) => Math.Abs(a - b) <= Tolerance * Math.Max(Math.Abs(a), Math.Abs(b));
}
