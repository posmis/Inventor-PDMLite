using PdmLite.Library.Catalog;

namespace PdmLite.Library.Import;

/// <summary>
/// Сеанс добавления: кандидаты и снимок библиотеки на момент анализа
/// (существующие обозначения и файлы — для проверки дубликатов).
/// </summary>
public sealed class ImportSession
{
    internal ImportSession(LibraryCatalog catalog, IReadOnlyDictionary<string, string> existingKeys,
        IReadOnlyDictionary<string, string> existingHashes, bool canWriteProperties, string? propertiesUnavailableReason)
    {
        Catalog = catalog;
        ExistingKeys = existingKeys;
        ExistingHashes = existingHashes;
        CanWriteProperties = canWriteProperties;
        PropertiesUnavailableReason = propertiesUnavailableReason;
    }

    public LibraryCatalog Catalog { get; }

    public List<ImportCandidate> Candidates { get; } = [];
    public List<SkippedFile> Skipped { get; } = [];

    /// <summary>Ключ обозначения → обозначение компонента, уже лежащего в библиотеке.</summary>
    internal IReadOnlyDictionary<string, string> ExistingKeys { get; }

    /// <summary>Хеш файла → обозначение компонента, уже лежащего в библиотеке.</summary>
    internal IReadOnlyDictionary<string, string> ExistingHashes { get; }

    public bool CanWriteProperties { get; }
    public string? PropertiesUnavailableReason { get; }

    public int ReadyCount => Candidates.Count(c => c.CanImport);
    public int ErrorCount => Candidates.Count(c => c.Status == ImportStatus.Error);
    public int WarningCount => Candidates.Count(c => c.Status == ImportStatus.Warning);
}
