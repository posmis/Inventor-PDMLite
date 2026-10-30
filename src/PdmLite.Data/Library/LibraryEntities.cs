namespace PdmLite.Data.Library;

/// <summary>Раздел или группа классификатора библиотеки (Крепёж → Болты).</summary>
/// <remarks>Уровни «Стандарт» и «Производитель → Серия» не хранятся: они строятся из полей компонента.</remarks>
public sealed class LibraryCategory
{
    public int Id { get; set; }
    public int? ParentId { get; set; }
    public LibraryCategory? Parent { get; set; }
    public List<LibraryCategory> Children { get; set; } = [];
    public required string Name { get; set; }
    public int SortOrder { get; set; }
}

public enum LibraryItemKind
{
    /// <summary>Изделие по стандарту (ГОСТ, ISO, DIN…).</summary>
    Standard = 1,

    /// <summary>Покупное изделие производителя.</summary>
    Purchased = 2,
}

public enum LibraryDocumentType
{
    Part = 1,
    Assembly = 2,
}

/// <summary>Компонент библиотеки.</summary>
public sealed class LibraryItem
{
    public Guid Id { get; set; }
    public int CategoryId { get; set; }
    public LibraryCategory? Category { get; set; }
    public LibraryItemKind Kind { get; set; }
    public LibraryDocumentType DocumentType { get; set; }

    /// <summary>Обозначение — строго как в стандарте (для покупных — наименование, тип, производитель).</summary>
    public required string Designation { get; set; }

    /// <summary>Нормализованное обозначение для поиска и проверки дубликатов (уникально).</summary>
    public required string DesignationKey { get; set; }

    public string? Standard { get; set; }

    /// <summary>Стандарт указан вручную (иначе распознаётся из обозначения).</summary>
    public bool StandardIsManual { get; set; }

    public string? Manufacturer { get; set; }

    /// <summary>Путь к файлу текущей версии относительно папки библиотеки.</summary>
    public required string RelativePath { get; set; }

    /// <summary>SHA-256 содержимого файла (hex).</summary>
    public required string FileHash { get; set; }
    public long FileSize { get; set; }

    /// <summary>Объём и площадь поверхности (см³, см²) — для поиска одинаковой геометрии; <c>null</c> — неизвестны.</summary>
    public double? Volume { get; set; }
    public double? Area { get; set; }

    /// <summary>
    /// SHA-256 исходного файла, из которого компонент добавлен. Файл в библиотеке отличается
    /// (в него записаны свойства), а повторное добавление того же исходника нужно распознать.
    /// </summary>
    public string? SourceHash { get; set; }

    public int Version { get; set; } = 1;
    public DateTime CreatedAt { get; set; }
    public required string CreatedBy { get; set; }
    public DateTime ModifiedAt { get; set; }
    public required string ModifiedBy { get; set; }

    public List<LibraryItemVersion> Versions { get; set; } = [];
    public List<LibraryItemProperty> Properties { get; set; } = [];

    /// <summary>Для сборки — входящие в неё компоненты библиотеки.</summary>
    public List<LibraryItemReference> References { get; set; } = [];
}

/// <summary>Версия компонента. Текущая версия тоже записана здесь.</summary>
public sealed class LibraryItemVersion
{
    public long Id { get; set; }
    public Guid ItemId { get; set; }
    public LibraryItem? Item { get; set; }
    public int Version { get; set; }
    public required string RelativePath { get; set; }
    public required string FileHash { get; set; }
    public long FileSize { get; set; }
    public DateTime CreatedAt { get; set; }
    public required string CreatedBy { get; set; }
    public string? Comment { get; set; }
}

/// <summary>Дополнительное свойство компонента (типоразмер, класс прочности, артикул…).</summary>
public sealed class LibraryItemProperty
{
    public Guid ItemId { get; set; }
    public required string Name { get; set; }
    public required string Value { get; set; }
}

/// <summary>Связь «сборка → компонент» внутри библиотеки.</summary>
public sealed class LibraryItemReference
{
    public Guid AssemblyId { get; set; }
    public Guid ComponentId { get; set; }
}

public enum LibraryRuleKind
{
    /// <summary>По стандарту: «ГОСТ 7798-70» → Крепёж/Болты.</summary>
    Standard = 1,

    /// <summary>По первому слову обозначения: «болт» → Крепёж/Болты.</summary>
    FirstWord = 2,
}

/// <summary>Правило автоматического выбора раздела при добавлении.</summary>
public sealed class LibraryCategoryRule
{
    public int Id { get; set; }
    public LibraryRuleKind Kind { get; set; }

    /// <summary>Нормализованный образец (ключ стандарта или слово в нижнем регистре).</summary>
    public required string Pattern { get; set; }

    public int CategoryId { get; set; }
    public LibraryCategory? Category { get; set; }

    /// <summary>Правило запомнено из исправления пользователя (приоритетнее встроенных).</summary>
    public bool IsLearned { get; set; }
}

/// <summary>Журнал действий с библиотекой.</summary>
public sealed class LibraryLogEntry
{
    public long Id { get; set; }
    public DateTime At { get; set; }
    public required string User { get; set; }
    public required string Action { get; set; }
    public Guid? ItemId { get; set; }
    public string? Details { get; set; }
}

/// <summary>
/// Правило замены в обозначении при подготовке файлов к добавлению в библиотеку
/// (например, «Болт_» → «Болт »). Формат обозначений по стандартам не проверяется.
/// </summary>
public sealed class DesignationRule
{
    public int Id { get; set; }

    /// <summary>Порядок применения (замены выполняются по возрастанию).</summary>
    public int SortOrder { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>Что искать.</summary>
    public required string Pattern { get; set; }

    /// <summary>На что заменить.</summary>
    public string? Replacement { get; set; }

    /// <summary>Pattern — регулярное выражение (иначе обычный текст).</summary>
    public bool IsRegex { get; set; }

    /// <summary>Пояснение.</summary>
    public string? Comment { get; set; }
}
