using PdmLite.Core.Domain;

namespace PdmLite.Core.Events;

/// <summary>Проект открыт или его дерево перестроено.</summary>
public sealed record ProjectOpened(ProjectInfo Project, ProjectItem Root);

/// <summary>Пользователь выбрал элементы в дереве.</summary>
public sealed record SelectionChanged(IReadOnlyList<ProjectItem> Items);

/// <summary>В Inventor сменился активный документ (<c>null</c> — нет активного документа).</summary>
public sealed record ActiveDocumentChanged(string? Path);

/// <summary>Документ сохранён в Inventor.</summary>
public sealed record DocumentSaved(string Path);

/// <summary>Документ закрыт в Inventor.</summary>
public sealed record DocumentClosed(string Path);

/// <summary>Изменилось состояние связи с Inventor (для внешнего приложения).</summary>
public sealed record InventorConnectionChanged(bool IsConnected, string? InventorVersion)
{
    public string? Error { get; init; }
}

/// <summary>Запрос показать и выделить документ в дереве проекта.</summary>
public sealed record RevealDocumentRequested(string Path);

/// <summary>В сборку вставлен компонент из Content Center Inventor.</summary>
public sealed record ContentCenterComponentPlaced(string AssemblyPath, string ComponentPath, string? PartNumber);

/// <summary>В дереве проекта файлы или папки перетащены в папку <paramref name="TargetFolder"/> — нужно переместить.</summary>
public sealed record MoveRequested(IReadOnlyList<string> Paths, string TargetFolder);

/// <summary>Файлы проекта переименованы или перемещены (старый путь → новый).</summary>
public sealed record ProjectFilesMoved(IReadOnlyDictionary<string, string> Moves);
