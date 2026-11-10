namespace ShortenNames;

internal sealed record ProjectFileScan(IReadOnlyList<string> Files, IReadOnlyList<string> Warnings);

internal static class ProjectFiles
{
    public static ProjectFileScan Scan(string root, Func<string, IEnumerable<string>>? enumerate = null)
    {
        enumerate ??= path => Directory.EnumerateFileSystemEntries(path, "*", new EnumerationOptions
        {
            IgnoreInaccessible = false,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System
        });
        var files = new List<string>();
        var warnings = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var folder))
        {
            try
            {
                foreach (var entry in enumerate(folder))
                {
                    try
                    {
                        var attributes = File.GetAttributes(entry);
                        if ((attributes & FileAttributes.ReparsePoint) != 0)
                        {
                            warnings.Add($"{entry}: ссылка файловой системы пропущена для защиты от циклического обхода");
                            continue;
                        }
                        if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
                        else files.Add(entry);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    { warnings.Add($"{entry}: {ex.Message}"); }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { warnings.Add($"{folder}: {ex.Message}"); }
        }
        return new(files, warnings);
    }
}
