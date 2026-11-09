using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace PdmLite.Link;

/// <summary>
/// Сервер именованного канала. Принимает несколько клиентов, отвечает на запросы
/// и рассылает события всем подключённым клиентам.
/// </summary>
public sealed class LinkServer : IAsyncDisposable
{
    public delegate Task<object?> RequestHandler(LinkMessage request, CancellationToken cancellationToken);

    private readonly string _pipeName;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, RequestHandler> _handlers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<LinkConnection, byte> _clients = new();
    private readonly CancellationTokenSource _cts = new();
    private Task? _acceptLoop;
    private int _disposed;
    private readonly object _workGate = new();
    private readonly HashSet<Task> _work = [];
    private Task? _stopTask;
    public TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(5);

    public LinkServer(string endpoint, ILogger? logger = null)
    {
        _pipeName = LinkEndpoints.PipeName(endpoint);
        _logger = logger ?? NullLogger.Instance;
    }

    public int ClientCount => _clients.Count;

    /// <summary>Регистрирует обработчик запроса. Результат обработчика отправляется в ответе.</summary>
    public LinkServer Handle(string requestName, RequestHandler handler)
    {
        _handlers[requestName] = handler;
        return this;
    }

    /// <summary>Регистрирует обработчик запроса с типизированными аргументами.</summary>
    public LinkServer Handle<TArgs>(string requestName, Func<TArgs, CancellationToken, Task<object?>> handler) =>
        Handle(requestName, (request, ct) =>
            handler(request.PayloadAs<TArgs>() ?? throw new ArgumentException($"Запрос '{requestName}' без аргументов."), ct));

    public void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_acceptLoop is not null)
            throw new InvalidOperationException("Сервер уже запущен.");
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
        _logger.LogInformation("Канал связи {Pipe} запущен", _pipeName);
    }

    /// <summary>Отправляет событие всем подключённым клиентам.</summary>
    public async Task BroadcastAsync(string eventName, object? payload)
    {
        var message = new LinkMessage { Type = LinkMessage.EventType, Name = eventName, Payload = LinkMessage.ToPayload(payload) };
        foreach (var client in _clients.Keys)
        {
            try
            {
                await client.WriteAsync(message, _cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
            {
                _clients.TryRemove(client, out _);
            }
        }
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

                await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);

                var connection = new LinkConnection(pipe);
                pipe = null;
                _clients[connection] = 0;
                // Без токена: обслуживание должно стартовать, иначе соединение не будет закрыто.
                if (!Track(() => ServeClientAsync(connection, ct)))
                {
                    _clients.TryRemove(connection, out _);
                    await connection.DisposeAsync().ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка канала связи {Pipe}", _pipeName);
                await Task.Delay(1000, ct).ConfigureAwait(false);
            }
            finally
            {
                if (pipe is not null)
                    await pipe.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task ServeClientAsync(LinkConnection connection, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && await connection.ReadAsync(ct).ConfigureAwait(false) is { } message)
            {
                if (message.Type == LinkMessage.RequestType)
                    Track(() => RespondAsync(connection, message, ct));
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or JsonException)
        {
            // клиент отключился или прислал мусор
        }
        finally
        {
            _clients.TryRemove(connection, out _);
            await connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task RespondAsync(LinkConnection connection, LinkMessage request, CancellationToken ct)
    {
        var response = new LinkMessage { Type = LinkMessage.ResponseType, Id = request.Id, Name = request.Name };
        try
        {
            ct.ThrowIfCancellationRequested();
            // Legacy clients omitted this field only in protocol v1.
            if ((request.ProtocolVersion ?? 1) != LinkProtocol.Version)
                throw new NotSupportedException($"Несовместимый протокол PdmLite: {request.ProtocolVersion ?? 1}; ожидается {LinkProtocol.Version}. Обновите Explorer и плагин Inventor.");
            if (request.Name is null || !_handlers.TryGetValue(request.Name, out var handler))
                throw new NotSupportedException($"Неизвестный запрос '{request.Name}'.");
            response.Payload = LinkMessage.ToPayload(await handler(request, ct).ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ошибка обработки запроса {Request}", request.Name);
            response.Ok = false;
            response.Error = ex.Message;
        }

        try
        {
            await connection.WriteAsync(response, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
        }
    }

    private bool Track(Func<Task> operation)
    {
        lock (_workGate)
        {
            if (_disposed != 0) return false;
            var task = Task.Run(operation);
            _work.Add(task);
            _ = task.ContinueWith(done => { _ = done.Exception; lock (_workGate) _work.Remove(done); },
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return true;
        }
    }

    /// <summary>Stops admission immediately; completion means every handler has actually finished.</summary>
    public Task StopAsync()
    {
        lock (_workGate)
        {
            if (_stopTask is not null) return _stopTask;
            Interlocked.Exchange(ref _disposed, 1);
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _stopTask = completion.Task;
            _ = DrainAsync(completion);
            return _stopTask;
        }
    }
    private async Task DrainAsync(TaskCompletionSource completion)
    {
        var errors = new List<Exception>();
        try { await _cts.CancelAsync().ConfigureAwait(false); } catch (Exception ex) { errors.Add(ex); }
        foreach (var client in _clients.Keys)
            try { await client.DisposeAsync().ConfigureAwait(false); } catch (Exception ex) { errors.Add(ex); }
        if (_acceptLoop is not null)
            try { await _acceptLoop.ConfigureAwait(false); } catch (Exception ex) { errors.Add(ex); }
        foreach (var client in _clients.Keys)
            try { await client.DisposeAsync().ConfigureAwait(false); } catch (Exception ex) { errors.Add(ex); }
        Task[] pending; lock (_workGate) pending = _work.ToArray();
        try { await Task.WhenAll(pending).ConfigureAwait(false); } catch (Exception ex) { errors.Add(ex); }
        _clients.Clear(); _cts.Dispose();
        if (errors.Count == 0) completion.TrySetResult();
        else completion.TrySetException(new AggregateException("Ошибки при остановке IPC после завершения запросов.", errors));
    }
    public async ValueTask DisposeAsync() => await StopAsync().WaitAsync(ShutdownTimeout).ConfigureAwait(false);
}
