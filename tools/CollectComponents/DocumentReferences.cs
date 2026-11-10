namespace CollectComponents;

internal sealed class IncompleteReferencesException(IReadOnlyList<DocumentReference> references, IEnumerable<string> problems)
    : Exception(string.Join("; ", problems))
{
    public IReadOnlyList<DocumentReference> References { get; } = references;
}

/// <summary>Чтение COM-коллекций с сохранением найденных ссылок и явным учётом неполноты.</summary>
internal static class DocumentReferences
{
    public static IReadOnlyList<DocumentReference> Read(object value)
    {
        dynamic document = value;
        var result = new List<DocumentReference>();
        var problems = new List<string>();
        try
        {
            dynamic descriptors = document.File.ReferencedFileDescriptors;
            foreach (dynamic descriptor in Items((object)descriptors, (int)descriptors.Count, problems))
            {
                var full = Get(() => (string?)descriptor.FullFileName);
                var relative = Get(() => (string?)descriptor.RelativeFileName);
                var logical = Get(() => (string?)descriptor.LogicalFileName);
                Add(full, relative, logical);
            }
        }
        catch (Exception ex) { problems.Add("ReferencedFileDescriptors: " + ex.Message); }

        // Найденные документы дополняют частичный список, но не подтверждают полноту:
        // отсутствующие файлы в ReferencedDocuments могут вообще не попасть.
        var primaryEmpty = result.Count == 0 && problems.Count == 0;
        if (result.Count == 0 || problems.Count > 0)
        {
            try
            {
                dynamic documents = document.ReferencedDocuments;
                foreach (dynamic referenced in Items((object)documents, (int)documents.Count, problems))
                    Add(Get(() => (string?)referenced.FullFileName), null, null);
            }
            catch (Exception ex) { problems.Add("ReferencedDocuments: " + ex.Message); }
        }
        if (primaryEmpty && result.Count > 0)
            problems.Add("Резервное чтение обнаружило ссылки при пустой коллекции дескрипторов");
        if (problems.Count > 0) throw new IncompleteReferencesException(result, problems);
        return result;

        void Add(string? full, string? relative, string? logical)
        {
            var name = new[] { full, relative, logical }.Where(v => !string.IsNullOrWhiteSpace(v))
                .Select(v => Path.GetFileName(v!.Replace('\\', '/').TrimEnd('/'))).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
            if (name is null) { problems.Add("Не удалось определить имя ссылки"); return; }
            if (!result.Any(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                result.Add(new DocumentReference(name, string.IsNullOrWhiteSpace(full) ? null : full));
        }
    }

    private static List<object> Items(object value, int count, List<string> problems)
    {
        dynamic collection = value;
        var result = new List<object>();
        foreach (var read in new Func<int, object>[] { i => collection[i], i => collection.Item(i) })
        {
            var attempt = new List<object>();
            try
            {
                for (var i = 1; i <= count; i++) attempt.Add(read(i));
                return attempt;
            }
            catch { if (attempt.Count > result.Count) result = attempt; }
        }
        var enumerated = new List<object>();
        try
        {
            foreach (var item in (System.Collections.IEnumerable)collection) enumerated.Add(item);
            if (enumerated.Count == count) return enumerated;
        }
        catch { /* Частичный перебор учитывается ниже. */ }
        if (enumerated.Count > result.Count) result = enumerated;
        problems.Add($"Коллекция ссылок прочитана не полностью: {result.Count} из {count}");
        return result;
    }

    private static string? Get(Func<string?> read)
    {
        try { return read(); }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        { return null; }
    }
}
