using System.Text.Json;

namespace PdmLite.Core.IO;

/// <summary>Первое состояние каждого документа перед операцией, с картой исходных путей для восстановления.</summary>
public sealed class DocumentBackupSet
{
    private readonly Dictionary<string, string> _copies = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private readonly string _documentName;
    public string Folder { get; }
    public DocumentBackupSet(string root, string documentName, TimeProvider? clock = null)
    {
        _documentName = documentName;
        Folder = Path.Combine(root, $"{(clock ?? TimeProvider.System).GetLocalNow():yyyyMMdd-HHmmss}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Folder);
    }
    public string Copy(string path)
    {
        path = Path.GetFullPath(path);
        lock (_gate)
        {
            if (_copies.TryGetValue(path, out var existing)) return existing;
            var relative = Path.Combine($"{_copies.Count + 1:0000}", Path.GetFileName(path));
            var target = Path.Combine(Folder, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(path, target, overwrite: false);
            _copies.Add(path, target);
            AtomicFile.WriteAllText(Path.Combine(Folder, "manifest.json"), JsonSerializer.Serialize(
                new { Document = _documentName, Files = _copies.Select(p => new { Original = p.Key, Backup = Path.GetRelativePath(Folder, p.Value) }) },
                new JsonSerializerOptions { WriteIndented = true }));
            return target;
        }
    }
}
