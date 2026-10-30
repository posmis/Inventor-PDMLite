using PdmLite.Core.Documents;
using PdmLite.Data.Library;

namespace PdmLite.Library.Import;

public enum ImportSeverity
{
    Info,
    Warning,
    Error,
}

public sealed record ImportIssue(ImportSeverity Severity, string Message);

public enum ImportStatus
{
    /// <summary>Готов к внесению.</summary>
    Ready,

    /// <summary>Можно внести, но есть замечания.</summary>
    Warning,

    /// <summary>Нельзя внести, пока не исправлено.</summary>
    Error,

    /// <summary>Исключён пользователем.</summary>
    Excluded,
}

/// <summary>Файл-кандидат на добавление в библиотеку. Поля с сеттерами пользователь правит в таблице.</summary>
public sealed class ImportCandidate
{
    public required string SourcePath { get; init; }
    public string FileName => Path.GetFileName(SourcePath);
    public LibraryDocumentType DocumentType { get; init; }

    /// <summary>Прочитанные свойства или <c>null</c>, если прочитать не удалось.</summary>
    public DocumentProperties? Properties { get; init; }
    public string? PropertiesError { get; init; }

    public string FileHash { get; init; } = "";
    public long FileSize { get; init; }

    /// <summary>Для сборки: хеши файлов компонентов (путь → хеш), чтобы узнать уже внесённые.</summary>
    public IReadOnlyDictionary<string, string> ReferenceHashes { get; init; } = new Dictionary<string, string>();

    public bool Include { get; set; } = true;

    /// <summary>Обозначение, как оно было в файле (до приведения к единому виду).</summary>
    public string OriginalDesignation { get; init; } = "";

    /// <summary>Обозначение (приводится к единому виду при проверке).</summary>
    public string Designation { get; set; } = "";

    public LibraryItemKind Kind { get; set; } = LibraryItemKind.Standard;
    public string? Standard { get; set; }
    public string? Manufacturer { get; set; }
    public int? CategoryId { get; set; }

    /// <summary>Раздел, предложенный автоматически (для запоминания исправлений пользователя).</summary>
    public int? SuggestedCategoryId { get; set; }

    /// <summary>Путь в библиотеке (вычисляется при проверке).</summary>
    public string? TargetRelativePath { get; set; }

    public List<ImportIssue> Issues { get; } = [];

    public ImportStatus Status =>
        !Include ? ImportStatus.Excluded
        : Issues.Any(i => i.Severity == ImportSeverity.Error) ? ImportStatus.Error
        : Issues.Any(i => i.Severity == ImportSeverity.Warning) ? ImportStatus.Warning
        : ImportStatus.Ready;

    public bool CanImport => Include && Status is ImportStatus.Ready or ImportStatus.Warning;
}

/// <summary>Файл, пропущенный при сканировании (резервные копии Inventor и т.п.).</summary>
public sealed record SkippedFile(string Path, string Reason);

public sealed record ImportProgress(int Done, int Total, string? Current);

public sealed record ImportResult(int Added, IReadOnlyList<(ImportCandidate Candidate, string Error)> Failed);
