using PdmLite.Library.Catalog;

namespace PdmLite.Library.Import;

/// <summary>
/// Сеанс добавления: кандидаты и сведения о библиотеке на момент анализа
/// (существующие обозначения и файлы — для проверки дубликатов).
/// </summary>
public sealed class ImportSession
{
    private readonly Dictionary<string, string> _existingKeys;
    private readonly Dictionary<string, string> _existingHashes;

    internal ImportSession(LibraryCatalog catalog, Dictionary<string, string> existingKeys,
        Dictionary<string, string> existingHashes, bool canWriteProperties, string? propertiesUnavailableReason)
    {
        Catalog = catalog;
        _existingKeys = existingKeys;
        _existingHashes = existingHashes;
        CanWriteProperties = canWriteProperties;
        PropertiesUnavailableReason = propertiesUnavailableReason;
    }

    public LibraryCatalog Catalog { get; }

    /// <summary>Правила обозначений (замены и шаблоны) на момент анализа или последнего обновления.</summary>
    public Rules.DesignationRuleSet Rules { get; internal set; } = PdmLite.Library.Rules.DesignationRuleSet.Empty;

    public List<ImportCandidate> Candidates { get; } = [];
    public List<SkippedFile> Skipped { get; } = [];

    /// <summary>Ключ обозначения → обозначение компонента, уже лежащего в библиотеке.</summary>
    internal IReadOnlyDictionary<string, string> ExistingKeys => _existingKeys;

    /// <summary>Хеш файла (исходного или в библиотеке) → обозначение компонента.</summary>
    internal IReadOnlyDictionary<string, string> ExistingHashes => _existingHashes;

    public bool CanWriteProperties { get; }
    public string? PropertiesUnavailableReason { get; }

    public int ReadyCount => Candidates.Count(c => c.CanImport);
    public int ErrorCount => Candidates.Count(c => c.Status == ImportStatus.Error);
    public int WarningCount => Candidates.Count(c => c.Status == ImportStatus.Warning);

    /// <summary>
    /// Запоминает внесённый компонент: повторное внесение того же файла в этом же сеансе
    /// будет распознано как дубликат.
    /// </summary>
    internal void Remember(string key, string designation, params string[] hashes)
    {
        _existingKeys[key] = designation;
        foreach (var hash in hashes)
            _existingHashes.TryAdd(hash, designation);
    }
}
