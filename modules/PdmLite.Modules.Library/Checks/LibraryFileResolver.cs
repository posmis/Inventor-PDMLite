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
/// (изменили обозначение, стандарт или раздел; перенесли папку библиотеки). Индекс путей держится
/// в памяти и обновляется после изменений библиотеки.
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
    private static readonly TimeSpan LoadTimeout = TimeSpan.FromSeconds(3);

    private volatile LibraryPathIndex? _index;
    private DateTime _loadedAt;
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

        var library = storage();
        bool missing;
        if (libraryName is not null)
        {
            // Ссылка внутри библиотеки проекта. Наша — проверим, есть ли файл; чужая — не знаем.
            var ours = string.Equals(libraryName, LibraryPanelViewModel.InventorLibraryName, StringComparison.OrdinalIgnoreCase);
            if (ours && File.Exists(library.GetFullPath(reference)))
                return null;
            missing = ours;
        }
        else if (Path.IsPathRooted(reference))
        {
            if (File.Exists(reference))
                return null;
            missing = true;
        }
        else
        {
            missing = false; // путь относительно проекта — проверить не можем, ищем только по старым путям
        }

        var relative = Index(reload: false)?.Find(reference, missing);
        if (relative is null && missing && DateTime.UtcNow - _loadedAt > MaxAge)
            relative = Index(reload: true)?.Find(reference, missing);
        if (relative is null)
            return null;

        var full = library.GetFullPath(relative);
        return File.Exists(full) ? full : null;
    }

    private LibraryPathIndex? Index(bool reload)
    {
        if (_index is { } index && !reload)
            return index;
        // Вызов из события Inventor — ждём недолго; не успели — Inventor покажет своё окно.
        SyncBridge.Run(RefreshAsync, LoadTimeout);
        return _index;
    }

    private async Task RefreshAsync()
    {
        if (!database.IsConfigured)
            return;
        try
        {
            _index = await items.LoadPathIndexAsync().ConfigureAwait(false);
            _loadedAt = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Не удалось загрузить пути компонентов библиотеки");
        }
    }

    public void Dispose() => _subscription?.Dispose();
}
