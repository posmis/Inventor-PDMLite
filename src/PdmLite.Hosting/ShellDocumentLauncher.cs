using System.Diagnostics;
using PdmLite.Core.Documents;

namespace PdmLite.Hosting;

/// <summary>Открытие файла приложением, связанным с ним в Windows.</summary>
public sealed class ShellDocumentLauncher : IDocumentLauncher
{
    public Task OpenAsync(string path, CancellationToken cancellationToken = default)
    {
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
        return Task.CompletedTask;
    }
}
