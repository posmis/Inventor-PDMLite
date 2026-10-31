using Microsoft.Extensions.Logging;
using PdmLite.Core.Documents;

namespace PdmLite.Modules.InventorLink;

/// <summary>
/// Документы, открытые в Inventor, — для Explorer через связь с плагином. Закрывать их отсюда нельзя:
/// пользователь закрывает сам (в Inventor могут быть несохранённые изменения).
/// </summary>
public sealed class LinkedOpenDocuments(IInventorConnection connection, ILogger<LinkedOpenDocuments> logger) : IOpenDocuments
{
    public bool CanClose => false;

    public async Task<IReadOnlyList<OpenDocument>> ListAsync(CancellationToken cancellationToken = default)
    {
        if (!connection.IsConnected)
            return [];
        try
        {
            var result = await connection.GetOpenDocumentsAsync(cancellationToken);
            return result?.Documents.Select(d => new OpenDocument(d.Path, d.IsDirty, d.IsVisible)).ToList() ?? [];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Старый плагин без этого запроса или обрыв связи — список неизвестен.
            logger.LogWarning(ex, "Не получен список документов, открытых в Inventor");
            throw new InvalidOperationException("Не удалось узнать, какие документы открыты в Inventor (обновите плагин PdmLite в Inventor или закройте Inventor). "
                                                + ex.Message, ex);
        }
    }

    public Task<IReadOnlyList<string>> CloseAsync(IReadOnlyCollection<string> affected, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("Закройте документы в Inventor.");

    public Task OpenAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
