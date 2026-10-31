using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using PdmLite.Core.Documents;
using PdmLite.Interop;

namespace PdmLite.Apprentice;

/// <summary>
/// iProperties и ссылки файлов Inventor через Apprentice Server — без запуска Inventor.
/// Нужен установленный Inventor или Inventor View.
/// </summary>
public sealed class ApprenticePropertyStore : IDocumentPropertyStore, IDisposable
{
    private readonly ILogger<ApprenticePropertyStore> _logger;
    private readonly StaWorker _worker = new("PdmLite Apprentice");
    private readonly Lazy<string?> _initError;
    private Inv.ApprenticeServerComponent? _apprentice;

    public ApprenticePropertyStore(ILogger<ApprenticePropertyStore> logger)
    {
        _logger = logger;
        _initError = new Lazy<string?>(() => _worker.InvokeAsync(Initialize).GetAwaiter().GetResult());
    }

    public bool IsAvailable => _initError.Value is null;
    public string? UnavailableReason => _initError.Value;

    private string? Initialize()
    {
        try
        {
            _apprentice = new Inv.ApprenticeServerComponent();
            _logger.LogInformation("Apprentice Server запущен");
            return null;
        }
        catch (Exception ex) when (ex is COMException or TypeLoadException or FileNotFoundException)
        {
            _logger.LogWarning(ex, "Apprentice Server недоступен");
            return "не найден Inventor или Inventor View (Apprentice Server). Установите бесплатный Inventor View.";
        }
    }

    public Task<DocumentProperties> ReadAsync(string path, CancellationToken cancellationToken = default) =>
        Run(() => WithDocument(path, doc => InventorPropertySets.Read(doc.PropertySets, doc.File,
            InventorPropertySets.Geometry(doc, ex => _logger.LogDebug(ex, "Масс-свойства не прочитаны: {Path}", path)))), cancellationToken);

    public Task<DocumentProperties> ReadWithoutGeometryAsync(string path, CancellationToken cancellationToken = default) =>
        Run(() => WithDocument(path, doc => InventorPropertySets.Read(doc.PropertySets, doc.File, (null, null))), cancellationToken);

    public Task WriteAsync(string path, DocumentPropertyChanges changes, CancellationToken cancellationToken = default) =>
        Run(() => WithDocument(path, doc =>
        {
            InventorPropertySets.Apply(doc.PropertySets, changes);
            doc.PropertySets.FlushToFile();
            return true;
        }), cancellationToken);

    public Task ReplaceReferencesAsync(string path, IReadOnlyDictionary<string, string> map, CancellationToken cancellationToken = default) =>
        Run(() => WithDocument(path, doc =>
        {
            var replaced = 0;
            foreach (Inv.FileDescriptor descriptor in doc.File.ReferencedFileDescriptors)
            {
                if (map.TryGetValue(descriptor.FullFileName, out var target))
                {
                    descriptor.ReplaceReference(target);
                    replaced++;
                }
            }

            if (replaced > 0)
            {
                var save = _apprentice!.FileSaveAs;
                save.AddFileToSave(doc, doc.FullFileName);
                save.ExecuteSave();
            }
            return replaced;
        }), cancellationToken);

    private async Task<T> Run<T>(Func<T> func, CancellationToken ct)
    {
        if (!IsAvailable)
            throw new InvalidOperationException($"Свойства файлов недоступны: {UnavailableReason}");
        return await _worker.InvokeAsync(func, ct).ConfigureAwait(false);
    }

    private T WithDocument<T>(string path, Func<Inv.ApprenticeServerDocument, T> func)
    {
        var doc = _apprentice!.Open(path);
        try
        {
            return func(doc);
        }
        finally
        {
            doc.Close();
            Marshal.ReleaseComObject(doc);
        }
    }

    public void Dispose()
    {
        if (_apprentice is not null)
        {
            var apprentice = _apprentice;
            _apprentice = null;
            _worker.InvokeAsync(() => Marshal.ReleaseComObject(apprentice)).Wait(TimeSpan.FromSeconds(5));
        }
        _worker.Dispose();
    }
}
