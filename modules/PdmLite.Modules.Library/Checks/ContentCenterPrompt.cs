using System.Windows;
using Microsoft.Extensions.Logging;
using PdmLite.Core.Documents;
using PdmLite.Core.Events;
using PdmLite.Core.Threading;
using PdmLite.Hosting;
using PdmLite.Library.Checks;
using PdmLite.Library.Items;
using PdmLite.Library.Storage;
using PdmLite.UI.Shared;

namespace PdmLite.Modules.Library.Checks;

/// <summary>
/// Когда в сборку вставлен компонент из Content Center — предупреждает и предлагает заменить его
/// таким же компонентом из библиотеки. Каждый файл в каждой сборке — один раз за сеанс.
/// </summary>
public sealed class ContentCenterPrompt(
    IEventBus events,
    IUiDispatcher ui,
    IAssemblyComponents assembly,
    LibraryItemService items,
    Func<LibraryStorage> storage,
    JsonSettings<LibrarySettings> settings,
    ILogger<ContentCenterPrompt> logger) : IDisposable
{
    private readonly HashSet<string> _asked = new(StringComparer.OrdinalIgnoreCase);
    private IDisposable? _subscription;

    public void Attach() =>
        // Откладываем до окончания обработки события Inventor: окно поверх его события недопустимо.
        _subscription = events.Subscribe<ContentCenterComponentPlaced>(e => ui.Post(() => PromptAsync(e).Forget()));

    private async Task PromptAsync(ContentCenterComponentPlaced placed)
    {
        // Настройки меняются в Explorer, а предупреждение работает в процессе Inventor.
        if (!settings.Reload().WarnOnContentCenter || !_asked.Add(placed.AssemblyPath + "|" + placed.ComponentPath))
            return;
        // Файл из нашей библиотеки — всё правильно, даже если в нём осталась метка Content Center.
        if (storage().Contains(placed.ComponentPath))
            return;

        try
        {
            var catalog = await items.LoadCatalogAsync();
            var library = await items.ListAsync(catalog);
            var check = AssemblyCheck.Run([new AssemblyComponentInfo(placed.ComponentPath, placed.PartNumber, true, 1)], library, storage())
                .Findings.SingleOrDefault();
            var name = check?.Designation ?? placed.PartNumber ?? Path.GetFileNameWithoutExtension(placed.ComponentPath);

            if (check?.Replacement is not { } replacement)
            {
                MessageBox.Show(
                    $"Компонент «{name}» вставлен из Content Center.\n\nВ библиотеке PdmLite такого компонента нет. " +
                    "Добавьте его в библиотеку (PdmLite Explorer → Добавить в библиотеку), чтобы в следующий раз брать оттуда.",
                    UiMessages.Caption, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var answer = MessageBox.Show(
                $"Компонент «{name}» вставлен из Content Center.\n\n" +
                (check.IsExact ? "В библиотеке PdmLite есть такой же" : "В библиотеке PdmLite есть похожий (проверьте обозначение)") +
                $":\n«{replacement.Designation}».\n\n" +
                "Заменить на компонент из библиотеки? Будут заменены все вхождения в этой сборке.",
                UiMessages.Caption, MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes)
                return;

            var result = await assembly.ReplaceAsync([new ComponentReplacement(placed.ComponentPath, storage().GetFullPath(replacement.RelativePath))]);
            if (result.Problems.Count > 0)
                UiMessages.Error("Не удалось заменить компонент:\n" + string.Join("\n", result.Problems)
                                 + "\n\nЗавершите команду вставки и запустите «Проверить сборку».");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Предупреждение о Content Center: ошибка");
            UiMessages.Error("Не удалось предложить замену компонента из Content Center. " +
                             "Завершите команду вставки и запустите «Проверить сборку».", ex);
        }
    }

    public void Dispose() => _subscription?.Dispose();
}
