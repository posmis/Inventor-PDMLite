using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PdmLite.Core.Documents;
using PdmLite.Core.Events;
using PdmLite.Core.Projects;
using PdmLite.Core.Threading;
using PdmLite.Data;
using PdmLite.Data.Library;
using PdmLite.Library.Designations;
using PdmLite.Library.Items;
using PdmLite.Library.Storage;
using PdmLite.UI.Shared;

namespace PdmLite.Modules.Library.Panel;

/// <summary>
/// Панель «Библиотека»: дерево компонентов (раздел → группа → стандарт или производитель),
/// поиск, вставка в сборку Inventor. Одинакова в Inventor и в Explorer.
/// </summary>
public sealed partial class LibraryPanelViewModel : ObservableObject
{
    /// <summary>Имя библиотеки в проектах Inventor (раздел «Библиотеки» .ipj).</summary>
    public const string InventorLibraryName = "PdmLite";

    private readonly LibraryItemService _items;
    private readonly IPdmDatabase _database;
    private readonly IComponentPlacer _placer;
    private readonly IDocumentLauncher _launcher;
    private readonly Func<LibraryStorage> _storage;
    private readonly IProjectLibraryBinding? _binding;
    private readonly IUiDispatcher _ui;
    private readonly IEventBus _events;
    private readonly ILogger<LibraryPanelViewModel> _logger;
    private readonly List<IDisposable> _subscriptions = [];
    private bool _loaded;

    public LibraryPanelViewModel(LibraryItemService items, IPdmDatabase database, IComponentPlacer placer, IDocumentLauncher launcher,
        Func<LibraryStorage> storage, IUiDispatcher ui, IEventBus events, ILogger<LibraryPanelViewModel> logger,
        IProjectLibraryBinding? binding = null)
    {
        _items = items;
        _database = database;
        _placer = placer;
        _launcher = launcher;
        _storage = storage;
        _binding = binding;
        _ui = ui;
        _events = events;
        _logger = logger;
    }

