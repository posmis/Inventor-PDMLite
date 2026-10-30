using System.Diagnostics;
using Microsoft.Extensions.Logging;
using PdmLite.Hosting;
using PdmLite.Link;

namespace PdmLite.AddIn;

/// <summary>
/// «Показать в Explorer»: передаёт документ запущенному Explorer
/// или запускает Explorer с нужными аргументами.
/// </summary>
public sealed class ExplorerLauncher(ILogger<ExplorerLauncher> logger)
{
    public const string ExeName = "PdmLite.Explorer.exe";

    public async Task RevealAsync(string? documentPath, string? projectPath)
    {
        await using (var client = new LinkClient(LinkEndpoints.Explorer))
        {
            if (await client.TryConnectAsync(TimeSpan.FromMilliseconds(300)))
            {
                if (documentPath is not null)
                    await client.RequestAsync(LinkProtocol.Requests.RevealDocument, new RevealArgs(documentPath, projectPath));
                else if (projectPath is not null)
                    await client.RequestAsync(LinkProtocol.Requests.OpenProject, new ProjectArgs(projectPath));
                return;
            }
        }

        var exe = FindExplorer()
                  ?? throw new FileNotFoundException(
                      "PdmLite Explorer не найден. Запустите его один раз вручную — плагин запомнит его расположение.");

        var start = new ProcessStartInfo(exe) { UseShellExecute = false };
        if (projectPath is not null)
            start.ArgumentList.Add(projectPath);
        if (documentPath is not null)
        {
            start.ArgumentList.Add("--reveal");
            start.ArgumentList.Add(documentPath);
        }

        logger.LogInformation("Запуск Explorer: {Exe}", exe);
        Process.Start(start)?.Dispose();
    }

    private static string? FindExplorer()
    {
        var candidates = new List<string?>
        {
            new JsonSettings<SharedSettings>(SharedSettings.Name).Value.ExplorerPath,
            // Раскладка установки install.ps1: %LocalAppData%\PdmLite\{AddIn,Explorer}
            Path.Combine(AppContext.BaseDirectory, "..", "Explorer", ExeName),
            Path.Combine(Path.GetDirectoryName(typeof(ExplorerLauncher).Assembly.Location)!, "..", "Explorer", ExeName),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PdmLite", "Explorer", ExeName),
        };

        // Сборка из Visual Studio: ...\src\PdmLite.AddIn\bin\{Config}\{TFM} → ...\src\PdmLite.Explorer\bin\{Config}\{TFM}
        var output = new DirectoryInfo(Path.GetDirectoryName(typeof(ExplorerLauncher).Assembly.Location)!);
        if (output.Parent?.Parent?.Parent?.Parent is { } src)
            candidates.Add(Path.Combine(src.FullName, "PdmLite.Explorer", "bin", output.Parent.Name, output.Name, ExeName));

        return candidates
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => Path.GetFullPath(p!))
            .FirstOrDefault(File.Exists);
    }
}
