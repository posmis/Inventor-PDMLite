namespace PdmLite.Core.Documents;

/// <summary>Свойства документа Inventor (iProperties) и его ссылки.</summary>
public sealed record DocumentProperties
{
    /// <summary>Проект → Обозначение (Part Number).</summary>
    public string? PartNumber { get; init; }

    /// <summary>Проект → Описание (Description).</summary>
    public string? Description { get; init; }

    /// <summary>Документ → Наименование (Title).</summary>
    public string? Title { get; init; }

    /// <summary>Файл создан из Content Center (компонент библиотеки Inventor).</summary>
    public bool IsContentCenter { get; init; }

    /// <summary>Пользовательские свойства.</summary>
    public IReadOnlyDictionary<string, string> Custom { get; init; } = new Dictionary<string, string>();

    /// <summary>Полные пути файлов, на которые ссылается документ (компоненты сборки).</summary>
    public IReadOnlyList<string> References { get; init; } = [];
}

/// <summary>Изменения свойств: <c>null</c> — не менять.</summary>
public sealed record DocumentPropertyChanges
{
    public string? PartNumber { get; init; }
    public string? Description { get; init; }
    public string? Title { get; init; }
    public IReadOnlyDictionary<string, string> Custom { get; init; } = new Dictionary<string, string>();
}

/// <summary>
/// Чтение и запись iProperties файлов Inventor без открытия их в интерфейсе.
/// Вне Inventor — Apprentice (Inventor View), внутри Inventor — Inventor API.
/// </summary>
public interface IDocumentPropertyStore
{
    /// <summary>Хранилище работает (например, установлен Inventor или Inventor View).</summary>
    bool IsAvailable { get; }

    /// <summary>Почему недоступно (для сообщения пользователю).</summary>
    string? UnavailableReason { get; }

    Task<DocumentProperties> ReadAsync(string path, CancellationToken cancellationToken = default);

    Task WriteAsync(string path, DocumentPropertyChanges changes, CancellationToken cancellationToken = default);

    /// <summary>Заменяет ссылки сборки: старый полный путь → новый.</summary>
    Task ReplaceReferencesAsync(string path, IReadOnlyDictionary<string, string> map, CancellationToken cancellationToken = default);
}

/// <summary>Заглушка, когда читать свойства нечем (нет Inventor/Inventor View).</summary>
public sealed class UnavailablePropertyStore(string reason) : IDocumentPropertyStore
{
    public bool IsAvailable => false;
    public string? UnavailableReason => reason;

    public Task<DocumentProperties> ReadAsync(string path, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException(reason);

    public Task WriteAsync(string path, DocumentPropertyChanges changes, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException(reason);

    public Task ReplaceReferencesAsync(string path, IReadOnlyDictionary<string, string> map, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException(reason);
}
