using PdmLite.Index;

namespace PdmLite.Modules.References;

/// <summary>Короткое представление замечания; исходные пути и текст сохраняются для поиска и копирования.</summary>
public sealed record ProjectHealthRow(ProjectHealthIssue Issue, string Root)
{
    public string Severity => Issue.Severity;
    public string Category => Issue.Category;
    public string Level => Severity == "Ошибка" ? "Ошибка" : "Замечание";
    public string Path => Issue.Path;
    public string Details => Issue.Details;
    public string Name => FileName(Path);
    public string DisplayName => Name.Length <= 64 ? Name : Name[..40] + "…" + Name[^23..];
    public string Folder
    {
        get
        {
            var path = Path.Replace('\\', '/');
            var root = Root.Replace('\\', '/').TrimEnd('/');
            var directory = path.Contains('/') ? path[..path.LastIndexOf('/')] : "";
            if (directory.Equals(root, StringComparison.OrdinalIgnoreCase)) return "Корень проекта";
            return directory.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase)
                ? directory[(root.Length + 1)..] : directory;
        }
    }
    public string Preview => Category switch
    {
        "Ненайденная ссылка" => "Не найден: " + FileName(Details),
        "Ссылка по другому пути" => "Найден: " + FileName(Details.Split('→')[^1].Trim()),
        "Одинаковое имя файла" or "Повтор обозначения" =>
            $"Совпадение с другими документами: {Details.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Length}",
        "Не прочитан" => "Не удалось прочитать документ",
        "Проверка ссылки" => "Не удалось проверить ссылку",
        _ => Details.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "",
    };

    public bool Matches(string search, string? severity, string? category) =>
        (severity is null || Severity == severity) && (category is null || Category == category)
        && (string.IsNullOrWhiteSpace(search) || new[] { Path, Details, Category, Severity }
            .Any(s => s.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase)));

    private static string FileName(string path) => path.Replace('\\', '/').TrimEnd('/').Split('/')[^1];
}
