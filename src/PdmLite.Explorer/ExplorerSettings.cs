namespace PdmLite.Explorer;

/// <summary>Настройки Explorer (<c>%AppData%\PdmLite\explorer.json</c>).</summary>
public sealed class ExplorerSettings
{
    public const string Name = "explorer";
    public const int MaxRecentProjects = 10;

    public string? LastProject { get; set; }
    public List<string> RecentProjects { get; set; } = [];

    public double WindowWidth { get; set; } = 1100;
    public double WindowHeight { get; set; } = 700;
    public bool WindowMaximized { get; set; }

    public void AddRecent(string path)
    {
        RecentProjects.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        RecentProjects.Insert(0, path);
        if (RecentProjects.Count > MaxRecentProjects)
            RecentProjects.RemoveRange(MaxRecentProjects, RecentProjects.Count - MaxRecentProjects);
        LastProject = path;
    }
}
