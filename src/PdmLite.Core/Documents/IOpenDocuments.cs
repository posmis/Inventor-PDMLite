namespace PdmLite.Core.Documents;

/// <summary>Документ, загруженный в Inventor.</summary>
/// <param name="IsDirty">Есть несохранённые изменения.</param>
/// <param name="IsVisible">Открыт в своём окне (а не только загружен как компонент сборки или модель чертежа).</param>
public sealed record OpenDocument(string Path, bool IsDirty, bool IsVisible);

/// <summary>
/// Документы, открытые в Inventor. Файл, загруженный в Inventor, нельзя переименовать или переместить:
/// Inventor держит его в памяти и перезапишет. Внутри Inventor документы можно закрыть и снова открыть;
/// из Explorer — только узнать список (через связь с плагином).
/// </summary>
public interface IOpenDocuments
{
    /// <summary>Документы можно закрыть и снова открыть отсюда (только внутри Inventor).</summary>
    bool CanClose { get; }

    /// <summary>Все загруженные документы (и видимые, и подгруженные как компоненты). Нет Inventor — пусто.</summary>
    Task<IReadOnlyList<OpenDocument>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Закрывает видимые документы, которые сами входят в <paramref name="affected"/> или ссылаются на них
    /// (на любом уровне), сохраняя изменения. Возвращает пути закрытых видимых документов — чтобы открыть их снова.
    /// Если после закрытия что-то из <paramref name="affected"/> осталось загруженным — <see cref="InvalidOperationException"/>.
    /// </summary>
    Task<IReadOnlyList<string>> CloseAsync(IReadOnlyCollection<string> affected, CancellationToken cancellationToken = default);

    /// <summary>Открывает документы (например, закрытые перед переименованием — уже по новым путям).</summary>
    Task OpenAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default);
}

/// <summary>Inventor недоступен: открытых документов нет.</summary>
public sealed class NoOpenDocuments : IOpenDocuments
{
    public bool CanClose => false;

    public Task<IReadOnlyList<OpenDocument>> ListAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<OpenDocument>>([]);

    public Task<IReadOnlyList<string>> CloseAsync(IReadOnlyCollection<string> affected, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>([]);

    public Task OpenAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
