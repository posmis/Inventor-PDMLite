namespace PdmLite.Core.UI;

public enum PanelPlacement
{
    /// <summary>Навигация: дерево проекта, поиск.</summary>
    Navigator,

    /// <summary>Сведения о выбранном документе.</summary>
    Details,
}

/// <param name="Id">Уникальный идентификатор панели.</param>
/// <param name="Title">Заголовок (вкладка).</param>
/// <param name="Placement">Где разместить панель.</param>
/// <param name="CreateView">
/// Фабрика представления. Возвращает объект интерфейса хоста (для WPF — <c>FrameworkElement</c>);
/// тип не указан, чтобы ядро не зависело от WPF.
/// </param>
/// <param name="Order">Порядок среди панелей одного места.</param>
public sealed record PanelDescriptor(
    string Id,
    string Title,
    PanelPlacement Placement,
    Func<IServiceProvider, object> CreateView,
    int Order = 0);
