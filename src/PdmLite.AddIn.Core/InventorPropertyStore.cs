using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using PdmLite.Core.Documents;
using PdmLite.Core.Threading;
using PdmLite.Interop;

namespace PdmLite.AddIn.Core;

/// <summary>
/// iProperties и ссылки файлов внутри Inventor. Документ, открытый в Inventor, читается через Inventor API
/// (быстро, как есть). Остальное — через Apprentice в отдельном процессе (<see cref="ApprenticeHost"/>):
/// Inventor API не может открыть и сохранить копию файла Content Center, исходник которого открыт в сборке.
/// Если отдельный процесс недоступен (нет PdmLite Explorer), — через Inventor API, как раньше.
/// </summary>
public sealed class InventorPropertyStore(Inv.Application inventor, IUiDispatcher ui, ApprenticeHost apprentice,
    ILogger<InventorPropertyStore> logger) : IDocumentPropertyStore
{
    public bool IsAvailable => true;
    public string? UnavailableReason => null;

    public Task<DocumentProperties> ReadAsync(string path, CancellationToken cancellationToken = default) => ReadCoreAsync(path, true, cancellationToken);

    public Task<DocumentProperties> ReadWithoutGeometryAsync(string path, CancellationToken cancellationToken = default) =>
        ReadCoreAsync(path, false, cancellationToken);

    private async Task<DocumentProperties> ReadCoreAsync(string path, bool geometry, CancellationToken ct)
    {
        var open = await ui.InvokeAsync(() => FindOpen(path) is not null).ConfigureAwait(false);
        if (!open && apprentice.IsAvailable)
            return await apprentice.ReadAsync(path, geometry, ct).ConfigureAwait(false);
        return await ui.InvokeAsync(() => WithDocument(path, save: false, doc => InventorPropertySets.Read(doc.PropertySets, doc.File,
            geometry ? InventorPropertySets.Geometry(doc, ex => logger.LogDebug(ex, "Масс-свойства не прочитаны: {Path}", path)) : (null, null))))
            .ConfigureAwait(false);
    }

    public Task WriteAsync(string path, DocumentPropertyChanges changes, CancellationToken cancellationToken = default) =>
        apprentice.IsAvailable
            ? apprentice.WriteAsync(path, changes, cancellationToken)
            : ui.InvokeAsync(() => WithDocument(path, save: true, doc =>
            {
                InventorPropertySets.Apply(doc.PropertySets, changes);
                return true;
            }));

    public Task ReplaceReferencesAsync(string path, IReadOnlyDictionary<string, string> map, CancellationToken cancellationToken = default) =>
        apprentice.IsAvailable
            ? apprentice.ReplaceReferencesAsync(path, map, cancellationToken)
            : ReplaceInInventorAsync(path, map);

    private Task ReplaceInInventorAsync(string path, IReadOnlyDictionary<string, string> map) =>
        ui.InvokeAsync(() => WithDocument(path, save: false, doc =>
        {
            // Чертёж, презентация, производная деталь — заменой ссылки файла (как «Замена ссылок» в Design Assistant).
            if (doc is not Inv.AssemblyDocument assembly)
            {
                var count = 0;
                foreach (Inv.FileDescriptor descriptor in doc.File.ReferencedFileDescriptors)
                {
                    if (map.TryGetValue(descriptor.FullFileName, out var target))
                    {
                        descriptor.ReplaceReference(target);
                        count++;
                    }
                }
                if (count > 0)
                    doc.Save();
                return count;
            }

            // В открытой сборке ссылки меняются заменой вхождений (как «Заменить компонент»).
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
                InventorAssemblyComponents.FixNames(assembly, map.Values.ToHashSet(StringComparer.OrdinalIgnoreCase), apply: true, logger);
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
