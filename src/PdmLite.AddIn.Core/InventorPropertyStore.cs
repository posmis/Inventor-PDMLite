using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using PdmLite.Core.Documents;
using PdmLite.Core.Threading;
using PdmLite.Interop;

namespace PdmLite.AddIn.Core;

/// <summary>
/// iProperties и ссылки файлов через Inventor API — внутри Inventor (Apprentice в процессе Inventor
/// не работает). Документ, уже открытый в Inventor, читается как есть; остальные открываются
/// невидимо и закрываются после работы.
/// </summary>
public sealed class InventorPropertyStore(Inv.Application inventor, IUiDispatcher ui, ILogger<InventorPropertyStore> logger) : IDocumentPropertyStore
{
    public bool IsAvailable => true;
    public string? UnavailableReason => null;

    public Task<DocumentProperties> ReadAsync(string path, CancellationToken cancellationToken = default) =>
        ui.InvokeAsync(() => WithDocument(path, save: false, doc => InventorPropertySets.Read(doc.PropertySets, doc.File,
            InventorPropertySets.Geometry(doc, ex => logger.LogDebug(ex, "Масс-свойства не прочитаны: {Path}", path)))));

    public Task WriteAsync(string path, DocumentPropertyChanges changes, CancellationToken cancellationToken = default) =>
        ui.InvokeAsync(() => WithDocument(path, save: true, doc =>
        {
            InventorPropertySets.Apply(doc.PropertySets, changes);
            return true;
        }));

    public Task ReplaceReferencesAsync(string path, IReadOnlyDictionary<string, string> map, CancellationToken cancellationToken = default) =>
        ui.InvokeAsync(() => WithDocument(path, save: false, doc =>
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
            if (replaced > 0)
                doc.Save(); // без замен файл не трогаем
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
}
