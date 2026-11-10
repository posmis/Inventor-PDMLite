using Microsoft.Extensions.Logging;
using PdmLite.Core.Documents;
using PdmLite.Library.Storage;
using PdmLite.Modules.Library.Catalog;
using PdmLite.UI.Shared;

namespace PdmLite.Modules.Library.Checks;

/// <summary>
/// «Имена в обозревателе»: вхождения компонентов библиотеки в открытой сборке (например, оставшиеся от
/// Content Center) получают имя по файлу. Сначала список «сейчас → станет», затем переименование.
/// </summary>
public sealed class OccurrenceNamesCommand(IAssemblyComponents assembly, Func<LibraryStorage> storage, ILogger<OccurrenceNamesCommand> logger)
{
    public async Task RunAsync()
    {
        try
        {
            var inspection = await assembly.InspectActiveAsync();
            var root = storage();
            var libraryFiles = inspection.Components.Where(c => !c.IsMissing && root.Contains(c.Path)).Select(c => c.Path).ToList();
            var plan = await assembly.FixOccurrenceNamesAsync(libraryFiles, apply: false, expectedAssemblyPath: inspection.AssemblyPath);
            if (plan.Count == 0)
            {
                UiMessages.Info("Имена компонентов библиотеки в обозревателе уже в порядке.");
                return;
            }

            var confirm = new ConfirmChangesWindow("Имена в обозревателе",
                $"Будут переименованы вхождения компонентов библиотеки: {plan.Count}.",
                "Имя вхождения станет как у файла библиотеки: «обозначение:номер».",
                "Меняются только имена узлов в обозревателе — компоненты, их положение и зависимости не меняются. Сохраните сборку.",
                "Переименовать", ("Сборка", "Что", "Сейчас", "Станет"),
                plan.Select(r => new ChangeLine(r.Assembly, "Имя в обозревателе", r.OldName, r.NewName)).ToList());
            if (confirm.ShowDialog() != true)
                return;

            var done = await assembly.FixOccurrenceNamesAsync(libraryFiles, apply: true, expectedAssemblyPath: inspection.AssemblyPath);
            UiMessages.Info($"Переименовано вхождений: {done.Count}. Сохраните сборку.");
        }
        catch (InvalidOperationException ex)
        {
            UiMessages.Info(ex.Message); // например, активный документ — не сборка
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Не удалось обновить имена вхождений");
            UiMessages.Error("Не удалось обновить имена в обозревателе.", ex);
        }
    }
}
