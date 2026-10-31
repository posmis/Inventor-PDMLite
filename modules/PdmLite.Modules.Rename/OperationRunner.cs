using PdmLite.Core.Documents;
using PdmLite.Index;
using PdmLite.Index.Operations;

namespace PdmLite.Modules.Rename;

/// <summary>
/// Выполнение плана с документами, открытыми в Inventor: внутри Inventor — сохранить, закрыть и после
/// операции открыть снова (по новым путям); из Explorer — попросить закрыть.
/// </summary>
internal static class OperationRunner
{
    /// <returns>Итог или <c>null</c>, если операция не начата (отказ, документы открыты в Inventor).</returns>
    public static async Task<FileOperationResult?> RunAsync(ProjectFileOperations operations, IOpenDocuments openDocuments, FileOperationPlan plan,
        Func<string, bool> confirm, Action<string> blocked, Action<string> warning, IProgress<string> progress, ISet<string>? wasOpen = null)
    {
        var involved = plan.InvolvedFiles.Select(ReferenceIndexService.Key).ToHashSet();
        var open = (await openDocuments.ListAsync()).Where(d => involved.Contains(ReferenceIndexService.Key(d.Path))).ToList();
        foreach (var document in open.Where(d => d.IsVisible))
            wasOpen?.Add(document.Path); // запоминаем и при отказе: пользователь закроет сам, а после операции откроем снова
        IReadOnlyList<string> closed = [];
        if (open.Count > 0)
        {
            var names = string.Join(Environment.NewLine, open.Select(d => "  " + Path.GetFileName(d.Path)).Distinct().Take(15));
            if (!openDocuments.CanClose)
            {
                blocked("Эти документы загружены в Inventor — закройте их (или сборки, в которые они входят) и нажмите снова:"
                        + Environment.NewLine + names);
                return null;
            }
            if (!confirm("Документы открыты в Inventor:" + Environment.NewLine + names + Environment.NewLine + Environment.NewLine
                         + "Они будут сохранены и закрыты, а после операции открыты снова. Продолжить?"))
                return null;
            progress.Report("Закрытие документов в Inventor…");
            closed = await openDocuments.CloseAsync(plan.InvolvedFiles);
        }

        var done = false;
        try
        {
            var result = await operations.ExecuteAsync(plan, progress);
            done = true;
            return result;
        }
        finally
        {
            if (closed.Count > 0)
            {
                try
                {
                    await openDocuments.OpenAsync(closed.Select(p => done ? plan.After(p) : p).Where(File.Exists).ToList());
                }
                catch (Exception ex)
                {
                    warning($"Документы не открылись снова: {ex.Message}");
                }
            }
        }
    }

    public static string Summary(string action, FileOperationResult result) =>
        $"{action}: файлов {result.Moved}, ссылки обновлены в {result.ReferencesUpdated} док., свойства записаны в {result.PropertiesWritten}."
        + (result.Warnings.Count == 0 ? "" : Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine, result.Warnings))
        + Environment.NewLine + Environment.NewLine + "Журнал и копии изменённых документов: " + result.BackupFolder;
}
