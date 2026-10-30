using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PdmLite.Core.Documents;
using PdmLite.Core.Events;
using PdmLite.Data.Library;
using PdmLite.Library.Catalog;
using PdmLite.Library.Import;
using PdmLite.UI.Shared;

namespace PdmLite.Modules.Library.Import;

public sealed partial class ImportViewModel : ObservableObject
{
    public static readonly IReadOnlyList<string> Filters = ["Все", "Готовые", "С замечаниями", "С ошибками", "Исключённые"];

    private readonly LibraryImportService _service;
    private readonly ILogger<ImportViewModel> _logger;
    private readonly IEventBus _events;
    private ImportSession? _session;
    private bool _revalidating;

    public ImportViewModel(LibraryImportService service, IDocumentPropertyStore store, IEventBus events, ILogger<ImportViewModel> logger)
    {
        _service = service;
        _logger = logger;
        _events = events;
        View = CollectionViewSource.GetDefaultView(Rows);
        View.Filter = o => Matches((ImportRowViewModel)o);
        if (!store.IsAvailable)
            Banner = $"Свойства файлов Inventor недоступны: {store.UnavailableReason} Без них компоненты внести нельзя.";
    }

    public ObservableCollection<ImportRowViewModel> Rows { get; } = [];
    public ICollectionView View { get; }

    public LibraryCatalog? Catalog => _session?.Catalog;
    public IReadOnlyList<CategoryInfo> Categories => _session?.Catalog.Leaves.ToList() ?? [];
    public IReadOnlyList<string> KindNames => ImportRowViewModel.KindNames;
    public IReadOnlyList<string> FilterNames => Filters;

    public IReadOnlyList<SkippedFile> Skipped => _session?.Skipped ?? [];

