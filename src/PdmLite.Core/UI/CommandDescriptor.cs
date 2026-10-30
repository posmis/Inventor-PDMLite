using PdmLite.Core.Domain;

namespace PdmLite.Core.UI;

[Flags]
public enum CommandPlacement
{
    None = 0,

    /// <summary>Контекстное меню элемента дерева.</summary>
    ContextMenu = 1,

    /// <summary>Главное меню / панель инструментов хоста.</summary>
    Toolbar = 2,
}

/// <summary>Контекст выполнения команды: выбранные элементы.</summary>
public sealed record CommandContext(IReadOnlyList<ProjectItem> Selection)
{
    public static CommandContext Empty { get; } = new([]);

    public ProjectItem? Single => Selection.Count == 1 ? Selection[0] : null;
}

/// <param name="Id">Уникальный идентификатор команды.</param>
/// <param name="Title">Текст пункта меню.</param>
/// <param name="Placement">Где показывать команду.</param>
/// <param name="Execute">Действие.</param>
/// <param name="CanExecute">Доступна ли команда; по умолчанию — всегда.</param>
/// <param name="Order">Порядок в меню.</param>
/// <param name="Group">Группа: между группами в меню ставится разделитель.</param>
/// <param name="IsDefault">Команда по двойному щелчку (выделяется жирным).</param>
/// <param name="Icon">Ключ иконки из <see cref="IconKeys"/>.</param>
public sealed record CommandDescriptor(
    string Id,
    string Title,
    CommandPlacement Placement,
    Func<CommandContext, Task> Execute,
    Func<CommandContext, bool>? CanExecute = null,
    int Order = 0,
    string Group = "",
    bool IsDefault = false,
    string? Icon = null)
{
    public bool IsAvailable(CommandContext context) => CanExecute?.Invoke(context) ?? true;
}
