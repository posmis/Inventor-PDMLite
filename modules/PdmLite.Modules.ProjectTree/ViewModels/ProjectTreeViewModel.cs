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
        _subscriptions.Add(events.Subscribe<RevealDocumentRequested>(e => ui.Post(() => Reveal(e.Path))));

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
        var selectedPath = SelectedNode?.Item.Path;

        var rootNode = new TreeNodeViewModel(root, null) { IsExpanded = true };
        Nodes.Clear();
        Nodes.Add(rootNode);
        _byPath = rootNode.Descendants().ToDictionary(n => n.Item.Path, StringComparer.OrdinalIgnoreCase);
        _activeNode = null;

        var documents = _byPath.Values.Count(n => !n.IsFolder);
        StatusText = $"{session.Project?.Name}: документов {documents}";

        if (SearchText.Length > 0)
            OnSearchTextChanged(SearchText);

        if (_activeDocumentPath is not null)
            MarkActive(_activeDocumentPath);

        if (selectedPath is not null)
            Reveal(selectedPath);
        else if (FollowActiveDocument && _activeDocumentPath is not null)
            Reveal(_activeDocumentPath);
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

    private void Reveal(string path)
    {
        if (!_byPath.TryGetValue(path, out var node))
            return;

        // Найденный документ может быть скрыт фильтром — фильтр сбрасываем.
        if (!node.IsVisible)
            SearchText = "";

        node.ExpandAncestors();
        node.IsSelected = true;
        SelectedNode = node;
    }
}
