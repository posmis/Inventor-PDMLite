using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdmLite.Core.UI;
using PdmLite.Library.Backup;
using PdmLite.Library.Catalog;
using PdmLite.Library.Items;
using PdmLite.UI.Shared;

namespace PdmLite.Modules.Library.Catalog;

/// <summary>
/// Обслуживание библиотеки. Каждая операция в три шага: «Проверить/Найти» (только чтение, таблица
/// изменений) → отметки → окно со всем списком изменений и предупреждением → запись.
/// </summary>
public sealed partial class MaintenanceViewModel : ObservableObject
{
    private readonly LibraryItemService _service;
    private readonly LibraryCatalog _catalog;
    private readonly IReadOnlyCollection<Guid> _selectedIds;
    private CancellationTokenSource? _cts;

    private readonly LibraryBackupService? _backup;

    public MaintenanceViewModel(LibraryItemService service, LibraryCatalog catalog, IReadOnlyCollection<Guid> selectedIds, int totalCount,
        LibraryBackupService? backup = null)
    {
        _service = service;
        _backup = backup;
        _catalog = catalog;
        _selectedIds = selectedIds;
        TotalCount = totalCount;
        _onlySelected = selectedIds.Count > 0;

        // Изменения — по строке на свойство, сгруппированы по файлу компонента.
        Changes = new ListCollectionView(ChangeRows);
        Changes.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ChangeRow.Owner)));
        Duplicates = new ListCollectionView(DuplicateRows);
        Duplicates.GroupDescriptions.Add(new PropertyGroupDescription(nameof(DuplicateRow.Owner)));
    }

    /// <summary>Показ окна подтверждения (задаёт окно обслуживания).</summary>
    public Func<ConfirmChangesWindow, bool> Ask { get; set; } = w => w.ShowDialog() == true;

    public int SelectedCount => _selectedIds.Count;
    public int TotalCount { get; }
    public bool HasSelection => _selectedIds.Count > 0;

    [ObservableProperty] private bool _onlySelected;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckCommand), nameof(ApplyCommand), nameof(StopCommand), nameof(FindDuplicatesCommand), nameof(MergeCommand))]
    private bool _isRunning;

    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _progressText = "";

    /// <summary>Файлы изменялись — каталог стоит перечитать.</summary>
    public bool Changed { get; private set; }

    // ---------- Свойства в файлах ----------

    public ObservableCollection<ChangeRow> ChangeRows { get; } = [];
    public ICollectionView Changes { get; }
    private readonly List<DiffRow> _diffs = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    private int _toApply;

    [ObservableProperty] private string _summary = "Нажмите «Проверить»: файлы будут только прочитаны, ничего не изменится.";

    private bool CanCheck() => !IsRunning;

    /// <summary>Шаг 1: только чтение.</summary>
    [RelayCommand(CanExecute = nameof(CanCheck))]
    private Task CheckAsync() => RunAsync("Проверка", async (progress, ct) =>
    {
        var diffs = await _service.DiagnosePropertiesAsync(_catalog, OnlySelected ? _selectedIds : null, progress, ct);
        ChangeRows.Clear();
        _diffs.Clear();
        foreach (var diff in diffs.Where(d => d.NeedsUpdate || d.Error is not null))
        {
            var owner = new DiffRow(diff, UpdateCount);
            _diffs.Add(owner);
            if (diff.Error is not null)
                ChangeRows.Add(new ChangeRow(owner, "Файл не прочитан", diff.Error, ""));
            else
                foreach (var change in diff.Changes)
                    ChangeRows.Add(new ChangeRow(owner, change.Property, Show(change.Current), Show(change.Expected)));
        }
        UpdateCount();

        var needs = diffs.Count(d => d.NeedsUpdate);
        var errors = diffs.Count(d => d.Error is not null);
        Summary = $"Проверено файлов: {diffs.Count}. В порядке: {diffs.Count - needs - errors}. Нужно обновить: {needs}"
                  + $" (изменений свойств: {diffs.Where(d => d.NeedsUpdate).Sum(d => d.Changes.Count)})."
                  + (errors > 0 ? $" Не прочитаны: {errors}." : "")
                  + (needs > 0 ? " Ниже — каждое изменение: свойство, что сейчас в файле и что будет записано." : "");
    });

    private bool CanApply() => !IsRunning && ToApply > 0;

    /// <summary>Шаг 2: окно со всеми изменениями, затем запись — только отмеченные файлы.</summary>
    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task ApplyAsync()
    {
        var chosen = _diffs.Where(d => d.Apply).ToList();
        var lines = chosen.SelectMany(d => d.Diff.Changes.Select(c =>
            new ChangeLine($"{d.Designation}\n{d.Path}", c.Property, Show(c.Current), Show(c.Expected)))).ToList();
        var totals = string.Join("; ", chosen.SelectMany(d => d.Diff.Changes).GroupBy(c => c.Property)
            .Select(g => $"{g.Key} — в {Files(g.Count())}"));

        var confirm = new ConfirmChangesWindow("Обслуживание: подтверждение записи",
            $"Будут изменены свойства в {Files(chosen.Count)} библиотеки ({lines.Count} изменений).",
            totals,
            "Сначала делается резервная копия всей библиотеки. Перед записью каждый файл проверяется ещё раз (если уже в порядке — не изменяется) и копируется; при ошибке " +
            "файл восстанавливается из копии. Остановить можно в любой момент — обработанные файлы останутся обновлёнными.",
            "Записать в файлы", ("Компонент", "Свойство", "Сейчас в файле", "Будет записано"), lines);
        if (!Ask(confirm))
            return;

        if (!await BackupAsync(BackupReason.BeforeMaintenance))
            return;

        RewriteResult? result = null;
        await RunAsync("Запись свойств", async (progress, ct) =>
            result = await _service.RewritePropertiesAsync(_catalog, chosen.Select(d => d.Diff.ItemId).ToList(), progress, ct));
        Changed = true;
        if (result is not null)
        {
            var text = $"Обновлено файлов: {result.Updated}. Уже были в порядке: {result.UpToDate}.";
            if (result.Failed.Count > 0)
                UiMessages.Error(text + $"\n\nНе удалось ({result.Failed.Count}):\n" + string.Join("\n", result.Failed.Take(20)));
            else
                UiMessages.Info(text);
        }

        // Повторная проверка: в списке остаётся только то, что ещё не в порядке.
        await CheckAsync();
    }

    private void UpdateCount() => ToApply = _diffs.Count(d => d.Apply);

    /// <summary>Отметить все файлы, которые можно обновить, или снять все отметки.</summary>
    [RelayCommand]
    private void CheckAllFiles(bool apply)
    {
        foreach (var diff in _diffs.Where(d => d.CanApply))
            diff.Apply = apply;
    }

    /// <summary>Таблица изменений — в буфер обмена (для отчёта): компонент, свойство, сейчас, станет.</summary>
    [RelayCommand]
    private void CopyChanges() => UiClipboard.SetText(string.Join(Environment.NewLine,
        ChangeRows.Select(r => $"{r.Owner.Designation}\t{r.Property}\t{r.Current}\t{r.Expected}")));

    /// <summary>Строка таблицы дубликатов, выделенная пользователем.</summary>
    [ObservableProperty] private DuplicateRow? _selectedDuplicate;

    /// <summary>Оставить выделенный компонент в его группе (остальные группы будут удалены при объединении).</summary>
    [RelayCommand]
    private void KeepSelected()
    {
        if (SelectedDuplicate is { } row)
        {
            row.Keep = true;
            row.Owner.Merge = true;
        }
    }

    [RelayCommand]
    private void CheckAllGroups(bool merge)
    {
        foreach (var group in _groups)
            group.Merge = merge;
    }

    // ---------- Дубликаты ----------

    public ObservableCollection<DuplicateRow> DuplicateRows { get; } = [];
    public ICollectionView Duplicates { get; }
    private readonly List<DuplicateGroupRow> _groups = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MergeCommand))]
    private int _toMerge;

    [ObservableProperty] private string _duplicatesSummary =
        "Нажмите «Найти дубликаты»: ищутся компоненты с одинаковым файлом и с обозначениями, которые отличаются только " +
        "пробелами, знаками или регистром. Ничего не изменится, пока вы не подтвердите объединение.";

    [RelayCommand(CanExecute = nameof(CanCheck))]
    private Task FindDuplicatesAsync() => RunAsync("Поиск дубликатов", async (_, ct) =>
    {
        var groups = await Task.Run(() => _service.FindDuplicatesAsync(_catalog, ct), ct);
        ct.ThrowIfCancellationRequested();
        DuplicateRows.Clear();
        _groups.Clear();
        foreach (var group in groups)
        {
            var owner = new DuplicateGroupRow(group, UpdateMergeCount);
            _groups.Add(owner);
            foreach (var row in owner.Rows)
                DuplicateRows.Add(row);
        }
        UpdateMergeCount();
        DuplicatesSummary = groups.Count == 0
            ? "Дубликатов не найдено."
            : $"Найдено групп: {groups.Count}. В каждой выберите, какой компонент оставить (по умолчанию — тот, что стоит в сборках " +
              "библиотеки, иначе самый ранний), остальные будут удалены, а сборки библиотеки перевязаны на оставленный.";
    });

    private bool CanMerge() => !IsRunning && ToMerge > 0;

    [RelayCommand(CanExecute = nameof(CanMerge))]
    private async Task MergeAsync()
    {
        var chosen = _groups.Where(g => g.Merge).ToList();
        var lines = new List<ChangeLine>();
        foreach (var group in chosen)
        {
            var keep = group.Kept;
            foreach (var row in group.Rows.Where(r => r != keep))
                lines.Add(new ChangeLine($"{row.Designation}\n{row.Path}",
                    row.UsedIn > 0 ? $"Удалить; перевязать сборок: {row.UsedIn}" : "Удалить",
                    row.Designation, $"{keep.Designation}\n{keep.Path}"));
        }

        var confirm = new ConfirmChangesWindow("Обслуживание: объединение дубликатов",
            $"Будет удалено компонентов: {lines.Count} (групп: {chosen.Count}).",
            "Сборки библиотеки, где стоит удаляемый компонент, будут перевязаны на оставляемый.",
            "Сначала делается резервная копия всей библиотеки — из неё можно вернуть удалённое. Сборки проектов не изменяются: " +
            "при открытии в Inventor ссылка на удалённый файл будет подменена оставленным компонентом — после этого сохраните сборку.",
            "Объединить", ("Удаляется", "Действие", "Было", "Вместо него — оставляемый"), lines);
        if (!Ask(confirm))
            return;

        if (!await BackupAsync(BackupReason.BeforeMerge))
            return;

        var failed = new List<string>();
        await RunAsync("Объединение", async (progress, ct) =>
        {
            for (var i = 0; i < chosen.Count; i++)
            {
                var group = chosen[i];
                progress.Report((i, chosen.Count, group.Kept.Designation));
                try
                {
                    await _service.MergeDuplicatesAsync(group.Kept.Entry.Item.Id,
                        group.Rows.Where(r => r != group.Kept).Select(r => r.Entry.Item.Id).ToList(), ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failed.Add($"{group.Kept.Designation}: {ex.Message}");
                }
            }
        });
        Changed = true;
        if (failed.Count > 0)
            UiMessages.Error("Не удалось объединить:\n" + string.Join("\n", failed));
        else
            UiMessages.Info($"Объединено групп: {chosen.Count}.");
        await FindDuplicatesAsync();
    }

    private void UpdateMergeCount() => ToMerge = _groups.Count(g => g.Merge);

    // ---------- Общее ----------

    /// <summary>Резервная копия библиотеки перед изменением. Не удалась — операция не выполняется.</summary>
    private async Task<bool> BackupAsync(BackupReason reason)
    {
        if (_backup is null)
            return true;
        var done = false;
        await RunAsync("Резервная копия библиотеки", async (progress, ct) =>
        {
            await _backup.CreateAsync(reason, progress, ct);
            done = true;
        });
        // Сообщение об ошибке уже показал RunAsync; здесь — только что операция не выполнена.
        if (!done)
            ProgressText = "Резервная копия не сделана — файлы не изменялись. Проверьте папку копий (Резервные копии… → настройки).";
        return done;
    }

    private bool CanStop() => IsRunning;

    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop() => _cts?.Cancel();

    private async Task RunAsync(string title, Func<IProgress<(int Done, int Total, string? Current)>, CancellationToken, Task> work)
    {
        IsRunning = true;
        _cts = new CancellationTokenSource();
        var progress = new Progress<(int Done, int Total, string? Current)>(p =>
        {
            Progress = p.Total == 0 ? 100 : 100.0 * p.Done / p.Total;
            ProgressText = p.Current is null ? "" : $"{title}: {p.Done + 1} из {p.Total} — {p.Current}";
        });
        try
        {
            await work(progress, _cts.Token);
        }
        catch (OperationCanceledException)
        {
            ProgressText = $"{title}: остановлено.";
        }
        catch (Exception ex)
        {
            UiMessages.Error($"{title}: ошибка.", ex);
        }
        finally
        {
            IsRunning = false;
            Progress = 0;
            if (!ProgressText.EndsWith("остановлено."))
                ProgressText = "";
            _cts.Dispose();
            _cts = null;
        }
    }

    private static string Show(string? value) => string.IsNullOrEmpty(value) ? "(нет)" : value;

    private static string Files(int count) => count % 10 == 1 && count % 100 != 11 ? $"{count} файле" : $"{count} файлах";
}

