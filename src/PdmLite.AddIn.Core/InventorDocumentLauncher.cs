using PdmLite.Core.Documents;
using PdmLite.Core.Threading;

namespace PdmLite.AddIn.Core;

/// <summary>Открытие документа через Inventor API (в главном потоке Inventor).</summary>
public sealed class InventorDocumentLauncher(Inv.Application inventor, IUiDispatcher ui) : IDocumentLauncher
{
    public Task OpenAsync(string path, CancellationToken cancellationToken = default) =>
        ui.InvokeAsync(() =>
        {
            // Для уже открытого документа Open возвращает его же; Activate выводит окно на передний план.
            var document = inventor.Documents.Open(path, true);
            document.Activate();
        });
}
