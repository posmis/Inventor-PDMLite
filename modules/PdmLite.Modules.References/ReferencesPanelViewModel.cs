using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PdmLite.Core.Documents;
using PdmLite.Core.Domain;
using PdmLite.Core.Events;
using PdmLite.Core.Threading;
using PdmLite.Core.UI;
using PdmLite.Data;
using PdmLite.Index;
using PdmLite.UI.Shared;

namespace PdmLite.Modules.References;

/// <summary>Индекс ссылок обновлён — панели перечитывают связи.</summary>
public sealed record ReferenceIndexUpdated(string Root);

/// <summary>Панель «Связи» выбранного документа: где используется и из чего состоит.</summary>
public sealed partial class ReferencesPanelViewModel(
    ReferenceIndexService index,
    IPdmDatabase database,
    IDocumentLauncher launcher,
    IEventBus events,
    IUiDispatcher ui,
    ILogger<ReferencesPanelViewModel> logger) : ObservableObject
{
    private readonly List<IDisposable> _subscriptions = [];
    private int _version;

    public ObservableCollection<ReferenceRow> UsedIn { get; } = [];
    public ObservableCollection<ReferenceRow> Components { get; } = [];

    /// <summary>Ненайденные файлы сборки на всех уровнях — только имена, каждый один раз.</summary>
    public ObservableCollection<MissingFile> Missing { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasItem), nameof(Title))]
    private ProjectItem? _item;

    /// <summary>«Где используется» на всех уровнях (до верхней сборки), иначе — только непосредственно.</summary>
    [ObservableProperty] private bool _allLevels;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenCommand), nameof(ShowFileCommand), nameof(CopyPathCommand))]
    private ReferenceRow? _selected;

    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _isBusy;

    public bool HasItem => Item is not null;
    public string Title => Item?.Name ?? "";

    internal void Attach()
    {
        _subscriptions.Add(events.Subscribe<SelectionChanged>(e =>
            ui.Post(() => Item = e.Items is [{ IsFolder: false } single] ? single : null)));
        _subscriptions.Add(events.Subscribe<ReferenceIndexUpdated>(_ => ui.Post(() => LoadAsync().Forget())));
    }

    internal void Detach()
    {
        foreach (var subscription in _subscriptions)
            subscription.Dispose();
    }

    partial void OnItemChanged(ProjectItem? value) => LoadAsync().Forget();
    partial void OnAllLevelsChanged(bool value) => LoadAsync().Forget();

    [RelayCommand]
    public async Task LoadAsync()
    {
        var version = ++_version;
        UsedIn.Clear();
        Components.Clear();
        Missing.Clear();
        if (Item is not { } item)
        {
            Status = "";
            return;
        }
        if (!database.IsConfigured)
        {
            Status = "Нет подключения к базе данных — укажите его в настройках библиотеки.";
            return;
        }

        IsBusy = true;
        try
        {
            var scanned = await index.ScannedAtAsync(item.Path);
            var usedIn = await index.WhereUsedAsync(item.Path, AllLevels);
            var components = await index.ComponentsAsync(item.Path);
            var missing = await index.MissingComponentsAsync(item.Path);
            if (version != _version)
                return; // пока загружали, выбрали другой документ

            foreach (var document in usedIn)
                UsedIn.Add(new ReferenceRow(document));
            foreach (var document in components)
                Components.Add(new ReferenceRow(document));
            foreach (var file in missing)
                Missing.Add(file);

            Status = scanned is null
                ? "Документ не проиндексирован — обновите индекс проекта (PdmLite Explorer → «Индекс ссылок проекта…»)."
                : $"Индекс от {scanned.Value.ToLocalTime():g}.";
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Не удалось загрузить связи {Path}", item.Path);
            Status = $"Не удалось загрузить связи: {ex.GetBaseException().Message}";
        }
        finally
        {
            if (version == _version)
                IsBusy = false;
        }
    }

    private bool CanOpen() => Selected is { Exists: true };

    [RelayCommand(CanExecute = nameof(CanOpen))]
    private async Task OpenAsync()
    {
        if (Selected is not { } row)
            return;
        try
        {
            await launcher.OpenAsync(row.Path);
        }
        catch (Exception ex)
        {
            UiMessages.Error("Не удалось открыть документ.", ex);
        }
    }

    [RelayCommand(CanExecute = nameof(CanOpen))]
    private void ShowFile()
    {
        if (Selected is { } row)
            UiClipboard.ShowInExplorer(row.Path);
    }

    /// <summary>Имена ненайденных файлов — в буфер обмена, по одному на строку.</summary>
    [RelayCommand]
    private void CopyMissingNames() => UiClipboard.SetText(string.Join(Environment.NewLine, Missing.Select(m => m.Name)));

    private bool HasSelection() => Selected is not null;

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void CopyPath()
    {
        if (Selected is { } row)
            UiClipboard.SetText(row.Path);
    }
}

/// <summary>Строка списков панели и окна индекса.</summary>
public sealed record ReferenceRow(IndexedDocument Document)
{
    public string Path => Document.Path;
    public string Name => Document.Name;
    public bool Exists => Document.Exists;
    public string Designation => Document.PartNumber ?? "";
    public string Title => Document.Title ?? "";
    public string KindName => DocumentKinds.DisplayName(Document.Kind);
    public string LevelText => Document.Level > 1 ? $"ур. {Document.Level}" : "";
    public string Icon => Document.Exists ? IconKeys.StateOk : IconKeys.Error;
}