/// <summary>Файл компонента: отметка «обновить».</summary>
public sealed partial class DiffRow : ObservableObject
{
    private readonly Action _changed;

    public DiffRow(PropertyDiff diff, Action changed)
    {
        Diff = diff;
        _changed = changed;
        _apply = diff.NeedsUpdate;
    }

    public PropertyDiff Diff { get; }

    [ObservableProperty] private bool _apply;

    partial void OnApplyChanged(bool value) => _changed();

    public bool CanApply => Diff.NeedsUpdate;
    public string Designation => Diff.Designation;
    public string Path => Diff.RelativePath;
    public string Icon => Diff.Error is null ? IconKeys.Warning : IconKeys.Error;
    public string CountText => Diff.Error is null ? $"изменений: {Diff.Changes.Count}" : "не прочитан";
}

/// <summary>Одно изменение свойства в файле.</summary>
public sealed record ChangeRow(DiffRow Owner, string Property, string Current, string Expected);

/// <summary>Группа дубликатов: отметка «объединить» и выбор оставляемого.</summary>
public sealed partial class DuplicateGroupRow : ObservableObject
{
    private readonly Action _changed;

    public DuplicateGroupRow(DuplicateGroup group, Action changed)
    {
        Group = group;
        _changed = changed;
        Rows = group.Items.Select(e => new DuplicateRow(this, e)).ToList();
        // Оставить по умолчанию: стоит в сборках библиотеки, иначе самый ранний.
        var keep = Rows.OrderByDescending(r => r.UsedIn).ThenBy(r => r.Entry.CreatedAt).First();
        keep.Keep = true;
        // Совпадает только геометрия — возможно, законная копия (другое покрытие): по умолчанию не объединяем.
        _merge = !group.GeometryOnly;
    }

