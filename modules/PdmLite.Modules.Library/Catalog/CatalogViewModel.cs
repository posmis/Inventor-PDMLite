using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PdmLite.Core.Events;
using PdmLite.Data.Library;
using PdmLite.Library.Catalog;
using PdmLite.Library.Items;
using PdmLite.UI.Shared;

namespace PdmLite.Modules.Library.Catalog;

public sealed partial class CatalogViewModel : ObservableObject
{
    public const string AllCategories = "Все разделы";

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

    [ObservableProperty] private IReadOnlyList<string> _categoryFilters = [AllCategories];
    [ObservableProperty] private string _categoryFilter = AllCategories;
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private CatalogRow? _selected;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _summary = "";

    partial void OnSearchChanged(string value) => RefreshView();
    partial void OnCategoryFilterChanged(string value) => RefreshView();

    private bool Matches(CatalogRow row) =>
        (CategoryFilter == AllCategories || row.Item.CategoryPath == CategoryFilter)
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

            CategoryFilters = [AllCategories, .. items.Select(i => i.CategoryPath).Distinct().Order(StringComparer.CurrentCulture)];
            if (!CategoryFilters.Contains(CategoryFilter))
                CategoryFilter = AllCategories;
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

    private void RefreshView()
    {
        View.Refresh();
        var shown = Items.Count(Matches);
        Summary = shown == Items.Count ? $"Компонентов: {Items.Count}" : $"Показано: {shown} из {Items.Count}";
    }
}

public sealed record CatalogRow(LibraryItemSummary Item)
{
    public string KindName => Item.Kind == LibraryItemKind.Standard ? "Стандартное" : "Покупное";
    public string Path => Item.RelativePath;
    public string Modified => $"{Item.ModifiedAt.ToLocalTime():g}, {Item.ModifiedBy}";
}
