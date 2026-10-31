using Microsoft.Extensions.Logging;
using PdmLite.Core.Documents;
using PdmLite.Core.Threading;

namespace PdmLite.AddIn.Core;

/// <summary>
/// Документы, загруженные в Inventor: список, закрытие перед переименованием (с сохранением) и повторное открытие.
/// Inventor API — только в главном потоке (<see cref="IUiDispatcher"/>).
/// </summary>
public sealed class InventorOpenDocuments(Inv.Application inventor, IUiDispatcher ui, ILogger<InventorOpenDocuments> logger) : IOpenDocuments
{
    public bool CanClose => true;

    public Task<IReadOnlyList<OpenDocument>> ListAsync(CancellationToken cancellationToken = default) =>
        ui.InvokeAsync<IReadOnlyList<OpenDocument>>(() => List(inventor));

    /// <summary>Все загруженные документы (видимые и подгруженные как компоненты).</summary>
    internal static List<OpenDocument> List(Inv.Application inventor)
    {
        var visible = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Inv._Document document in inventor.Documents.VisibleDocuments)
            if (!string.IsNullOrEmpty(document.FullFileName))
                visible.Add(document.FullFileName);

        var result = new List<OpenDocument>();
        foreach (Inv._Document document in inventor.Documents)
        {
            var path = document.FullFileName;
            if (!string.IsNullOrEmpty(path))
                result.Add(new OpenDocument(path, document.Dirty, visible.Contains(path)));
        }
        return result;
    }

    public Task<IReadOnlyList<string>> CloseAsync(IReadOnlyCollection<string> affected, CancellationToken cancellationToken = default) =>
        ui.InvokeAsync<IReadOnlyList<string>>(() =>
        {
            var keys = new HashSet<string>(affected.Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);
            var toClose = new List<Inv._Document>();
            foreach (Inv._Document document in inventor.Documents.VisibleDocuments)
            {
                if (keys.Contains(document.FullFileName) || References(document).Any(keys.Contains))
                    toClose.Add(document);
            }

            var closed = new List<string>();
            foreach (var document in toClose)
            {
                var path = document.FullFileName;
                if (document.Dirty)
                    document.Save2(true); // вместе с изменёнными компонентами
                document.Close(true);
                closed.Add(path);
                logger.LogInformation("Закрыт перед операцией с файлами: {Path}", path);
            }
            // Подгруженные компоненты, на которые больше никто не ссылается, Inventor держит в памяти — выгружаем.
            inventor.Documents.CloseAll(true);

            var still = List(inventor).Where(d => keys.Contains(d.Path)).Select(d => Path.GetFileName(d.Path)).Distinct().ToList();
            if (still.Count > 0)
                throw new InvalidOperationException("В Inventor остались загруженными: " + string.Join(", ", still.Take(10))
                                                    + ". Закройте документы, в которые они входят, и повторите.");
            return closed;
        });

    public Task OpenAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default) =>
        ui.InvokeAsync(() =>
        {
            foreach (var path in paths)
            {
                try
                {
                    inventor.Documents.Open(path, true);
                }
                catch (System.Runtime.InteropServices.COMException ex)
                {
                    logger.LogWarning(ex, "Не открыт после операции с файлами: {Path}", path);
                }
            }
        });

    private static IEnumerable<string> References(Inv._Document document)
    {
        foreach (Inv._Document referenced in document.AllReferencedDocuments)
            if (!string.IsNullOrEmpty(referenced.FullFileName))
                yield return referenced.FullFileName;
    }
}
