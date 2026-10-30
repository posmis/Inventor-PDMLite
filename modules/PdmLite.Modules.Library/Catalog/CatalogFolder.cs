using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using PdmLite.Core.Naming;

namespace PdmLite.Modules.Library.Catalog;

/// <summary>Папка дерева каталога: раздел, группа, стандарт или производитель.</summary>
public sealed partial class CatalogFolder(string name, string path) : ObservableObject
{
    public string Name { get; } = name;

    /// <summary>Путь папки: «Крепёж/Болты/ГОСТ 7798-70»; пусто — вся библиотека.</summary>
    public string Path { get; } = path;

    public ObservableCollection<CatalogFolder> Children { get; } = [];

    /// <summary>Компонентов в папке со всеми вложенными.</summary>
    [ObservableProperty] private int _count;

    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _isSelected;

    public string CountText => Count > 0 ? $"({Count})" : "";
    partial void OnCountChanged(int value) => OnPropertyChanged(nameof(CountText));

    /// <summary>Компонент с папкой <paramref name="itemFolder"/> лежит в этой папке или вложенной.</summary>
    public bool Contains(string itemFolder) =>
        Path.Length == 0 || itemFolder == Path || itemFolder.StartsWith(Path + "/", StringComparison.Ordinal);

    public CatalogFolder GetOrAdd(string name)
    {
        var existing = Children.FirstOrDefault(c => c.Name == name);
        if (existing is not null)
            return existing;
        var folder = new CatalogFolder(name, Path.Length == 0 ? name : $"{Path}/{name}");
        // Порядок как в проводнике: числа по величине.
        var index = 0;
        while (index < Children.Count && NaturalComparer.Instance.Compare(Children[index].Name, name) < 0)
            index++;
        Children.Insert(index, folder);
        return folder;
    }

    public IEnumerable<CatalogFolder> Descendants()
    {
        yield return this;
        foreach (var child in Children)
            foreach (var folder in child.Descendants())
                yield return folder;
    }
}
