using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdmLite.Core.UI;
using PdmLite.Library.Catalog;
using PdmLite.Library.Items;
using PdmLite.UI.Shared;

namespace PdmLite.Modules.Library.Catalog;

/// <summary>
/// Обслуживание библиотеки в два шага: «Проверить» (только чтение, список изменений) →
/// «Применить к отмеченным» (запись после предупреждения).
/// </summary>
public sealed partial class MaintenanceViewModel(
    LibraryItemService service,
    LibraryCatalog catalog,
    IReadOnlyCollection<Guid> selectedIds,
    int totalCount) : ObservableObject
{
    private CancellationTokenSource? _cts;

    public int SelectedCount => selectedIds.Count;
    public int TotalCount => totalCount;
    public bool HasSelection => selectedIds.Count > 0;

    public ObservableCollection<DiffRow> Rows { get; } = [];

    [ObservableProperty] private bool _onlySelected = selectedIds.Count > 0;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckCommand), nameof(ApplyCommand), nameof(StopCommand))]
    private bool _isRunning;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    private int _toApply;

    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private string _summary = "Нажмите «Проверить»: файлы будут только прочитаны, ничего не изменится.";

    /// <summary>Файлы изменялись — каталог стоит перечитать.</summary>
    public bool Changed { get; private set; }

    private bool CanCheck() => !IsRunning;

    /// <summary>Шаг 1: только чтение.</summary>
    [RelayCommand(CanExecute = nameof(CanCheck))]
    private Task CheckAsync() => RunAsync("Проверка", async (progress, ct) =>
    {
        var diffs = await service.DiagnosePropertiesAsync(catalog, OnlySelected ? selectedIds : null, progress, ct);
        Rows.Clear();
        foreach (var diff in diffs.Where(d => d.NeedsUpdate || d.Error is not null))
            Rows.Add(new DiffRow(diff, UpdateCount));
        UpdateCount();

        var needs = diffs.Count(d => d.NeedsUpdate);
        var errors = diffs.Count(d => d.Error is not null);
        Summary = $"Проверено: {diffs.Count}. В порядке: {diffs.Count - needs - errors}. Нужно обновить: {needs}."
                  + (errors > 0 ? $" Не прочитаны: {errors}." : "")
                  + (needs > 0 ? " Посмотрите список, снимите отметки с лишнего и нажмите «Применить к отмеченным»." : "");
    });

    private bool CanApply() => !IsRunning && ToApply > 0;

    /// <summary>Шаг 2: запись — только отмеченные, после предупреждения.</summary>
    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task ApplyAsync()
    {
        var ids = Rows.Where(r => r.Apply).Select(r => r.Diff.ItemId).ToList();
        if (System.Windows.MessageBox.Show(
                $"Будут изменены файлы библиотеки: {ids.Count}.\n\n" +
                "• перед записью каждый файл проверяется ещё раз — если он уже в порядке, не изменяется;\n" +
                "• перед записью делается резервная копия файла, при ошибке файл восстанавливается;\n" +
                "• остановить можно в любой момент — обработанные файлы останутся обновлёнными.\n\n" +
                "Продолжить?",
                UiMessages.Caption, System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Warning)
            != System.Windows.MessageBoxResult.OK)
            return;

        RewriteResult? result = null;
        await RunAsync("Запись свойств", async (progress, ct) => result = await service.RewritePropertiesAsync(catalog, ids, progress, ct));
        Changed = true;
        if (result is not null)
        {
            var text = $"Обновлено: {result.Updated}. Уже были в порядке: {result.UpToDate}.";
            if (result.Failed.Count > 0)
                UiMessages.Error(text + $"\n\nНе удалось ({result.Failed.Count}):\n" + string.Join("\n", result.Failed.Take(20)));
            else
                UiMessages.Info(text);
        }

        // Повторная проверка: в списке остаётся только то, что ещё не в порядке.
        await CheckAsync();
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
            Summary = $"{title}: остановлено.";
        }
        catch (Exception ex)
        {
            Summary = $"{title}: ошибка — {ex.Message}";
        }
        finally
        {
            IsRunning = false;
            ProgressText = "";
            _cts.Dispose();
            _cts = null;
        }
    }

    private void UpdateCount() => ToApply = Rows.Count(r => r.Apply);
}

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

    /// <summary>«Свойство: было → станет», по строке на свойство.</summary>
    public string ChangesText => Diff.Error is not null
        ? $"Не прочитан: {Diff.Error}"
        : string.Join("\n", Diff.Changes.Select(c => $"{c.Property}: {Show(c.Current)} → {Show(c.Expected)}"));

    private static string Show(string? value) => string.IsNullOrEmpty(value) ? "(нет)" : $"«{value}»";
}
