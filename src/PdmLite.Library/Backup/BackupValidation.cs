using PdmLite.Core.IO;
using PdmLite.Data.Library;

namespace PdmLite.Library.Backup;

/// <summary>Проверяет структуру до обращения к БД и подмены файлов. Не использует рекурсивный обход разделов.</summary>
internal static class BackupValidation
{
    public static IReadOnlyList<LibraryCategory> OrderCategories(IReadOnlyList<LibraryCategory> categories)
    {
        if (categories.Any(c => c is null) || categories.Select(c => c.Id).Distinct().Count() != categories.Count)
            throw new InvalidDataException("В копии повторяются или отсутствуют разделы.");
        var byId = categories.ToDictionary(c => c.Id);
        var children = categories.Where(c => c.ParentId.HasValue).ToLookup(c => c.ParentId!.Value);
        if (categories.Any(c => c.ParentId is { } parent && !byId.ContainsKey(parent)))
            throw new InvalidDataException("В копии отсутствует родительский раздел.");
        var pending = new Queue<LibraryCategory>(categories.Where(c => c.ParentId is null).OrderBy(c => c.Id));
        var ordered = new List<LibraryCategory>(categories.Count);
        while (pending.TryDequeue(out var category))
        {
            ordered.Add(category);
            foreach (var child in children[category.Id].OrderBy(c => c.Id)) pending.Enqueue(child);
        }
        if (ordered.Count != categories.Count) throw new InvalidDataException("В разделах копии обнаружен цикл.");
        return ordered;
    }

    public static void Validate(LibrarySnapshot snapshot, IEnumerable<string> entries)
    {
        if (snapshot.Categories is null || snapshot.Items is null || snapshot.Versions is null
            || snapshot.CategoryRules is null || snapshot.Properties is null || snapshot.References is null
            || snapshot.DesignationRules is null || snapshot.Log is null)
            throw new InvalidDataException("В копии отсутствуют таблицы библиотеки.");
        OrderCategories(snapshot.Categories);
        if (snapshot.Items.Any(i => i is null) || snapshot.Versions.Any(i => i is null)
            || snapshot.Items.Select(i => i.Id).Distinct().Count() != snapshot.Items.Count)
            throw new InvalidDataException("В копии повторяются или отсутствуют компоненты.");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var root = Path.Combine(Path.GetTempPath(), "pdmlite-archive-validation");
        foreach (var entry in entries)
        {
            if (!entry.StartsWith("files/", StringComparison.Ordinal) || entry.EndsWith('/')) continue;
            string path;
            try { path = ContainedPath.Resolve(root, entry[6..]); }
            catch (ArgumentException ex) { throw new InvalidDataException("Недопустимый путь в копии: " + entry, ex); }
            if (!paths.Add(path)) throw new InvalidDataException("В копии повторяется путь файла: " + entry);
        }
        foreach (var relative in snapshot.Items.Select(i => i.RelativePath).Concat(snapshot.Versions.Select(i => i.RelativePath)))
        {
            try { ContainedPath.Resolve(root, relative); }
            catch (ArgumentException ex) { throw new InvalidDataException("Недопустимый путь компонента в копии.", ex); }
        }
    }
}
