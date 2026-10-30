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

    /// <summary>Короткая метка типа: расширение файла.</summary>
    public string Badge => IsFolder ? "" : System.IO.Path.GetExtension(Item.Path).TrimStart('.').ToUpperInvariant();

    public string ToolTip => $"{DocumentKinds.DisplayName(Kind)}\n{Item.Path}";

    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isVisible = true;

    /// <summary>Документ активен в Inventor.</summary>
    [ObservableProperty] private bool _isActive;

    public IEnumerable<TreeNodeViewModel> Descendants()
    {
        yield return this;
        foreach (var child in Children)
            foreach (var node in child.Descendants())
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
