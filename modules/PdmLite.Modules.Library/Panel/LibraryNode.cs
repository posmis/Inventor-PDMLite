using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using PdmLite.Library.Designations;
using PdmLite.Library.Items;

namespace PdmLite.Modules.Library.Panel;

/// <summary>Узел дерева библиотеки: папка (раздел, группа, стандарт, производитель) или компонент.</summary>
public sealed partial class LibraryNode : ObservableObject
{
    private readonly string _searchKey;

    private LibraryNode(string name, LibraryItemSummary? item, string? fullPath)
    {
        Name = name;
        Item = item;
        FullPath = fullPath;
        _searchKey = DesignationNormalizer.ToKey(name);
    }

    public static LibraryNode Folder(string name) => new(name, null, null);

    public static LibraryNode Component(LibraryItemSummary item, string fullPath) => new(item.Designation, item, fullPath);

    public string Name { get; }
    public LibraryItemSummary? Item { get; }

    /// <summary>Полный путь файла компонента (для иконки и вставки).</summary>
    public string? FullPath { get; }

    public bool IsFolder => Item is null;
    public ObservableCollection<LibraryNode> Children { get; } = [];

    /// <summary>Число компонентов внутри папки — показывается рядом с названием.</summary>
    public int Count => IsFolder ? Children.Sum(c => c.IsFolder ? c.Count : 1) : 0;
    public string CountText => IsFolder ? $"({Count})" : "";

    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isVisible = true;

    public LibraryNode GetOrAddFolder(string name)
    {
        var existing = Children.FirstOrDefault(c => c.IsFolder && c.Name == name);
        if (existing is not null)
            return existing;
        var folder = Folder(name);
        Children.Add(folder);
        return folder;
    }

    public IEnumerable<LibraryNode> Descendants()
    {
        yield return this;
        foreach (var child in Children)
            foreach (var node in child.Descendants())
                yield return node;
    }

    /// <summary>
    /// Фильтр по обозначению. Сравнение по ключу: «м8» найдёт «M8», «2х4» — «2x4».
    /// Возвращает видимость узла.
    /// </summary>
    public bool ApplyFilter(string key)
    {
        var anyChild = false;
        foreach (var child in Children)
            anyChild |= child.ApplyFilter(key);

        IsVisible = key.Length == 0 || anyChild || (!IsFolder && _searchKey.Contains(key, StringComparison.Ordinal));
        if (key.Length > 0 && anyChild)
            IsExpanded = true;
        return IsVisible;
    }
}