    [ObservableProperty] private ImportRowViewModel? _selectedRow;
    [ObservableProperty] private string _filter = Filters[0];
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private string? _banner;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CommitCommand))]
    private bool _isBusy;

    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private double _progressValue;
    [ObservableProperty] private string _summary = "Добавьте файлы или папку с компонентами.";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CommitCommand))]
    private int _readyCount;

    partial void OnFilterChanged(string value) => RefreshViewWhenIdle();
    partial void OnSearchChanged(string value) => RefreshViewWhenIdle();

    /// <summary>
    /// Пересчёт фильтра. Во время правки ячейки таблица запрещает Refresh — откладываем
    /// до окончания редактирования.
    /// </summary>
    private void RefreshViewWhenIdle()
    {
        if (View is IEditableCollectionView { IsEditingItem: true } or IEditableCollectionView { IsAddingNew: true })
        {
            System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Background, RefreshViewWhenIdle);
            return;
        }
        View.Refresh();
    }

    private bool Matches(ImportRowViewModel row)
    {
        var byStatus = Filter switch
        {
            "Готовые" => row.Status == ImportStatus.Ready,
            "С замечаниями" => row.Status == ImportStatus.Warning,
            "С ошибками" => row.Status == ImportStatus.Error,
            "Исключённые" => row.Status == ImportStatus.Excluded,
            _ => true,
        };
        return byStatus && (Search.Length == 0
                            || row.FileName.Contains(Search, StringComparison.CurrentCultureIgnoreCase)
                            || row.Designation.Contains(Search, StringComparison.CurrentCultureIgnoreCase));
    }

    /// <summary>Анализ файлов и папок (первый раз — с загрузкой классификатора из базы).</summary>
    public async Task AddAsync(IReadOnlyList<string> inputs)
    {
        if (inputs.Count == 0)
            return;
        await RunAsync("Анализ файлов", async progress =>
        {
            if (_session is null)
            {
                _session = await _service.AnalyzeAsync(inputs, progress);
                OnPropertyChanged(nameof(Categories));
                OnPropertyChanged(nameof(HasSession));
            }
            else
            {
                await _service.AddAsync(_session, inputs, progress);
            }

            var known = Rows.Select(r => r.Candidate).ToHashSet();
            foreach (var candidate in _session.Candidates.Where(c => !known.Contains(c)))
                Rows.Add(new ImportRowViewModel(candidate, this));
        });
        RefreshAll();
    }

    /// <summary>Перепроверка после правки (вызывается строками).</summary>
    public void Revalidate()
    {
        if (_session is null || _revalidating)
            return;
        _revalidating = true;
        try
        {
            _service.Validate(_session);
            RefreshAll();
        }
        finally
        {
            _revalidating = false;
        }
    }

    public void SetInclude(IEnumerable<ImportRowViewModel> rows, bool include)
    {
        foreach (var row in rows.ToList())
            row.Candidate.Include = include;
        Revalidate();
    }

    [RelayCommand]
    private void Recheck() => Revalidate();

    public bool HasSession => _session is not null;

    /// <summary>Путь последнего сохранённого или открытого списка подготовки.</summary>
    [ObservableProperty] private string? _listPath;

    /// <summary>Обозначение выделенных строк — из другого свойства или имени файла. Возвращает число изменённых строк.</summary>
    public int SetSource(IEnumerable<ImportRowViewModel> rows, DesignationSource source)
    {
        if (_session is null)
            return 0;
        var changed = _service.SetSource(_session, rows.Select(r => r.Candidate).ToList(), source);
        RefreshAll();
        return changed;
    }

    /// <summary>Поиск и замена в обозначениях выделенных строк.</summary>
    public int ReplaceInDesignations(IEnumerable<ImportRowViewModel> rows, string find, string replace, bool isRegex)
    {
        if (_session is null)
            return 0;
        var changed = _service.ReplaceInDesignations(_session, rows.Select(r => r.Candidate).ToList(), find, replace, isRegex);
        RefreshAll();
        return changed;
    }

    /// <summary>Правка выделенных строк разом; <c>null</c> — поле не менять.</summary>
    public void BulkEdit(IEnumerable<ImportRowViewModel> rows, LibraryItemKind? kind, string? manufacturer, CategoryInfo? category)
    {
        foreach (var c in rows.Select(r => r.Candidate))
        {
            if (kind is { } k)
                c.Kind = k;
            if (manufacturer is not null)
                c.Manufacturer = manufacturer.Trim().Length == 0 ? null : manufacturer.Trim();
            if (category is not null)
                c.CategoryId = category.Id;
        }
        Revalidate();
    }

    /// <summary>После изменения правил: перечитать их и подготовить обозначения заново (кроме ручных).</summary>
    public async Task ReloadRulesAsync()
    {
        if (_session is null)
            return;
        await RunAsync("Применение правил", async _ => await _service.ReloadRulesAsync(_session));
        RefreshAll();
    }

    public void SaveList(string path)
    {
        if (_session is null)
            return;
        try
        {
            _service.SaveList(_session, path);
            ListPath = path;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Не удалось сохранить список {Path}", path);
            UiMessages.Error("Не удалось сохранить список.", ex);
        }
    }

    /// <summary>Открывает список подготовки вместо текущего.</summary>
    public async Task LoadListAsync(string path)
    {
        ImportSession? loaded = null;
        await RunAsync("Открытие списка", async progress => loaded = await _service.LoadListAsync(path, progress));
        if (loaded is null)
            return;

        _session = loaded;
        ListPath = path;
        Rows.Clear();
        foreach (var candidate in _session.Candidates)
            Rows.Add(new ImportRowViewModel(candidate, this));
        OnPropertyChanged(nameof(Categories));
        OnPropertyChanged(nameof(HasSession));
        RefreshAll();
    }

    [RelayCommand(CanExecute = nameof(CanCommit))]
    private async Task CommitAsync()
    {
        if (_session is null)
            return;
        var toImport = _session.Candidates.Where(c => c.CanImport).ToList();
        if (System.Windows.MessageBox.Show($"Внести в библиотеку компонентов: {toImport.Count}?", UiMessages.Caption,
                System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Question) != System.Windows.MessageBoxResult.OK)
            return;

        ImportResult? result = null;
        await RunAsync("Внесение в библиотеку", async progress => result = await _service.CommitAsync(_session, progress));
        if (result is null)
            return;
        if (result.Added > 0)
            _events.Publish(new LibraryChanged());

        // Внесённые убираем из списка; неудачные остаются с причиной.
        var failed = result.Failed.ToDictionary(f => f.Candidate, f => f.Error);
        foreach (var row in Rows.Where(r => toImport.Contains(r.Candidate) && !failed.ContainsKey(r.Candidate)).ToList())
        {
            Rows.Remove(row);
            _session.Candidates.Remove(row.Candidate);
        }
        Revalidate();
        foreach (var (candidate, error) in failed)
            candidate.Issues.Add(new ImportIssue(ImportSeverity.Error, $"Не внесено: {error}"));
        RefreshAll();

        if (failed.Count == 0)
            UiMessages.Info($"Внесено в библиотеку: {result.Added}.");
        else
            UiMessages.Error($"Внесено: {result.Added}. Не удалось внести: {failed.Count} — причины в списке.");
    }

    private bool CanCommit() => !IsBusy && ReadyCount > 0;

    private async Task RunAsync(string title, Func<IProgress<ImportProgress>, Task> work)
    {
        IsBusy = true;
        ProgressValue = 0;
        ProgressText = title + "…";
        var progress = new Progress<ImportProgress>(p =>
        {
            ProgressValue = p.Total == 0 ? 0 : 100.0 * p.Done / p.Total;
            ProgressText = p.Current is null ? title : $"{title}: {p.Done + 1} из {p.Total} — {p.Current}";
        });
        try
        {
            await work(progress);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{Title}: ошибка", title);
            UiMessages.Error($"{title}: ошибка.", ex);
        }
        finally
        {
            IsBusy = false;
            ProgressText = "";
        }
    }

    private void RefreshAll()
    {
        foreach (var row in Rows)
            row.Refresh();
        RefreshViewWhenIdle();

        if (_session is null)
            return;
        ReadyCount = _session.ReadyCount;
        var excluded = _session.Candidates.Count(c => !c.Include);
        Summary = $"Файлов: {_session.Candidates.Count} · можно внести: {_session.ReadyCount} (с замечаниями: {_session.WarningCount})"
                  + $" · с ошибками: {_session.ErrorCount}" + (excluded > 0 ? $" · исключены: {excluded}" : "")
                  + (_session.Skipped.Count > 0 ? $" · пропущено: {_session.Skipped.Count}" : "");
        OnPropertyChanged(nameof(Skipped));
    }
}
