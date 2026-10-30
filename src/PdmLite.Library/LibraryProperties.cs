using PdmLite.Core.Documents;

namespace PdmLite.Library;

/// <summary>Свойства, которые PdmLite записывает в файл компонента библиотеки.</summary>
public static class LibraryProperties
{
    public const string LibraryId = "PdmLite.LibraryId";
    public const string Standard = "Стандарт";
    public const string Manufacturer = "Производитель";
    public const string Category = "Раздел библиотеки";

    /// <summary>
    /// Обозначение — в «Обозначение», «Описание» и «Наименование»; остальное — в пользовательские свойства.
    /// </summary>
    public static DocumentPropertyChanges For(string designation, string? standard, string? manufacturer, string categoryPath, Guid? id = null)
    {
        var custom = new Dictionary<string, string>
        {
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
