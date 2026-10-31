using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PdmLite.Core.Events;
using PdmLite.Core.Projects;
using PdmLite.Index;
using PdmLite.UI.Shared;

namespace PdmLite.Modules.References;

/// <summary>Окно индекса ссылок проекта: обновление (с остановкой), битые ссылки, нечитаемые файлы.</summary>
public sealed partial class IndexViewModel(
    ReferenceIndexService index,
    IProjectSession session,
    IEventBus events,
    ILogger<IndexViewModel> logger) : ObservableObject
{
    private CancellationTokenSource? _cts;

    public ObservableCollection<MissingRow> Broken { get; } = [];

    /// <summary>Нет по записанному пути, но найдены в библиотеке (переехали или переименованы) — Inventor находит их сам.</summary>
    public ObservableCollection<MissingRow> FoundInLibrary { get; } = [];
    public ObservableCollection<string> Errors { get; } = [];

    public string? Root => session.Project?.RootFolder;
    public string ProjectText => session.Project is { } p ? $"{p.Name}  —  {p.RootFolder}" : "Проект не открыт";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand), nameof(StopCommand))]
    private bool _isBusy;

    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private MissingRow? _selected;

    private bool CanScan() => !IsBusy && Root is not null;

    /// <summary>Читает новые и изменённые документы проекта; показывает битые ссылки.</summary>
    [RelayCommand(CanExecute = nameof(CanScan))]
    public async Task ScanAsync()
    {
        if (Root is not { } root)
            return;
        IsBusy = true;
        _cts = new CancellationTokenSource();
        var progress = new Progress<(int Done, int Total, string? Current)>(p =>
        {
            Progress = p.Total == 0 ? 100 : 100.0 * p.Done / p.Total;
            ProgressText = p.Current is null ? "" : $"Чтение документов: {p.Done + 1} из {p.Total} — {p.Current}";
        });
        try
        {
            var result = await index.ScanAsync(root, progress, _cts.Token);
            Errors.Clear();
            foreach (var error in result.Errors)
                Errors.Add(error);
            Summary = $"Документов в проекте: {result.Files}. Прочитано заново: {result.Read}" +
                      (result.Removed > 0 ? $", удалено из индекса: {result.Removed}" : "") +
                      (result.Errors.Count > 0 ? $", не прочитано: {result.Errors.Count}" : "") + ".";
            events.Publish(new ReferenceIndexUpdated(root));
        }
        catch (OperationCanceledException)
        {
            Summary = "Остановлено. Прочитанное сохранено — при следующем обновлении продолжится с оставшихся.";
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Не удалось обновить индекс {Root}", root);
            UiMessages.Error("Не удалось обновить индекс ссылок.", ex);
        }
        finally
        {
            IsBusy = false;
            ProgressText = "";
            Progress = 0;
            _cts.Dispose();
            _cts = null;
        }
        await LoadBrokenAsync();
    }

    private bool CanStop() => IsBusy;

    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop() => _cts?.Cancel();

    private async Task LoadBrokenAsync()
    {
        if (Root is not { } root)
            return;
        try
        {
            Broken.Clear();
            FoundInLibrary.Clear();
            foreach (var missing in await Task.Run(() => index.MissingFilesAsync(root)))
                (missing.IsFound ? FoundInLibrary : Broken).Add(new MissingRow(missing));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Не удалось получить битые ссылки");
        }
    }

    [RelayCommand]
    private void ShowDocument()
    {
        if (Selected is { } row)
            UiClipboard.ShowInExplorer(row.File.UsedIn[0]);
    }

    /// <summary>Только имена ненайденных файлов — по одному на строку.</summary>
    [RelayCommand]
    private void CopyNames() => UiClipboard.SetText(string.Join(Environment.NewLine, Broken.Select(b => b.Name)));

    /// <summary>Таблица: имя, где искали, где используется (для Excel).</summary>
    [RelayCommand]
    private void CopyList() => UiClipboard.SetText(string.Join(Environment.NewLine, Broken.Select(b => $"{b.Name}\t{b.Folder}\t{b.UsedInText}")));
}

/// <summary>Ненайденный файл: имя, папка, где его ищут, и документы, где он используется (по именам).</summary>
public sealed record MissingRow(MissingFile File)
{
    public string Name => File.Name;
    public string Path => File.Target;
    public string Folder => File.Folder;
    public int Count => File.UsedIn.Count;
    public string UsedInText => string.Join(", ", File.UsedIn.Select(System.IO.Path.GetFileName));
    public string FoundAt => File.FoundAt ?? "";
}
