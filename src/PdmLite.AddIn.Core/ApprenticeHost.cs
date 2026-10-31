using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using PdmLite.Core.Documents;

namespace PdmLite.AddIn.Core;

/// <summary>
/// Свойства файлов через Apprentice в отдельном процессе (PdmLite Explorer в служебном режиме без окна).
/// Нужен для записи: Inventor API не может открыть и сохранить копию файла Content Center, исходник которого
/// открыт в сборке (одинаковый внутренний идентификатор), а Apprentice внутри Inventor не работает.
/// Процесс запускается при первом запросе и живёт до выгрузки плагина.
/// </summary>
public sealed class ApprenticeHost(ILogger<ApprenticeHost> logger) : IDisposable
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(2);

    private readonly object _gate = new();
    private Process? _process;
    private PropertyHostClient? _client;

    /// <summary>Есть ли PdmLite Explorer, через который можно работать.</summary>
    public bool IsAvailable => ExplorerLauncher.FindExplorer() is not null;

    public async Task<DocumentProperties> ReadAsync(string path, bool geometry, CancellationToken ct) =>
        (await SendAsync(new PropertyHostRequest(geometry ? "read" : "readNoGeometry", path), ct).ConfigureAwait(false)).Properties
        ?? new DocumentProperties();

    public Task WriteAsync(string path, DocumentPropertyChanges changes, CancellationToken ct) =>
        SendAsync(new PropertyHostRequest("write", path, changes), ct);

    public Task ReplaceReferencesAsync(string path, IReadOnlyDictionary<string, string> map, CancellationToken ct) =>
        SendAsync(new PropertyHostRequest("replace", path, Map: new Dictionary<string, string>(map, StringComparer.OrdinalIgnoreCase)), ct);

    /// <summary>Запрос; процесс завершился — один перезапуск и повтор.</summary>
    private async Task<PropertyHostResponse> SendAsync(PropertyHostRequest request, CancellationToken ct)
    {
        try
        {
            return await Client().SendAsync(request, RequestTimeout, ct).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            logger.LogWarning(ex, "Процесс свойств завершился — перезапуск");
            Stop();
            return await Client().SendAsync(request, RequestTimeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Stop(); // ответ мог прийти позже и сбить порядок — начинаем с чистого процесса
            throw;
        }
    }

    private PropertyHostClient Client()
    {
        lock (_gate)
        {
            if (_client is not null && _process is { HasExited: false })
                return _client;
            Stop();

            var exe = ExplorerLauncher.FindExplorer()
                      ?? throw new InvalidOperationException("Не найден PdmLite Explorer — он нужен, чтобы записывать свойства файлов из Inventor. " +
                                                             "Соберите решение или установите PdmLite (deploy\\install.ps1).");
            var start = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                StandardInputEncoding = new UTF8Encoding(false),
                StandardOutputEncoding = new UTF8Encoding(false),
            };
            start.ArgumentList.Add("--property-host");
            _process = Process.Start(start) ?? throw new InvalidOperationException("Не удалось запустить процесс свойств.");
            _client = new PropertyHostClient(_process.StandardOutput, _process.StandardInput);
            logger.LogInformation("Процесс свойств запущен: {Exe} (PID {Pid})", exe, _process.Id);
            return _client;
        }
    }

    private void Stop()
    {
        lock (_gate)
        {
            if (_process is { } process)
            {
                try
                {
                    process.StandardInput.Close(); // вход закрыт — процесс завершается сам
                    if (!process.WaitForExit(3000))
                        process.Kill();
                }
                catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
                {
                    logger.LogDebug(ex, "Остановка процесса свойств");
                }
                process.Dispose();
            }
            _process = null;
            _client = null;
        }
    }

    public void Dispose() => Stop();
}
