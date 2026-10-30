namespace PdmLite.Core.Domain;

/// <summary>Тип элемента проекта.</summary>
public enum DocumentKind
{
    Folder,
    Part,
    Assembly,
    Drawing,
    Presentation,
    Project,
    Other,
}

public static class DocumentKinds
{
    private static readonly Dictionary<string, DocumentKind> ByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".ipt"] = DocumentKind.Part,
        [".iam"] = DocumentKind.Assembly,
        [".idw"] = DocumentKind.Drawing,
        [".dwg"] = DocumentKind.Drawing,
        [".ipn"] = DocumentKind.Presentation,
        [".ipj"] = DocumentKind.Project,
    };

    /// <summary>Расширения файлов, которые учитывает PdmLite.</summary>
    public static IReadOnlyCollection<string> KnownExtensions => ByExtension.Keys;

    public static DocumentKind FromPath(string path) =>
        ByExtension.TryGetValue(Path.GetExtension(path), out var kind) ? kind : DocumentKind.Other;

    public static bool IsKnown(string path) => ByExtension.ContainsKey(Path.GetExtension(path));

    public static string DisplayName(DocumentKind kind) => kind switch
    {
        DocumentKind.Folder => "Папка",
        DocumentKind.Part => "Деталь",
        DocumentKind.Assembly => "Сборка",
        DocumentKind.Drawing => "Чертёж",
        DocumentKind.Presentation => "Презентация",
        DocumentKind.Project => "Файл проекта",
        _ => "Файл",
    };
}
