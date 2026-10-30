using Microsoft.Extensions.Logging;
using PdmLite.Core.Documents;
using PdmLite.Core.Events;
using PdmLite.Core.Threading;
using PdmLite.Data;
using PdmLite.Library.Items;
using PdmLite.Library.Storage;
using PdmLite.Modules.Library.Panel;

namespace PdmLite.Modules.Library.Checks;

/// <summary>
/// Находит компонент библиотеки, на который ссылается сборка, если файла нет по старому пути
/// (изменили обозначение, стандарт или раздел; объединили дубликаты; перенесли папку библиотеки).
/// Индекс путей держится в памяти и обновляется после изменений библиотеки.
/// </summary>
public sealed class LibraryFileResolver(
    LibraryItemService items,
    Func<LibraryStorage> storage,
    IPdmDatabase database,
    IEventBus events,
    ILogger<LibraryFileResolver> logger) : IMissingFileResolver, IDisposable
{
    /// <summary>Каталог могли изменить в Explorer (другой процесс) — при промахе индекс старше этого перечитывается.</summary>
    private static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Не удалось загрузить (база недоступна) — следующая попытка не раньше чем через это время.
    /// Иначе открытие сборки ждало бы загрузку на каждом файле.
    /// </summary>
    private static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan LoadTimeout = TimeSpan.FromSeconds(3);

    private readonly object _gate = new();
    private LibraryPathIndex? _index;
    private DateTime _loadedAt = DateTime.MinValue;
    private DateTime _failedAt = DateTime.MinValue;
    private IDisposable? _subscription;

    public void Attach()
    {
        _subscription = events.Subscribe<LibraryChanged>(_ => RefreshAsync().Forget());
        RefreshAsync().Forget();
    }

    public string? Resolve(string reference, string? libraryName)
    {
        if (!database.IsConfigured)
            return null;

        var root = storage().Root;
        var ours = LibraryPanelViewModel.InventorLibraryName;
        var index = Index(reload: false);
        if (index is null)
            return null;

        var found = LibraryReferenceResolver.Resolve(index, root, reference, libraryName, ours, File.Exists);
        if (found is null && LibraryReferenceResolver.ShouldReload(reference, libraryName, ours) && Age > MaxAge
            && Index(reload: true) is { } fresh && !ReferenceEquals(fresh, index))
            found = LibraryReferenceResolver.Resolve(fresh, root, reference, libraryName, ours, File.Exists);
        return found;
    }

    private TimeSpan Age
    {
        get
        {
            lock (_gate)
                return DateTime.UtcNow - _loadedAt;
        }
    }

    /// <summary>Индекс; при необходимости загружается синхронно (вызов из события Inventor — ждём недолго).</summary>
    private LibraryPathIndex? Index(bool reload)
    {
        lock (_gate)
        {
            if (_index is { } index && !reload)
                return index;
            if (DateTime.UtcNow - _failedAt < RetryAfter)
                return _index;
        }
        var completed = SyncBridge.Run(RefreshAsync, LoadTimeout);
        lock (_gate)
        {
            if (!completed)
                _failedAt = DateTime.UtcNow; // не дождались — загрузка продолжится в фоне, остальные файлы не ждут
            return _index;
        }
    }

    private async Task RefreshAsync()
    {
        if (!database.IsConfigured)
            return;
        try
        {
            var index = await items.LoadPathIndexAsync().ConfigureAwait(false);
            lock (_gate)
            {
                _index = index;
                _loadedAt = DateTime.UtcNow;
                _failedAt = DateTime.MinValue;
            }
        }
        catch (Exception ex)
        {
            lock (_gate)
                _failedAt = DateTime.UtcNow;
            logger.LogWarning(ex, "Не удалось загрузить пути компонентов библиотеки");
        }
    }

    public void Dispose() => _subscription?.Dispose();
}
