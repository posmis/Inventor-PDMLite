namespace PdmLite.Explorer;

/// <summary>
/// Аргументы командной строки:
/// <c>PdmLite.Explorer.exe [путь к проекту] [--reveal путь к документу]</c>.
/// </summary>
public sealed record StartupArgs(string? ProjectPath, string? RevealPath)
{
    public static StartupArgs Parse(IReadOnlyList<string> args)
    {
        string? project = null, reveal = null;
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i] is "--reveal" or "-r" && i + 1 < args.Count)
                reveal = args[++i];
            else if (!args[i].StartsWith('-'))
                project ??= args[i];
        }
        return new StartupArgs(project, reveal);
    }
}
