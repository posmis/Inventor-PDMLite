using CommunityToolkit.Mvvm.ComponentModel;
using PdmLite.Data.Library;
using PdmLite.Library.Catalog;
using PdmLite.Library.Import;
using PdmLite.Library.Items;
using PdmLite.Modules.Library.Import;

namespace PdmLite.Modules.Library.Catalog;

/// <summary>Карточка компонента: правка с проверкой «на лету».</summary>
public sealed partial class EditItemViewModel : ObservableObject
{
    private readonly LibraryItemService _service;
    private readonly LibraryCatalog _catalog;
    private readonly LibraryItemSummary _item;
    private int _checkVersion;

    public EditItemViewModel(LibraryItemService service, LibraryCatalog catalog, LibraryItemSummary item)
    {
        _service = service;
        _catalog = catalog;
        _item = item;
        _designation = item.Designation;
        _kindName = item.Kind == LibraryItemKind.Standard ? ImportRowViewModel.KindNames[0] : ImportRowViewModel.KindNames[1];
        _manufacturer = item.Manufacturer ?? "";
        _category = catalog.Get(item.CategoryId);
        _standardIsManual = item.StandardIsManual;
        _manualStandard = item.StandardIsManual ? item.Standard ?? "" : "";
        _standard = item.Standard;
        _ = CheckAsync();
    }

    public string CurrentDesignation => _item.Designation;
    public string CurrentPath => _item.RelativePath;
    public IReadOnlyList<string> KindNames => ImportRowViewModel.KindNames;
    public IReadOnlyList<CategoryInfo> Categories => _catalog.Leaves.ToList();

    [ObservableProperty] private string _designation;
    [ObservableProperty] private string _kindName;
    [ObservableProperty] private string _manufacturer;
    [ObservableProperty] private CategoryInfo? _category;

    /// <summary>Стандарт указан вручную (если распознан из обозначения неверно).</summary>
    [ObservableProperty] private bool _standardIsManual;
    [ObservableProperty] private string _manualStandard;

    [ObservableProperty] private IReadOnlyList<IssueViewModel> _issues = [];
    [ObservableProperty] private string? _newPath;
    [ObservableProperty] private string? _standard;
    [ObservableProperty] private bool _canSave;
    [ObservableProperty] private bool _isBusy;

    private LibraryItemKind Kind => KindName == ImportRowViewModel.KindNames[1] ? LibraryItemKind.Purchased : LibraryItemKind.Standard;

    private LibraryItemEdit Edit => new(Designation, Kind, Manufacturer, Category?.Id, StandardIsManual ? ManualStandard : null);

    partial void OnDesignationChanged(string value) => _ = CheckAsync();
    partial void OnKindNameChanged(string value) => _ = CheckAsync();
    partial void OnManufacturerChanged(string value) => _ = CheckAsync();
    partial void OnCategoryChanged(CategoryInfo? value) => _ = CheckAsync();
    partial void OnManualStandardChanged(string value) => _ = CheckAsync();

    partial void OnStandardIsManualChanged(bool value)
    {
        if (value && ManualStandard.Length == 0)
            ManualStandard = Standard ?? ""; // начать правку с распознанного
        _ = CheckAsync();
    }

    private async Task CheckAsync()
    {
        var version = ++_checkVersion;
        CanSave = false;
        try
        {
            var check = await _service.CheckEditAsync(_catalog, _item.Id, Edit);
            if (version != _checkVersion)
                return; // пока проверяли, пользователь продолжил печатать
            Issues = check.Issues.OrderByDescending(i => i.Severity).Select(i => new IssueViewModel(i)).ToList();
            NewPath = check.NewRelativePath;
            Standard = check.Standard;
            CanSave = !check.HasErrors && HasChanges(check);
        }
        catch (Exception ex)
        {
            Issues = [new IssueViewModel(new ImportIssue(ImportSeverity.Error, ex.Message))];
        }
    }

    private bool HasChanges(LibraryItemEditCheck check) =>
        check.CanonicalDesignation != _item.Designation
        || Kind != _item.Kind
        || (string.IsNullOrWhiteSpace(Manufacturer) ? null : Manufacturer.Trim()) != _item.Manufacturer
        || Category?.Id != _item.CategoryId
        || check.Standard != _item.Standard
        || (StandardIsManual && Kind == LibraryItemKind.Standard) != _item.StandardIsManual;

    /// <summary>Сохраняет; <c>null</c> — успешно, иначе текст ошибки.</summary>
    public async Task<string?> SaveAsync()
    {
        IsBusy = true;
        try
        {
            await _service.ApplyEditAsync(_catalog, _item.Id, Edit);
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
