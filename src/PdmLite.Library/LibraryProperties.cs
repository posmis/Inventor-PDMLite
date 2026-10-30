using PdmLite.Core.Documents;
using PdmLite.Data.Library;

namespace PdmLite.Library;

/// <summary>Свойства, которые PdmLite записывает в файл компонента библиотеки.</summary>
public static class LibraryProperties
{
    public const string LibraryId = "PdmLite.LibraryId";
    public const string Standard = "Стандарт";
    public const string Manufacturer = "Производитель";
    public const string Category = "Раздел библиотеки";

    /// <summary>
    /// Раздел спецификации по ГОСТ 2.106 — для системы спецификаций:
    /// стандартное изделие → <see cref="StandardProducts"/>, покупное → <see cref="OtherProducts"/>.
    /// </summary>
    public const string SpecSection = "SPEC_Section";
    public const string StandardProducts = "StandardProducts";
    public const string OtherProducts = "OtherProducts";

    public static string SpecSectionFor(LibraryItemKind kind) =>
        kind == LibraryItemKind.Standard ? StandardProducts : OtherProducts;

    /// <summary>Тип компонента по уже записанному в файл разделу спецификации или <c>null</c>.</summary>
    public static LibraryItemKind? KindFromSpecSection(IReadOnlyDictionary<string, string> custom) =>
        custom.TryGetValue(SpecSection, out var value) switch
        {
            true when value.Trim().Equals(StandardProducts, StringComparison.OrdinalIgnoreCase) => LibraryItemKind.Standard,
            true when value.Trim().Equals(OtherProducts, StringComparison.OrdinalIgnoreCase) => LibraryItemKind.Purchased,
            _ => null,
        };

    /// <summary>
    /// Обозначение — в «Обозначение», «Описание» и «Наименование»; раздел спецификации, стандарт,
    /// производитель, раздел библиотеки и Id — в пользовательские свойства.
    /// </summary>
    public static DocumentPropertyChanges For(string designation, LibraryItemKind kind, string? standard, string? manufacturer,
        string categoryPath, Guid? id = null)
    {
        var custom = new Dictionary<string, string>
        {
            [SpecSection] = SpecSectionFor(kind),
            [Standard] = standard ?? "",
            [Manufacturer] = manufacturer ?? "",
            [Category] = categoryPath,
        };
        if (id is { } value)
            custom[LibraryId] = value.ToString();

        return new DocumentPropertyChanges
        {
            PartNumber = designation,
            Description = designation,
            Title = designation,
            Custom = custom,
        };
    }
}