    public DuplicateGroup Group { get; }
    public IReadOnlyList<DuplicateRow> Rows { get; }
    public string Reason => Group.Reason;
    public DuplicateRow Kept => Rows.Single(r => r.Keep);

    [ObservableProperty] private bool _merge;

    partial void OnMergeChanged(bool value) => _changed();

    internal void Choose(DuplicateRow row)
    {
        foreach (var other in Rows.Where(r => r != row))
            other.Keep = false;
    }
}

/// <summary>Компонент в группе дубликатов.</summary>
public sealed partial class DuplicateRow(DuplicateGroupRow owner, DuplicateEntry entry) : ObservableObject
{
    public DuplicateGroupRow Owner => owner;
    public DuplicateEntry Entry => entry;
    public string Designation => entry.Item.Designation;
    public string Path => entry.Item.RelativePath;
    public int UsedIn => entry.UsedIn;
    public string Created => entry.CreatedAt.ToLocalTime().ToString("dd.MM.yyyy HH:mm");
    public string GroupName => owner.GetHashCode().ToString();

    [ObservableProperty] private bool _keep;

    partial void OnKeepChanged(bool value)
    {
        if (value)
            owner.Choose(this);
        OnPropertyChanged(nameof(Action));
    }

    public string Action => Keep ? "Оставить" : "Удалить";
}
