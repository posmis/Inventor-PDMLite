using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using PdmLite.Core.Documents;

namespace PdmLite.Apprentice;

/// <summary>
/// iProperties и ссылки файлов Inventor через Apprentice Server — без запуска Inventor.
/// Нужен установленный Inventor или Inventor View.
/// </summary>
public sealed class ApprenticePropertyStore : IDocumentPropertyStore, IDisposable
{
    private const string SummarySet = "Inventor Summary Information";
    private const string DesignTrackingSet = "Design Tracking Properties";
    private const string UserDefinedSet = "Inventor User Defined Properties";
    private const string ContentCenterSet = "Content Library Component Properties";

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
        Run(() => WithDocument(path, doc =>
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
        }), cancellationToken);

    public Task WriteAsync(string path, DocumentPropertyChanges changes, CancellationToken cancellationToken = default) =>
        Run(() => WithDocument(path, doc =>
        {
            var sets = doc.PropertySets;
            if (changes.PartNumber is not null)
                SetValue(sets, DesignTrackingSet, "Part Number", changes.PartNumber);
            if (changes.Description is not null)
                SetValue(sets, DesignTrackingSet, "Description", changes.Description);
            if (changes.Title is not null)
                SetValue(sets, SummarySet, "Title", changes.Title);

            // Метку Content Center снимаем удалением её набора свойств.
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

            sets.FlushToFile();
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
