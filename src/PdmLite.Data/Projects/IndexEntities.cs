namespace PdmLite.Data.Projects;

/// <summary>
/// Файл проекта в индексе ссылок: что это за документ и когда его читали. Повторно файл
/// читается, только если изменились его размер или дата.
/// </summary>
public sealed class IndexedFile
{
    public long Id { get; set; }

    /// <summary>Полный путь, как на диске.</summary>
    public required string Path { get; set; }

    /// <summary>Путь для поиска: без учёта регистра и вида разделителя (уникален).</summary>
    public required string PathKey { get; set; }

    /// <summary>Папка проекта, при сканировании которой файл найден (ключ, как <see cref="PathKey"/>).</summary>
    public required string RootKey { get; set; }

    /// <summary>Тип документа (<c>PdmLite.Core.Domain.DocumentKind</c>).</summary>
    public int Kind { get; set; }

    public long Size { get; set; }
    public DateTime ModifiedUtc { get; set; }
    public DateTime ScannedAt { get; set; }

    public string? PartNumber { get; set; }
    public string? Title { get; set; }

    /// <summary>Файл не прочитан (например, DWG не Inventor или файл повреждён).</summary>
    public string? Error { get; set; }

    public List<IndexedReference> References { get; set; } = [];
}

/// <summary>Ссылка документа на другой файл: компонент сборки, модель чертежа, базовая деталь.</summary>
public sealed class IndexedReference
{
    public long Id { get; set; }
    public long FileId { get; set; }

    /// <summary>Полный путь, на который ссылается документ (файла может уже не быть).</summary>
    public required string TargetPath { get; set; }

    /// <summary>Путь для поиска «где используется».</summary>
    public required string TargetKey { get; set; }
}
