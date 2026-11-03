namespace PdmLite.Core.Domain;

/// <summary>Элемент дерева проекта: папка или файл.</summary>
public sealed class ProjectItem
{
    public ProjectItem(string path, DocumentKind kind, long size, DateTime modifiedUtc, IReadOnlyList<ProjectItem>? children = null)
    {
        Path = path;
        Kind = kind;
        Size = size;
        ModifiedUtc = modifiedUtc;
        Children = children ?? [];
    }

    public string Path { get; }
    public string Name => System.IO.Path.GetFileName(Path);
    public DocumentKind Kind { get; }
    public bool IsFolder => Kind == DocumentKind.Folder;
    public long Size { get; }
    public DateTime ModifiedUtc { get; }
    public IReadOnlyList<ProjectItem> Children { get; }

    /// <summary>Обход поддерева в глубину, включая сам элемент.</summary>
    public IEnumerable<ProjectItem> Descendants()
    {
        var pending = new Stack<ProjectItem>();
        pending.Push(this);
        while (pending.TryPop(out var item))
        {
            yield return item;
            for (var i = item.Children.Count - 1; i >= 0; i--) pending.Push(item.Children[i]);
        }
    }

    public override string ToString() => Path;
}
