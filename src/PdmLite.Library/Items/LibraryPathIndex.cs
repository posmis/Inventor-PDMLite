using System.Text.Json;
using PdmLite.Data.Library;
using PdmLite.Library.Designations;

namespace PdmLite.Library.Items;

/// <summary>
/// Где компоненты библиотеки лежат сейчас и где лежали раньше (до изменения обозначения,
/// стандарта или раздела). По нему ненайденная ссылка сборки подменяется текущим файлом.
/// Все поиски — в памяти: вызывается из события Inventor при открытии каждого файла.
/// </summary>
public sealed class LibraryPathIndex
{
    private readonly HashSet<string> _current = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _previous = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<string>> _byFileName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<string>> _byKey = new(StringComparer.Ordinal);

    /// <param name="items">Компоненты: Id, текущий путь в библиотеке, обозначение.</param>
    /// <param name="moves">Переезды по журналу (старый путь → Id), от старых к новым.</param>
    public LibraryPathIndex(IEnumerable<(Guid Id, string RelativePath, string Designation)> items, IEnumerable<(string OldPath, Guid Id)> moves)
    {
        var byId = new Dictionary<Guid, string>();
        foreach (var (id, relative, designation) in items)
        {
            var path = Normalize(relative);
            byId[id] = path;
            _current.Add(path);
            Add(_byFileName, Path.GetFileName(path), path);
            Add(_byKey, DesignationNormalizer.ToKey(designation), path);
            Add(_byKey, DesignationNormalizer.ToKey(Path.GetFileNameWithoutExtension(path)), path);
        }

        // Удалённые компоненты пропускаем; старый путь, на котором теперь другой компонент, — тоже.
        foreach (var (oldPath, id) in moves)
            if (byId.TryGetValue(id, out var now) && !_current.Contains(Normalize(oldPath)))
                _previous[Normalize(oldPath)] = now;
    }

    public static LibraryPathIndex Empty { get; } = new([], []);

    public int Count => _current.Count;

    /// <summary>
    /// Текущий путь компонента (относительно папки библиотеки) для ссылки, которую документ не нашёл,
    /// или <c>null</c>, если это не компонент библиотеки.
    /// </summary>
    /// <param name="reference">Ссылка как её хранит документ: полный путь или путь внутри библиотеки.</param>
    /// <param name="missing">Файл по ссылке точно отсутствует (тогда допустим поиск по имени файла).</param>
    public string? Find(string reference, bool missing)
    {
        var path = Normalize(reference);

        // 1. Старый путь компонента библиотеки — в конце ссылки (корень библиотеки мог быть другим).
        for (var start = 0; start >= 0; start = NextSegment(path, start))
        {
            var tail = path[start..];
            if (_previous.TryGetValue(tail, out var moved))
                return moved;
            if (missing && _current.Contains(tail))
                return tail; // тот же путь, но библиотека теперь в другой папке
        }

        if (!missing)
            return null;

        // 2. По имени файла и по обозначению — только если совпадение одно.
        if (_byFileName.TryGetValue(Path.GetFileName(path), out var sameName) && sameName.Count == 1)
            return sameName[0];
        var key = DesignationNormalizer.ToKey(Path.GetFileNameWithoutExtension(path));
        return key.Length > 0 && _byKey.TryGetValue(key, out var sameKey) && sameKey.Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1
            ? sameKey[0]
            : null;
    }

    /// <summary>
    /// Старые пути из журнала: изменение (<c>edit</c>: before.RelativePath) и объединение
    /// дубликатов (<c>merge</c>: путь удалённого → оставленный компонент).
    /// </summary>
    public static IEnumerable<(string OldPath, Guid Id)> MovesFromLog(IEnumerable<LibraryLogEntry> log)
    {
        foreach (var entry in log.Where(e => e is { Action: "edit" or "merge", ItemId: not null, Details: not null }).OrderBy(e => e.At))
        {
            string? before = null, after = null;
            try
            {
                using var json = JsonDocument.Parse(entry.Details!);
                if (entry.Action == "merge")
                {
                    before = json.RootElement.TryGetProperty("RelativePath", out var removed) && removed.ValueKind == JsonValueKind.String
                        ? removed.GetString()
                        : null;
                }
                else
                {
                    before = Read(json.RootElement, "before");
                    after = Read(json.RootElement, "after");
                }
            }
            catch (JsonException)
            {
            }

            if (before is not null && !string.Equals(before, after, StringComparison.OrdinalIgnoreCase))
                yield return (before, entry.ItemId!.Value);
        }

        static string? Read(JsonElement root, string name) =>
            root.TryGetProperty(name, out var snapshot) && snapshot.ValueKind == JsonValueKind.Object
            && snapshot.TryGetProperty("RelativePath", out var path) && path.ValueKind == JsonValueKind.String
                ? path.GetString()
                : null;
    }

    private static int NextSegment(string path, int start)
    {
        var slash = path.IndexOf('/', start);
        return slash < 0 ? -1 : slash + 1;
    }

    private static string Normalize(string path) => path.Replace('\\', '/').Trim('/');

    private static void Add(Dictionary<string, List<string>> map, string key, string path)
    {
        if (key.Length == 0)
            return;
        if (!map.TryGetValue(key, out var list))
            map[key] = list = [];
        list.Add(path);
    }
}
