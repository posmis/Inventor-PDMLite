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
    ILogger<LibraryFileResolver> logger) : IMissingFileResolver, IDisposable, IAsyncDisposable
{
    /// <summary>Каталог могли изменить в Explorer (другой процесс) — при промахе индекс старше этого перечитывается.</summary>
    private static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(20);

    private readonly LibraryPathCache _cache = new(
        ct => items.LoadPathIndexAsync(ct),
        ex => logger.LogWarning(ex, "Не удалось загрузить пути компонентов библиотеки"));
    private IDisposable? _subscription;

    public void Attach()
    {
        _subscription = events.Subscribe<LibraryChanged>(_ => RefreshAsync().Forget());
        RefreshAsync().Forget();
    }

    private Task RefreshAsync() => database.IsConfigured ? _cache.RefreshAsync() : Task.CompletedTask;

    public string? Resolve(string reference, string? libraryName)
    {
        if (!database.IsConfigured)
            return null;

        var root = storage().Root;
        var ours = LibraryPanelViewModel.InventorLibraryName;
        var index = _cache.Get(reload: false);
        if (index is null)
            return null;

        var found = LibraryReferenceResolver.Resolve(index, root, reference, libraryName, ours, File.Exists);
        if (found is null && LibraryReferenceResolver.ShouldReload(reference, libraryName, ours) && _cache.Age > MaxAge
            && _cache.Get(reload: true) is { } fresh && !ReferenceEquals(fresh, index))
            found = LibraryReferenceResolver.Resolve(fresh, root, reference, libraryName, ours, File.Exists);
        return found;
    }

    public void Dispose()
    {
        _subscription?.Dispose();
        _cache.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        Dispose();
        await _cache.DisposeAsync().ConfigureAwait(false);
    }
}
