using System.IO;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PdmLite.Core.Domain;
using PdmLite.Core.Events;
using PdmLite.Core.Modularity;
using PdmLite.Core.Projects;
using PdmLite.Core.Threading;
using PdmLite.Core.UI;
using PdmLite.UI.Shared;

namespace PdmLite.Modules.ProjectTree.ViewModels;

public sealed partial class ProjectTreeViewModel(
    IEventBus events,
    IProjectSession session,
    IUiDispatcher ui,
    IUiRegistry registry,
    ILogger<ProjectTreeViewModel> logger) : ObservableObject
{
    private readonly List<IDisposable> _subscriptions = [];
    private Dictionary<string, TreeNodeViewModel> _byPath = new(StringComparer.OrdinalIgnoreCase);
    private TreeNodeViewModel? _activeNode;
    private string? _activeDocumentPath;

    public ObservableCollection<TreeNodeViewModel> Nodes { get; } = [];

    public IUiRegistry Registry => registry;

    [ObservableProperty] private TreeNodeViewModel? _selectedNode;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string _statusText = "Проект не открыт";
    [ObservableProperty] private bool _isBusy;

    /// <summary>Выделять в дереве документ, активный в Inventor.</summary>
    [ObservableProperty] private bool _followActiveDocument = true;

    internal void Attach()
    {
        _subscriptions.Add(events.Subscribe<ProjectOpened>(e => ui.Post(() => Build(e.Root))));
        _subscriptions.Add(events.Subscribe<ActiveDocumentChanged>(e => ui.Post(() => OnActiveDocumentChanged(e.Path))));
        _subscriptions.Add(events.Subscribe<RevealDocumentRequested>(e => ui.Post(() => Reveal(e.Path, remember: true))));
        _subscriptions.Add(events.Subscribe<ProjectFilesMoved>(e => ui.Post(() => _moved = e.Moves)));

        if (session.Root is { } root)
            Build(root);
    }

    internal void Detach()
    {
        _subscriptions.ForEach(s => s.Dispose());
        _subscriptions.Clear();
    }

    public CommandContext CurrentContext() =>
        SelectedNode is { } node ? new CommandContext([node.Item]) : CommandContext.Empty;

    /// <summary>Перетаскивание в дереве: переместить файл или папку (выполняет модуль переименования, с проверкой и подтверждением).</summary>
    public void RequestMove(IReadOnlyList<string> paths, string targetFolder) =>
        events.Publish(new MoveRequested(paths, targetFolder));

    /// <summary>Команда по умолчанию (двойной щелчок).</summary>
    public async Task ExecuteDefaultAsync()
    {
        var context = CurrentContext();
        var command = registry.Commands.FirstOrDefault(c => c.IsDefault && c.IsAvailable(context));
        if (command is not null)
            await CommandMenuBuilder.RunAsync(command, context);
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (session.Project is null)
            return;
        IsBusy = true;
        try
        {
            await session.RefreshAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Ошибка обновления дерева проекта");
            UiMessages.Error("Не удалось обновить дерево проекта.", ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void CollapseAll()
    {
        foreach (var node in Nodes.SelectMany(n => n.Descendants()))
            node.IsExpanded = false;
        foreach (var node in Nodes)
            node.IsExpanded = true;
    }

    partial void OnSearchTextChanged(string value)
    {
        var filter = value.Trim();
        foreach (var node in Nodes)
            node.ApplyFilter(filter);
    }

    partial void OnSelectedNodeChanged(TreeNodeViewModel? value) =>
        events.Publish(new SelectionChanged(value is null ? [] : [value.Item]));

    partial void OnFollowActiveDocumentChanged(bool value)
    {
        if (value && _activeDocumentPath is not null)
            Reveal(_activeDocumentPath);
    }

    private void Build(ProjectItem root)
    {
        // Перестроение (обновление, переименование) не сворачивает дерево: раскрытые папки и выделение сохраняются,
        // переименованные и перемещённые — по новым путям.
        var selectedPath = Moved(SelectedNode?.Item.Path);
        var expanded = Nodes.SelectMany(n => n.Descendants()).Where(n => n.IsFolder && n.IsExpanded)
            .Select(n => Moved(n.Item.Path)!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _moved = null;

        var rootNode = new TreeNodeViewModel(root, null) { IsExpanded = true };
        Nodes.Clear();
        Nodes.Add(rootNode);
        _byPath = rootNode.Descendants().ToDictionary(n => n.Item.Path, StringComparer.OrdinalIgnoreCase);
        _activeNode = null;
        foreach (var node in _byPath.Values.Where(n => n.IsFolder && expanded.Contains(n.Item.Path)))
            node.IsExpanded = true;

        var documents = _byPath.Values.Count(n => !n.IsFolder);
        StatusText = $"{session.Project?.Name}: документов {documents}";

        if (SearchText.Length > 0)
            OnSearchTextChanged(SearchText);

        if (_activeDocumentPath is not null)
            MarkActive(_activeDocumentPath);

        if (_pendingReveal is { } pending && _byPath.ContainsKey(pending))
            Reveal(pending);
        else if (selectedPath is not null && _byPath.ContainsKey(selectedPath))
            Reveal(selectedPath);
        else if (FollowActiveDocument && _activeDocumentPath is not null)
            Reveal(_activeDocumentPath);
        _pendingReveal = null;
    }

    /// <summary>Пути, переименованные или перемещённые последней операцией (до перестроения дерева).</summary>
    private IReadOnlyDictionary<string, string>? _moved;

    /// <summary>Документ, который нужно выделить, когда дерево перестроится (его ещё нет в старом дереве).</summary>
    private string? _pendingReveal;

    /// <summary>Новый путь файла или папки после переименования/перемещения (папка — по путям её файлов).</summary>
    private string? Moved(string? path)
    {
        if (path is null || _moved is null)
            return path;
        if (_moved.TryGetValue(path, out var to))
            return to;
        var prefix = path.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var (from, target) in _moved)
        {
            if (!from.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;
            // Папка: та же глубина вложенности в новом пути.
            var depth = from[prefix.Length..].Split(Path.DirectorySeparatorChar).Length;
            var folder = target;
            for (var i = 0; i < depth && folder is not null; i++)
                folder = Path.GetDirectoryName(folder);
            return folder ?? path;
        }
        return path;
    }

    private void OnActiveDocumentChanged(string? path)
    {
        _activeDocumentPath = path;
        MarkActive(path);
        if (FollowActiveDocument && path is not null)
            Reveal(path);
    }

    private void MarkActive(string? path)
    {
        if (_activeNode is not null)
            _activeNode.IsActive = false;
        _activeNode = path is not null && _byPath.TryGetValue(path, out var node) ? node : null;
        if (_activeNode is not null)
            _activeNode.IsActive = true;
    }

    /// <param name="remember">Если узла ещё нет (дерево не перестроено после переименования) — выделить после перестроения.</param>
    private void Reveal(string path, bool remember = false)
    {
        if (!_byPath.TryGetValue(path, out var node))
        {
            if (remember)
                _pendingReveal = path;
            return;
        }

        // Найденный документ может быть скрыт фильтром — фильтр сбрасываем.
        if (!node.IsVisible)
            SearchText = "";

        node.ExpandAncestors();
        node.IsSelected = true;
        SelectedNode = node;
    }
}
