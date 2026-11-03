namespace PdmLite.Core.IO;

/// <summary>Лексическая проверка путей для файлов библиотеки и архивов, с правилами Windows на любой ОС.</summary>
public static class ContainedPath
{
    private static bool IsDevice(string segment)
    {
        var stem = segment.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$"
            || (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT"))
                && "123456789¹²³".Contains(stem[3]));
    }
    public static string Resolve(string root, string relative)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relative);
        var normalized = relative.Replace('\\', '/');
        var parts = normalized.Split('/');
        if (parts.Any(p => IsDevice(p) || p.Length == 0 || p is "." or ".." || p.EndsWith('.') || p.EndsWith(' ')
            || p.Any(c => char.IsControl(c) || ":*?\"<>|".Contains(c))))
            throw new ArgumentException("Ожидается относительный путь без выхода из корневой папки.", nameof(relative));
        var full = Path.GetFullPath(Path.Combine(root, Path.Combine(parts)));
        if (!Contains(root, full)) throw new ArgumentException("Путь находится вне корневой папки.", nameof(relative));
        return full;
    }

    public static bool Contains(string root, string path)
    {
        var fullRoot = Path.GetFullPath(root);
        var prefix = Path.EndsInDirectorySeparator(fullRoot) ? fullRoot : fullRoot + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && !Path.GetFullPath(path).Equals(Path.TrimEndingDirectorySeparator(fullRoot), StringComparison.OrdinalIgnoreCase);
    }
}
