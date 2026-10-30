using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using PdmLite.Core.Documents;
using PdmLite.Core.Threading;

namespace PdmLite.AddIn.Core;

/// <summary>
/// iProperties и ссылки файлов через Inventor API — внутри Inventor (Apprentice в процессе Inventor
/// не работает). Документ, уже открытый в Inventor, читается как есть; остальные открываются
/// невидимо и закрываются после работы.
/// </summary>
public sealed class InventorPropertyStore(Inv.Application inventor, IUiDispatcher ui, ILogger<InventorPropertyStore> logger) : IDocumentPropertyStore
{
    private const string SummarySet = "Inventor Summary Information";
    private const string DesignTrackingSet = "Design Tracking Properties";
    private const string UserDefinedSet = "Inventor User Defined Properties";
    private const string ContentCenterSet = "Content Library Component Properties";

    public bool IsAvailable => true;
    public string? UnavailableReason => null;

    public Task<DocumentProperties> ReadAsync(string path, CancellationToken cancellationToken = default) =>
        ui.InvokeAsync(() => WithDocument(path, save: false, doc =>
        {
            var sets = doc.PropertySets;
            var custom = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (FindSet(sets, UserDefinedSet) is { } user)
                foreach (Inv.Property property in user)
                    custom[property.Name] = Convert.ToString(property.Value) ?? "";

            var references = new List<string>();
            foreach (Inv.FileDescriptor descriptor in doc.File.ReferencedFileDescriptors)
                if (!string.IsNullOrEmpty(descriptor.FullFileName))
                    references.Add(descriptor.FullFileName);

            return new DocumentProperties
            {
                PartNumber = GetValue(sets, DesignTrackingSet, "Part Number"),
                Description = GetValue(sets, DesignTrackingSet, "Description"),
                Title = GetValue(sets, SummarySet, "Title"),
                IsContentCenter = FindSet(sets, ContentCenterSet) is not null,
                Custom = custom,
                References = references,
            };
        }));

    public Task WriteAsync(string path, DocumentPropertyChanges changes, CancellationToken cancellationToken = default) =>
        ui.InvokeAsync(() => WithDocument(path, save: true, doc =>
        {
            var sets = doc.PropertySets;
            if (changes.PartNumber is not null)
                SetValue(sets, DesignTrackingSet, "Part Number", changes.PartNumber);
            if (changes.Description is not null)
                SetValue(sets, DesignTrackingSet, "Description", changes.Description);
            if (changes.Title is not null)
                SetValue(sets, SummarySet, "Title", changes.Title);

            if (changes.RemoveContentCenterLink && FindSet(sets, ContentCenterSet) is { } contentCenter)
            {
                try
                {
                    contentCenter.Delete();
                }
                catch (COMException ex)
                {
                    throw new InvalidOperationException($"Не удалось снять метку Content Center: {ex.Message}", ex);
                }
            }

            if (changes.Custom.Count > 0)
            {
                var user = FindSet(sets, UserDefinedSet) ?? throw new InvalidOperationException("Нет набора пользовательских свойств.");
                foreach (var (name, value) in changes.Custom)
                {
                    if (FindProperty(user, name) is { } property)
                        property.Value = value;
                    else
                        user.Add(value, name, Type.Missing);
                }
            }
            return true;
        }));

    public Task ReplaceReferencesAsync(string path, IReadOnlyDictionary<string, string> map, CancellationToken cancellationToken = default) =>
        ui.InvokeAsync(() => WithDocument(path, save: true, doc =>
        {
            // В открытой сборке ссылки меняются заменой вхождений (как «Заменить компонент»).
            if (doc is not Inv.AssemblyDocument assembly)
                return 0;
            var replaced = 0;
            var groups = assembly.ComponentDefinition.Occurrences.Cast<Inv.ComponentOccurrence>()
                .Select(o => (Occurrence: o, Path: o.ReferencedDocumentDescriptor?.FullDocumentName))
                .Where(o => o.Path is not null && map.ContainsKey(o.Path))
                .GroupBy(o => o.Path!, StringComparer.OrdinalIgnoreCase)
                .ToList();
            foreach (var group in groups)
            {
                group.First().Occurrence.Replace(map[group.Key], true);
                replaced++;
            }
            return replaced;
        }));

    /// <summary>
    /// Работа с документом: уже открытый — как есть (сохраняется, только если просили); иначе
    /// открывается невидимо и закрывается после работы.
    /// </summary>
    private T WithDocument<T>(string path, bool save, Func<Inv._Document, T> work)
    {
        var opened = FindOpen(path);
        var document = opened ?? (Inv._Document)inventor.Documents.Open(path, false);
        try
        {
            var result = work(document);
            if (save)
                document.Save();
            return result;
        }
        finally
        {
            if (opened is null)
            {
                try
                {
                    document.Close(true);
                }
                catch (COMException ex)
                {
                    logger.LogWarning(ex, "Не удалось закрыть {Path}", path);
                }
            }
        }
    }

    private Inv._Document? FindOpen(string path)
    {
        foreach (Inv._Document document in inventor.Documents)
            if (string.Equals(document.FullFileName, path, StringComparison.OrdinalIgnoreCase))
                return document;
        return null;
    }

    private static Inv.PropertySet? FindSet(Inv.PropertySets sets, string name)
    {
        foreach (Inv.PropertySet set in sets)
            if (set.Name == name)
                return set;
        return null;
    }

    private static Inv.Property? FindProperty(Inv.PropertySet set, string name)
    {
        foreach (Inv.Property property in set)
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                return property;
        return null;
    }

    private static string? GetValue(Inv.PropertySets sets, string setName, string propertyName) =>
        FindSet(sets, setName) is { } set && FindProperty(set, propertyName) is { } property
            ? Convert.ToString(property.Value)
            : null;

    private static void SetValue(Inv.PropertySets sets, string setName, string propertyName, string value)
    {
        var property = FindSet(sets, setName) is { } set ? FindProperty(set, propertyName) : null;
        if (property is null)
            throw new InvalidOperationException($"Нет свойства «{propertyName}».");
        property.Value = value;
    }
}
