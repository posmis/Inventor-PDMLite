using PdmLite.Core.Threading;
using PdmLite.Library.Items;

namespace PdmLite.Modules.Library.Checks;

/// <summary>Одна загрузка за раз; изменения во время загрузки требуют нового снимка.</summary>
internal sealed class LibraryPathCache(
    Func<CancellationToken, Task<LibraryPathIndex>> load,
    Action<Exception> reportError,
    Func<DateTime>? clock = null) : IDisposable, IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Func<DateTime> _now = clock ?? (() => DateTime.UtcNow);
    private LibraryPathIndex? _index;
    private DateTime _loadedAt = DateTime.MinValue;
    private DateTime _failedAt = DateTime.MinValue;
    private Task? _pending;
    private long _requested;
    private bool _disposed;
    private Task? _disposeTask;

    public TimeSpan Age { get { lock (_gate) return _now() - _loadedAt; } }
    internal LibraryPathIndex? Current { get { lock (_gate) return _index; } }

    public LibraryPathIndex? Get(bool reload)
    {
        Task pending;
        lock (_gate)
        {
            if (_disposed || (_index is not null && !reload) || _now() - _failedAt < TimeSpan.FromMinutes(1))
                return _index;
            // Ожидание уже начатой загрузки не означает ещё одного изменения библиотеки.
            pending = _pending ?? Start();
        }
        if (!SyncBridge.Run(() => pending, TimeSpan.FromSeconds(3)))
        {
            lock (_gate)
                // Не затрагиваем время успешно завершившегося обновления.
                if (ReferenceEquals(_pending, pending)) _failedAt = _now();
        }
        return Current;
    }

    public Task RefreshAsync()
    {
        lock (_gate)
        {
            if (_disposed) return Task.CompletedTask;
            _requested++;
            return _pending ?? Start();
        }
    }

    // Вызывается под _gate. Пул потоков не захватывает контекст интерфейса Inventor.
    private Task Start() => _pending = Task.Run(RunAsync);

    private async Task RunAsync()
    {
        while (true)
        {
            long version;
            lock (_gate)
            {
                if (_disposed) { _pending = null; return; }
                version = _requested;
            }
            LibraryPathIndex? index = null;
            Exception? error = null;
            try { index = await load(_stop.Token).ConfigureAwait(false); }
            catch (Exception ex) { error = ex; }
            lock (_gate)
            {
                if (_disposed) { _pending = null; return; }
                // Ни результат, ни ошибка устаревшей загрузки не меняют состояние кэша.
                if (version != _requested) continue;
                if (error is null)
                {
                    _index = index;
                    _loadedAt = _now();
                    _failedAt = DateTime.MinValue;
                }
                else _failedAt = _now();
                _pending = null;
            }
            if (error is not null) reportError(error);
            return;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            var pending = _pending;
            _disposeTask = Task.Run(async () =>
            {
                _stop.Cancel();
                try
                {
                    if (pending is not null) await pending.ConfigureAwait(false);
                }
                finally { _stop.Dispose(); }
            });
        }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        lock (_gate) return new ValueTask(_disposeTask!);
    }
}
