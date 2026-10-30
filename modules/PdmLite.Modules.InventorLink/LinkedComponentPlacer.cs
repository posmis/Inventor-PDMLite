using PdmLite.Core.Documents;

namespace PdmLite.Modules.InventorLink;

/// <summary>Вставка компонента из Explorer: команда уходит плагину в запущенном Inventor.</summary>
public sealed class LinkedComponentPlacer(IInventorConnection connection) : IComponentPlacer
{
    public bool CanPlace => connection.IsConnected;

    public Task PlaceAsync(string path, CancellationToken cancellationToken = default) =>
        connection.IsConnected
            ? connection.PlaceComponentAsync(path, cancellationToken)
            : throw new InvalidOperationException("Нет связи с Inventor. Запустите Inventor с плагином PdmLite.");
}
