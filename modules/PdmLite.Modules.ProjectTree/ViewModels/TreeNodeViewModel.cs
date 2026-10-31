using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using PdmLite.Core.Domain;

namespace PdmLite.Modules.ProjectTree.ViewModels;

public sealed partial class TreeNodeViewModel : ObservableObject
{
    public TreeNodeViewModel(ProjectItem item, TreeNodeViewModel? parent)
    {
        Item = item;
        Parent = parent;
        Children = new ObservableCollection<TreeNodeViewModel>(item.Children.Select(c => new TreeNodeViewModel(c, this)));
    }

    public ProjectItem Item { get; }
    public TreeNodeViewModel? Parent { get; }
    public ObservableCollection<TreeNodeViewModel> Children { get; }

    public string Name => Item.Name;
    public DocumentKind Kind => Item.Kind;
    public bool IsFolder => Item.IsFolder;

    public string KindName => DocumentKinds.DisplayName(Kind);

    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isVisible = true;

    /// <summary>Входит в выделение нескольких элементов (Ctrl/Shift + щелчок).</summary>
    [ObservableProperty] private bool _isMarked;

    /// <summary>Документ активен в Inventor.</summary>
    [ObservableProperty] private bool _isActive;

    public IEnumerable<TreeNodeViewModel> Descendants()
    {
        yield return this;
        foreach (var child in Children)
            foreach (var node in child.Descendants())
                yield return node;
    }

    /// <summary>Узлы, которые сейчас видны в дереве (родители раскрыты, фильтр не скрыл), по порядку сверху вниз.</summary>
    public IEnumerable<TreeNodeViewModel> VisibleDescendants()
    {
        if (!IsVisible)
            yield break;
        yield return this;
        if (!IsExpanded)
            yield break;
        foreach (var child in Children)
            foreach (var node in child.VisibleDescendants())
                yield return node;
    }

    public void ExpandAncestors()
    {
        for (var node = Parent; node is not null; node = node.Parent)
            node.IsExpanded = true;
    }

    /// <summary>Скрывает узлы, не подходящие под фильтр. Возвращает видимость узла.</summary>
    public bool ApplyFilter(string filter)
    {
        var anyChild = false;
        foreach (var child in Children)
            anyChild |= child.ApplyFilter(filter);

        var selfMatches = filter.Length == 0 || Name.Contains(filter, StringComparison.CurrentCultureIgnoreCase);
        IsVisible = selfMatches || anyChild;

        if (filter.Length > 0 && anyChild)
            IsExpanded = true;

        return IsVisible;
    }
}
