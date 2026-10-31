using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using PdmLite.Data.Library;

namespace PdmLite.Library.Import;

/// <summary>Строка сохранённого списка подготовки: файл и правки пользователя.</summary>
public sealed record ImportListEntry
{
    public required string Path { get; init; }
    public bool Include { get; init; } = true;
    public DesignationSource Source { get; init; }

    /// <summary>Обозначение; при загрузке берётся как есть только для ручного ввода, иначе готовится заново из источника.</summary>
    public string Designation { get; init; } = "";
    public LibraryItemKind Kind { get; init; }
    public string? Manufacturer { get; init; }

    /// <summary>Стандарт, указанный вручную.</summary>
    public string? ManualStandard { get; init; }

    /// <summary>Раздел библиотеки (путь, например «Крепёж/Болты») — Id разделов в разных базах разные.</summary>
    public string? CategoryPath { get; init; }

    /// <summary>Раздел выбран пользователем, а не предложен автоматически.</summary>
    public bool CategoryChosen { get; init; }
}

/// <summary>
/// Список подготовки к внесению (файл .pdmlist, JSON): позволяет разобрать большую папку
/// в несколько приёмов — сохранить правки и продолжить позже.
/// </summary>
public sealed class ImportList
{
    public const string Extension = ".pdmlist";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // кириллица читаемой, а не \uXXXX
        Converters = { new JsonStringEnumConverter() },
    };

    public int Version { get; init; } = 1;
    public List<ImportListEntry> Items { get; init; } = [];

    public static ImportList From(ImportSession session) => new()
    {
        Items = session.Candidates.Select(c => new ImportListEntry
        {
            Path = c.SourcePath,
            Include = c.Include,
            Source = c.Source,
            Designation = c.Designation,
            Kind = c.Kind,
            Manufacturer = string.IsNullOrWhiteSpace(c.Manufacturer) ? null : c.Manufacturer.Trim(),
            ManualStandard = c.ManualStandard,
            CategoryPath = session.Catalog.Get(c.CategoryId)?.Path,
            CategoryChosen = c.CategoryId != c.SuggestedCategoryId,
        }).ToList(),
    };

    public void Save(string path)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, Options));
        File.Move(temp, path, overwrite: true);
    }

    public static ImportList Load(string path)
    {
        try
        {
            var list = JsonSerializer.Deserialize<ImportList>(File.ReadAllText(path), Options)
                       ?? throw new InvalidDataException("Файл пуст.");
            if (list.Version != 1)
                throw new InvalidDataException($"Версия списка {list.Version} не поддерживается.");
            // «Items»: null или пустые строки в списке (правка вручную) — не повод падать.
            var items = (list.Items ?? []).Where(i => i is not null && !string.IsNullOrWhiteSpace(i.Path)).ToList();
            return new ImportList { Version = list.Version, Items = items };
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Файл не является списком PdmLite: {ex.Message}", ex);
        }
    }
}