    public ObservableCollection<LibraryNode> Nodes { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PlaceCommand), nameof(OpenCommand), nameof(ShowFileCommand))]
    private LibraryNode? _selected;

    [ObservableProperty] private string _search = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status = "";

    /// <summary>Сообщение о подключении библиотеки к проекту Inventor (null — всё в порядке).</summary>
    [ObservableProperty] private string? _bindingNotice;
    [ObservableProperty] private bool _canConnectLibrary;

    public LibraryItemSummary? SelectedItem => Selected?.Item;

    internal void Attach()
    {
        _subscriptions.Add(_events.Subscribe<LibraryChanged>(_ => _ui.Post(() => LoadAsync().Forget())));
        // Сменился проект Inventor — проверяем, подключена ли к нему библиотека.
        _subscriptions.Add(_events.Subscribe<ProjectOpened>(_ => _ui.Post(() => CheckBindingAsync().Forget())));
        // Связь с Inventor появилась или пропала — меняется доступность «Вставить».
        _subscriptions.Add(_events.Subscribe<InventorConnectionChanged>(_ => _ui.Post(PlaceCommand.NotifyCanExecuteChanged)));
    }

    internal void Detach()
    {
        _subscriptions.ForEach(s => s.Dispose());
        _subscriptions.Clear();
    }

    /// <summary>Первая загрузка — когда панель впервые показана.</summary>
    public Task EnsureLoadedAsync() => _loaded ? Task.CompletedTask : LoadAsync();

    [RelayCommand]
    public async Task LoadAsync()
    {
        _loaded = true;
        if (!_database.IsConfigured)
        {
            Nodes.Clear();
            Status = "Подключение к базе не настроено: PdmLite Explorer → Сервис → Настройки библиотеки.";
            return;
        }

        IsBusy = true;
        try
        {
            var selectedId = SelectedItem?.Id;
            var catalog = await _items.LoadCatalogAsync();
            var items = await _items.ListAsync(catalog);
            Build(items, selectedId);
            Status = items.Count == 0 ? "Библиотека пуста. Компоненты добавляются в PdmLite Explorer." : $"Компонентов: {items.Count}";
            await CheckBindingAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Не удалось загрузить библиотеку");
            Status = $"Не удалось загрузить библиотеку: {ex.GetBaseException().Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Build(IReadOnlyList<LibraryItemSummary> items, Guid? selectedId)
    {
        var storage = _storage();
        var roots = new List<LibraryNode>();
        LibraryNode RootFolder(string name)
        {
            var node = roots.FirstOrDefault(r => r.Name == name);
            if (node is null)
                roots.Add(node = LibraryNode.Folder(name));
            return node;
        }

        foreach (var item in items.OrderBy(i => i.CategoryPath, StringComparer.CurrentCulture).ThenBy(i => i.Designation, StringComparer.CurrentCulture))
        {
            var parts = item.CategoryPath.Split('/');
            var folder = RootFolder(parts[0]);
            foreach (var part in parts.Skip(1))
                folder = folder.GetOrAddFolder(part);
            var sub = item.Kind == LibraryItemKind.Standard
                ? item.Standard ?? "Без стандарта"
                : item.Manufacturer ?? LibraryStorage.NoManufacturerFolder;
            folder.GetOrAddFolder(sub).Children.Add(LibraryNode.Component(item, storage.GetFullPath(item.RelativePath)));
        }

        Nodes.Clear();
        foreach (var root in roots)
            Nodes.Add(root);

        ApplyFilter();
        if (selectedId is not null && Nodes.SelectMany(n => n.Descendants()).FirstOrDefault(n => n.Item?.Id == selectedId) is { } again)
            Reveal(again);
    }

    partial void OnSearchChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        var key = DesignationNormalizer.ToKey(Search.Trim());
        foreach (var node in Nodes)
            node.ApplyFilter(key);
    }

    private void Reveal(LibraryNode target)
    {
        bool Expand(LibraryNode node)
        {
            if (node == target)
                return true;
            foreach (var child in node.Children)
                if (Expand(child))
                {
                    node.IsExpanded = true;
                    return true;
                }
            return false;
        }

        foreach (var root in Nodes)
            if (Expand(root))
                break;
        target.IsSelected = true;
        Selected = target;
    }

    private bool HasComponent() => Selected is { IsFolder: false };

    private bool CanPlace() => HasComponent() && _placer.CanPlace;

    [RelayCommand(CanExecute = nameof(CanPlace))]
    private async Task PlaceAsync()
    {
        if (Selected?.FullPath is not { } path)
            return;
        try
        {
            await _placer.PlaceAsync(path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Не удалось вставить {Path}", path);
            UiMessages.Error("Не удалось вставить компонент в сборку.", ex);
        }
    }

    [RelayCommand(CanExecute = nameof(HasComponent))]
    private async Task OpenAsync()
    {
        if (Selected?.FullPath is not { } path)
            return;
        try
        {
            await _launcher.OpenAsync(path);
        }
        catch (Exception ex)
        {
            UiMessages.Error("Не удалось открыть компонент.", ex);
        }
    }

    [RelayCommand(CanExecute = nameof(HasComponent))]
    private void ShowFile()
    {
        if (Selected?.FullPath is not { } path)
            return;
        if (!File.Exists(path))
        {
            UiMessages.Error($"Файл не найден в папке библиотеки:\n{path}");
            return;
        }
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true })?.Dispose();
    }

    private async Task CheckBindingAsync()
    {
        if (_binding is null || !_database.IsConfigured)
            return;
        try
        {
            var folder = _storage().Root;
            var state = await _binding.CheckAsync(InventorLibraryName, folder);
            (BindingNotice, CanConnectLibrary) = state.State switch
            {
                LibraryBindingState.Missing => ($"Библиотека «{InventorLibraryName}» не подключена к проекту «{state.ProjectName}». "
                                                + "Без неё Inventor считает компоненты библиотеки чужими файлами.", true),
                LibraryBindingState.DifferentFolder => ($"В проекте «{state.ProjectName}» библиотека «{InventorLibraryName}» указывает "
                                                        + $"на другую папку: {state.CurrentFolder}.", true),
                _ => ((string?)null, false),
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Не удалось проверить библиотеку в проекте Inventor");
        }
    }

    [RelayCommand]
    private async Task ConnectLibraryAsync()
    {
        if (_binding is null)
            return;
        try
        {
            await _binding.ConnectAsync(InventorLibraryName, _storage().Root);
            await CheckBindingAsync();
        }
        catch (Exception ex)
        {
            UiMessages.Error("Не удалось подключить библиотеку к проекту.", ex);
        }
    }
}
