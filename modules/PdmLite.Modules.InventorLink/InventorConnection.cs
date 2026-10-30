using Microsoft.Extensions.Logging;
using PdmLite.Core.Events;
using PdmLite.Link;

namespace PdmLite.Modules.InventorLink;

public interface IInventorConnection
{
    bool IsConnected { get; }
    string? InventorVersion { get; }

    Task<InventorStatus?> GetStatusAsync(CancellationToken cancellationToken = default);
    Task OpenDocumentAsync(string path, CancellationToken cancellationToken = default);
}

/// <summary>
/// Поддерживает соединение с плагином PdmLite в Inventor: переподключается,
/// пока Inventor не запущен, и переводит события плагина в события шины.
/// </summary>
public sealed class InventorConnection : IInventorConnection, IAsyncDisposable
{
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromMilliseconds(500);

    private readonly IEventBus events;
    private readonly ILogger<InventorConnection> logger;
    private readonly string _endpoint;
    private readonly TimeSpan _retryInterval;
    private readonly CancellationTokenSource _cts = new();
    private LinkClient? _client;
    private Task? _loop;

    public InventorConnection(IEventBus events, ILogger<InventorConnection> logger)
        : this(events, logger, LinkEndpoints.Inventor, RetryInterval)
    {
    }

    /// <summary>Для тестов: другой канал и интервал переподключения.</summary>
    internal InventorConnection(IEventBus events, ILogger<InventorConnection> logger, string endpoint, TimeSpan retryInterval)
    {
        this.events = events;
        this.logger = logger;
        _endpoint = endpoint;
        _retryInterval = retryInterval;
    }

    public bool IsConnected => _client?.IsConnected == true;
    public string? InventorVersion { get; private set; }

    public void Start() => _loop ??= Task.Run(() => RunAsync(_cts.Token));

    public async Task<InventorStatus?> GetStatusAsync(CancellationToken cancellationToken = default) =>
        _client is { IsConnected: true } client
            ? await client.RequestAsync<InventorStatus>(LinkProtocol.Requests.GetStatus, cancellationToken: cancellationToken)
            : null;

    public Task OpenDocumentAsync(string path, CancellationToken cancellationToken = default) =>
        _client is { IsConnected: true } client
            ? client.RequestAsync(LinkProtocol.Requests.OpenDocument, new DocumentArgs(path), TimeSpan.FromMinutes(5), cancellationToken)
            : throw new InvalidOperationException("Нет связи с Inventor.");

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!IsConnected)
                    await TryConnectAsync(ct);
                await Task.Delay(_retryInterval, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Ошибка связи с Inventor");
            }
        }
    }

    private async Task TryConnectAsync(CancellationToken ct)
    {
        if (_client is { } old)
        {
            // Старое соединение уже разорвано и сообщено — отписываемся, чтобы не сообщить повторно.
            old.EventReceived -= OnEvent;
            old.Disconnected -= OnDisconnected;
            _client = null;
            await old.DisposeAsync();
        }

        var client = new LinkClient(_endpoint);
        if (!await client.TryConnectAsync(ConnectTimeout, ct))
        {
            await client.DisposeAsync();
            return;
        }

        PingResult? ping;
        InventorStatus? status;
        try
        {
            ping = await client.RequestAsync<PingResult>(LinkProtocol.Requests.Ping, cancellationToken: ct);
            status = await client.RequestAsync<InventorStatus>(LinkProtocol.Requests.GetStatus, cancellationToken: ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Плагин подключился, но не ответил — попробуем позже.
            logger.LogWarning(ex, "Inventor не ответил на запрос состояния");
            await client.DisposeAsync();
            return;
        }

        client.EventReceived += OnEvent;
        client.Disconnected += OnDisconnected;
        _client = client;
        InventorVersion = status?.InventorVersion ?? ping?.Version;

        logger.LogInformation("Связь с Inventor {Version} установлена", InventorVersion);
        events.Publish(new InventorConnectionChanged(true, InventorVersion));
        events.Publish(new ActiveDocumentChanged(status?.ActiveDocument));
    }

    private void OnEvent(LinkMessage message)
    {
        var path = message.PayloadAs<DocumentArgs>()?.Path;
        switch (message.Name)
        {
            case LinkProtocol.Events.ActiveDocumentChanged:
                events.Publish(new ActiveDocumentChanged(path));
                break;
            case LinkProtocol.Events.DocumentSaved when path is not null:
                events.Publish(new DocumentSaved(path));
                break;
            case LinkProtocol.Events.DocumentClosed when path is not null:
                events.Publish(new DocumentClosed(path));
                break;
        }
    }

    private void OnDisconnected()
    {
        if (_cts.IsCancellationRequested)
            return;
        logger.LogInformation("Связь с Inventor разорвана");
        InventorVersion = null;
        events.Publish(new InventorConnectionChanged(false, null));
        events.Publish(new ActiveDocumentChanged(null));
    }

    private int _disposed;

    /// <summary>Повторный вызов безопасен: модуль останавливает связь, затем контейнер освобождает сервис.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        await _cts.CancelAsync().ConfigureAwait(false);
        if (_loop is not null)
            await _loop.ConfigureAwait(false);
        if (_client is not null)
            await _client.DisposeAsync().ConfigureAwait(false);
        _cts.Dispose();
    }
}
