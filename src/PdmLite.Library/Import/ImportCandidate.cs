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

/// <summary>Откуда взято обозначение.</summary>
public enum DesignationSource
{
    /// <summary>Свойство «Обозначение» (Part Number).</summary>
    PartNumber,

    /// <summary>Свойство «Описание» (Description).</summary>
    Description,

    /// <summary>Свойство «Наименование» (Title).</summary>
    Title,

    /// <summary>Имя файла без расширения.</summary>
    FileName,

    /// <summary>Введено или исправлено вручную — правила к нему повторно не применяются.</summary>
    Manual,
}

public enum ImportStatus
{
    /// <summary>Готов к внесению.</summary>
    Ready,

    /// <summary>Можно внести, но есть замечания.</summary>
    Warning,

    /// <summary>Нельзя внести, пока не исправлено.</summary>
    Error,
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

    /// <summary>Файл не удалось прочитать целиком (занят, нет доступа) — внести нельзя.</summary>
    public string? ReadError { get; init; }

    public string FileHash { get; init; } = "";
    public long FileSize { get; init; }

    /// <summary>Для сборки: хеши файлов компонентов (путь → хеш), чтобы узнать уже внесённые.</summary>
    public IReadOnlyDictionary<string, string> ReferenceHashes { get; init; } = new Dictionary<string, string>();

    public bool Include { get; set; } = true;

    /// <summary>Обозначение, как оно было в источнике (до приведения к единому виду и правил).</summary>
    public string OriginalDesignation { get; set; } = "";

    /// <summary>Откуда взято обозначение.</summary>
    public DesignationSource Source { get; set; } = DesignationSource.PartNumber;

    /// <summary>Какие правила замены изменили обозначение (пояснения правил).</summary>
    public IReadOnlyList<string> AppliedRules { get; set; } = [];

    /// <summary>Текст источника обозначения или <c>null</c>, если он пуст.</summary>
    public string? SourceText(DesignationSource source)
    {
        var text = source switch
        {
            DesignationSource.PartNumber => Properties?.PartNumber,
            DesignationSource.Description => Properties?.Description,
            DesignationSource.Title => Properties?.Title,
            DesignationSource.FileName => FileNameWithoutExtension,
            _ => null,
        };
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    private string FileNameWithoutExtension => System.IO.Path.GetFileNameWithoutExtension(FileName);

    /// <summary>Обозначение (приводится к единому виду при проверке).</summary>
    public string Designation { get; set; } = "";

    public LibraryItemKind Kind { get; set; } = LibraryItemKind.Standard;

    /// <summary>Тип взят из свойства SPEC_Section, записанного в файле.</summary>
    public bool KindFromFile { get; init; }
    public string? Standard { get; set; }

    /// <summary>Стандарт, указанный пользователем вручную (<c>null</c> — распознаётся из обозначения).</summary>
    public string? ManualStandard { get; set; }

    public string? Manufacturer { get; set; }
    public int? CategoryId { get; set; }

    /// <summary>Раздел, предложенный автоматически (для запоминания исправлений пользователя).</summary>
    public int? SuggestedCategoryId { get; set; }

    /// <summary>Путь в библиотеке (вычисляется при проверке).</summary>
    public string? TargetRelativePath { get; set; }

    /// <summary>Что уже есть в библиотеке: тот же компонент, тот же файл или та же геометрия; <c>null</c> — ничего.</summary>
    public string? LibraryMatch { get; set; }

    public List<ImportIssue> Issues { get; } = [];

    /// <summary>Результат проверки — и у исключённых строк (исключение показывается отдельно).</summary>
    public ImportStatus Status =>
        Issues.Any(i => i.Severity == ImportSeverity.Error) ? ImportStatus.Error
        : Issues.Any(i => i.Severity == ImportSeverity.Warning) ? ImportStatus.Warning
        : ImportStatus.Ready;

    public bool CanImport => Include && Status is ImportStatus.Ready or ImportStatus.Warning;
}

/// <summary>Файл, пропущенный при сканировании (резервные копии Inventor и т.п.).</summary>
public sealed record SkippedFile(string Path, string Reason);

public sealed record ImportProgress(int Done, int Total, string? Current);

/// <param name="Committed">Внесённые: исходный файл → файл в библиотеке (для замены в сборках).</param>
public sealed record ImportResult(int Added, IReadOnlyList<(ImportCandidate Candidate, string Error)> Failed)
{
    public IReadOnlyDictionary<string, string> Committed { get; init; } = new Dictionary<string, string>();
}
