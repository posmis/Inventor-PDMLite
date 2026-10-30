using System.Diagnostics;
using Microsoft.Extensions.Logging;
using PdmLite.Core.Documents;

namespace PdmLite.Modules.InventorLink;

/// <summary>
/// Открывает документ в запущенном Inventor через плагин. Если связи нет —
/// открывает файл через Windows (запустится Inventor, связанный с типом файла).
/// </summary>
public sealed class LinkedDocumentLauncher(IInventorConnection connection, ILogger<LinkedDocumentLauncher> logger) : IDocumentLauncher
{
    public async Task OpenAsync(string path, CancellationToken cancellationToken = default)
    {
        if (connection.IsConnected)
        {
            await connection.OpenDocumentAsync(path, cancellationToken);
            return;
        }

        logger.LogInformation("Нет связи с Inventor, файл открывается через Windows: {Path}", path);
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
    }
}
