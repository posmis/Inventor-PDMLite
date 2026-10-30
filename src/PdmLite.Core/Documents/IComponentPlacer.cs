namespace PdmLite.Core.Documents;

/// <summary>
/// Вставка компонента в активную сборку Inventor. Внутри Inventor — напрямую,
/// из Explorer — через связь с плагином.
/// </summary>
public interface IComponentPlacer
{
    /// <summary>Вставка сейчас возможна (например, есть связь с Inventor).</summary>
    bool CanPlace { get; }

    /// <summary>
    /// Запускает в Inventor команду «Вставить компонент» с этим файлом: пользователь указывает
    /// место щелчком, как при обычной вставке. Если активный документ — не сборка, бросает
    /// <see cref="InvalidOperationException"/> с объяснением.
    /// </summary>
    Task PlaceAsync(string path, CancellationToken cancellationToken = default);
}

public sealed class UnavailableComponentPlacer(string reason) : IComponentPlacer
{
    public bool CanPlace => false;

    public Task PlaceAsync(string path, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException(reason);
}
