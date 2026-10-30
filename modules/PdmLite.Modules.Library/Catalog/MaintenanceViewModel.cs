using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdmLite.Library.Catalog;
using PdmLite.Library.Items;
using PdmLite.UI.Shared;

namespace PdmLite.Modules.Library.Catalog;

/// <summary>
/// Служебные операции над библиотекой. Сейчас — обновление свойств PdmLite в файлах
/// (выделенные компоненты или вся библиотека), с остановкой и итогом.
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

    [ObservableProperty] private bool _onlySelected = selectedIds.Count > 0;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(StopCommand))]
    private bool _isRunning;

    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private string _result = "";

    public ObservableCollection<string> Failures { get; } = [];

    /// <summary>Были изменения в файлах — каталог стоит перечитать.</summary>
    public bool Changed { get; private set; }

    private bool CanStart() => !IsRunning;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        var count = OnlySelected ? SelectedCount : TotalCount;
        if (System.Windows.MessageBox.Show(
                $"Проверить свойства в файлах компонентов: {count}?\n\n" +
                "Файлы, где свойства уже правильные, не изменяются. Перед записью делается резервная копия файла, " +
                "при ошибке файл восстанавливается. Остановить можно в любой момент.",
                UiMessages.Caption, System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Question)
            != System.Windows.MessageBoxResult.OK)
            return;

        IsRunning = true;
        Failures.Clear();
        Result = "";
        _cts = new CancellationTokenSource();
        var progress = new Progress<(int Done, int Total, string? Current)>(p =>
        {
            Progress = p.Total == 0 ? 100 : 100.0 * p.Done / p.Total;
            ProgressText = p.Current is null ? "" : $"{p.Done + 1} из {p.Total}: {p.Current}";
        });

        try
        {
            var result = await service.RewritePropertiesAsync(catalog, OnlySelected ? selectedIds : null, progress, _cts.Token);
            Changed |= result.Updated > 0;
            foreach (var failure in result.Failed)
                Failures.Add(failure);
            Result = $"Проверено: {result.Checked}. Обновлено: {result.Updated}. Уже в порядке: {result.UpToDate}."
                     + (result.Failed.Count > 0 ? $" Ошибок: {result.Failed.Count} — список ниже." : "");
        }
        catch (OperationCanceledException)
        {
            Changed = true;
            Result = "Остановлено. Обработанные файлы обновлены, остальные не тронуты.";
        }
        catch (Exception ex)
        {
            Result = $"Ошибка: {ex.Message}";
        }
        finally
        {
            IsRunning = false;
            ProgressText = "";
            _cts.Dispose();
            _cts = null;
        }
    }

    private bool CanStop() => IsRunning;

    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop() => _cts?.Cancel();
}
