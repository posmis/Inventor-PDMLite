using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text.Json;

namespace PdmLite.Link;

/// <summary>Клиент именованного канала: запросы с ожиданием ответа и приём событий.</summary>
public sealed class LinkClient : IAsyncDisposable
{
    private readonly string _pipeName;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<LinkMessage>> _pending = new();
    private readonly CancellationTokenSource _cts = new();
    private LinkConnection? _connection;
    private Task? _readLoop;
    private int _disconnected;

    public LinkClient(string endpoint) => _pipeName = LinkEndpoints.PipeName(endpoint);

    public bool IsConnected => _connection is { IsConnected: true } && Volatile.Read(ref _disconnected) == 0;

    /// <summary>Событие от сервера. Вызывается в фоновом потоке.</summary>
    public event Action<LinkMessage>? EventReceived;

    /// <summary>Связь разорвана. Вызывается один раз, в фоновом потоке.</summary>
    public event Action? Disconnected;

    /// <summary>Подключается к серверу.</summary>
    /// <returns><c>false</c>, если сервер не запущен или не ответил за отведённое время.</returns>
    public async Task<bool> TryConnectAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (_connection is not null)
            throw new InvalidOperationException("Клиент уже подключался. Для повторного подключения создайте новый.");

        var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);
            await pipe.ConnectAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException or IOException or UnauthorizedAccessException)
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return false;
        }

        _connection = new LinkConnection(pipe);
        _readLoop = Task.Run(() => ReadLoopAsync(_connection, _cts.Token));
        return true;
    }

    public async Task<TResult?> RequestAsync<TResult>(string name, object? args = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(name, args, timeout ?? TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        return response.PayloadAs<TResult>();
    }

    public Task RequestAsync(string name, object? args = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default) =>
        SendAsync(name, args, timeout ?? TimeSpan.FromSeconds(30), cancellationToken);

    private async Task<LinkMessage> SendAsync(string name, object? args, TimeSpan timeout, CancellationToken ct)
    {
        var connection = _connection ?? throw new InvalidOperationException("Нет подключения.");
        var id = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<LinkMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        try
        {
            await connection.WriteAsync(new LinkMessage { Type = LinkMessage.RequestType, Id = id, Name = name, Payload = LinkMessage.ToPayload(args) }, ct)
                .ConfigureAwait(false);

            var response = await tcs.Task.WaitAsync(timeout, ct).ConfigureAwait(false);
            return response.Ok ? response : throw new LinkRemoteException(response.Error ?? "Неизвестная ошибка.");
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private async Task ReadLoopAsync(LinkConnection connection, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && await connection.ReadAsync(ct).ConfigureAwait(false) is { } message)
            {
                switch (message.Type)
                {
                    case LinkMessage.ResponseType when message.Id is not null && _pending.TryGetValue(message.Id, out var tcs):
                        tcs.TrySetResult(message);
                        break;
                    case LinkMessage.EventType:
                        try { EventReceived?.Invoke(message); }
                        catch { /* ошибка подписчика не рвёт связь */ }
                        break;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or JsonException)
        {
        }
        finally
        {
            OnDisconnected();
        }
    }

    private void OnDisconnected()
    {
        if (Interlocked.Exchange(ref _disconnected, 1) != 0)
            return;
        foreach (var tcs in _pending.Values)
            tcs.TrySetException(new IOException("Связь разорвана."));
        Disconnected?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        if (_connection is not null)
            await _connection.DisposeAsync().ConfigureAwait(false);
        if (_readLoop is not null)
            await _readLoop.ConfigureAwait(false);
        _cts.Dispose();
    }
}
