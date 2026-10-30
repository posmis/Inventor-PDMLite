using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PdmLite.Core.Events;
using PdmLite.Core.Naming;
using PdmLite.Data.Library;
using PdmLite.Library.Catalog;
using PdmLite.Library.Items;
using PdmLite.UI.Shared;

namespace PdmLite.Modules.Library.Catalog;

/// <summary>
/// Каталог библиотеки как проводник: слева дерево (раздел → группа → стандарт или производитель),
/// справа — компоненты выбранной папки со всеми вложенными.
/// </summary>
public sealed partial class CatalogViewModel : ObservableObject
{
    public const string AllFolders = "Вся библиотека";

    private readonly LibraryItemService _service;
    private readonly ILogger<CatalogViewModel> _logger;
    private readonly IEventBus _events;

    public CatalogViewModel(LibraryItemService service, IEventBus events, ILogger<CatalogViewModel> logger)
    {
        _service = service;
        _logger = logger;
        _events = events;
        View = CollectionViewSource.GetDefaultView(Items);
        View.Filter = o => Matches((CatalogRow)o);
    }

    public ObservableCollection<CatalogRow> Items { get; } = [];
    public ICollectionView View { get; }
    public LibraryCatalog? Catalog { get; private set; }
    public LibraryItemService Service => _service;

    /// <summary>Дерево папок: один корень «Вся библиотека».</summary>
    public ObservableCollection<CatalogFolder> Folders { get; } = [];

    [ObservableProperty] private CatalogFolder? _folder;
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private CatalogRow? _selected;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _summary = "";

    partial void OnSearchChanged(string value) => RefreshView();
    partial void OnFolderChanged(CatalogFolder? value) => RefreshView();

    private bool Matches(CatalogRow row) =>
        (Folder is null || Folder.Contains(row.Folder))
        && (Search.Length == 0
            || row.Item.Designation.Contains(Search, StringComparison.CurrentCultureIgnoreCase)
            || (row.Item.Manufacturer?.Contains(Search, StringComparison.CurrentCultureIgnoreCase) ?? false));

    [RelayCommand]
    public async Task LoadAsync()
    {
        IsBusy = true;
        try
        {
            var selectedId = Selected?.Item.Id;
            Catalog = await _service.LoadCatalogAsync();
            var items = await _service.ListAsync(Catalog);

            Items.Clear();
            foreach (var item in items)
                Items.Add(new CatalogRow(item));

            BuildFolders(Catalog, Items);
            Selected = Items.FirstOrDefault(r => r.Item.Id == selectedId);
            RefreshView();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Не удалось загрузить каталог библиотеки");
            UiMessages.Error("Не удалось загрузить каталог библиотеки.", ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task DeleteAsync(CatalogRow row)
    {
        if (System.Windows.MessageBox.Show($"Удалить из библиотеки «{row.Item.Designation}»?\n\nФайл будет удалён из папки библиотеки.",
                UiMessages.Caption, System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Warning)
            != System.Windows.MessageBoxResult.OK)
            return;
        try
        {
            await _service.DeleteAsync(row.Item.Id);
            _events.Publish(new LibraryChanged());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Не удалось удалить {Designation}", row.Item.Designation);
            UiMessages.Error("Не удалось удалить компонент.", ex);
        }
        await LoadAsync();
    }

    /// <summary>Карточка изменена — перечитать список и сообщить панелям библиотеки.</summary>
    public async Task OnEditedAsync()
    {
        _events.Publish(new LibraryChanged());
        await LoadAsync();
    }

    /// <summary>
    /// Дерево: все разделы и группы классификатора (и пустые — видно, куда класть), в группах —
    /// папки стандартов и производителей, как в папке библиотеки. Выбранная папка сохраняется.
    /// </summary>
    private void BuildFolders(LibraryCatalog catalog, IEnumerable<CatalogRow> rows)
    {
        var selectedPath = Folder?.Path ?? "";
        var expanded = Folders.SelectMany(f => f.Descendants()).Where(f => f.IsExpanded).Select(f => f.Path).ToHashSet();

        var root = new CatalogFolder(AllFolders, "") { IsExpanded = true };
        foreach (var category in catalog.Categories)
        {
            var node = root;
            foreach (var part in category.PathParts)
                node = node.GetOrAdd(part);
        }
        foreach (var row in rows)
        {
            var node = root;
            foreach (var part in row.Folder.Split('/'))
                node = node.GetOrAdd(part);
        }

        foreach (var folder in root.Descendants())
        {
            folder.Count = Items.Count(r => folder.Contains(r.Folder));
            if (expanded.Contains(folder.Path))
                folder.IsExpanded = true;
        }

        Folders.Clear();
        Folders.Add(root);
        var selected = root.Descendants().FirstOrDefault(f => f.Path == selectedPath) ?? root;
        selected.IsSelected = true;
        Folder = selected;
    }

    private void RefreshView()
    {
        View.Refresh();
        var shown = Items.Count(Matches);
        var where = Folder is null || Folder.Path.Length == 0 ? "" : $"«{Folder.Path}»: ";
        Summary = shown == Items.Count ? $"Компонентов: {Items.Count}" : $"{where}показано {shown} из {Items.Count}";
    }
}

public sealed record CatalogRow(LibraryItemSummary Item)
{
    /// <summary>Папка компонента в дереве: раздел/группа/стандарт (или производитель).</summary>
    public string Folder { get; } = $"{Item.CategoryPath}/{(Item.Kind == LibraryItemKind.Standard
        ? Item.Standard ?? "Без стандарта"
        : Item.Manufacturer ?? PdmLite.Library.Storage.LibraryStorage.NoManufacturerFolder)}";

    public string KindName => Item.Kind == LibraryItemKind.Standard ? "Стандартное" : "Покупное";
    public string Path => Item.RelativePath;
    public string Modified => $"{Item.ModifiedAt.ToLocalTime():g}, {Item.ModifiedBy}";
}
