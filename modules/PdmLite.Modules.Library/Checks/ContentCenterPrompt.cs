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
    ILogger<ContentCenterPrompt> logger) : IDisposable, IAsyncDisposable
{
    private readonly ContentCenterPromptAttempts _attempts = new();
    private IDisposable? _subscription;
    private readonly ContentCenterPromptLifetime _lifetime = new();

    public void Attach() =>
        // Откладываем до окончания обработки события Inventor: окно поверх его события недопустимо.
        _subscription = events.Subscribe<ContentCenterComponentPlaced>(e => ui.Post(() => _lifetime.RunAsync(ct => PromptAsync(e, ct)).Forget()));

    private async Task PromptAsync(ContentCenterComponentPlaced placed, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            // Настройки меняются в Explorer, а предупреждение работает в процессе Inventor.
            if (!settings.Reload().WarnOnContentCenter || storage().Contains(placed.ComponentPath))
                return;

            await _attempts.RunAsync(placed.AssemblyPath + "|" + placed.ComponentPath, async markShown =>
            {
                var catalog = await items.LoadCatalogAsync(ct);
                var library = await items.ListAsync(catalog, ct);
                ct.ThrowIfCancellationRequested();
                var active = await assembly.InspectActiveAsync(ct);
                AssemblyTarget.Verify(placed.AssemblyPath, active.AssemblyPath, ct);
                var check = AssemblyCheck.Run([new AssemblyComponentInfo(placed.ComponentPath, placed.PartNumber, true, 1)], library, storage())
                    .Findings.SingleOrDefault();
                var name = check?.Designation ?? placed.PartNumber ?? Path.GetFileNameWithoutExtension(placed.ComponentPath);

                if (check?.Replacement is not { } replacement)
                {
                    MessageBox.Show(
                        $"Компонент «{name}» вставлен из Content Center.\n\nВ библиотеке PdmLite такого компонента нет. " +
                        "Добавьте его в библиотеку (PdmLite Explorer → Добавить в библиотеку), чтобы в следующий раз брать оттуда.",
                        UiMessages.Caption, MessageBoxButton.OK, MessageBoxImage.Warning);
                    markShown();
                    return;
                }

                var answer = MessageBox.Show(
                    $"Компонент «{name}» вставлен из Content Center.\n\n" +
                    (check.IsExact ? "В библиотеке PdmLite есть такой же" : "В библиотеке PdmLite есть похожий (проверьте обозначение)") +
                    $":\n«{replacement.Designation}».\n\n" +
                    "Заменить на компонент из библиотеки? Будут заменены все вхождения в этой сборке.",
                    UiMessages.Caption, MessageBoxButton.YesNo, MessageBoxImage.Warning);
                markShown();
                if (answer != MessageBoxResult.Yes)
                    return;

                var result = await assembly.ReplaceAsync([new ComponentReplacement(placed.ComponentPath, storage().GetFullPath(replacement.RelativePath))], ct, placed.AssemblyPath);
                if (result.Problems.Count > 0)
                    UiMessages.Error("Не удалось заменить компонент:\n" + string.Join("\n", result.Problems)
                                     + "\n\nЗавершите команду вставки и запустите «Проверить сборку».");
            });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Предупреждение о Content Center: ошибка");
            UiMessages.Error("Не удалось предложить замену компонента из Content Center. " +
                             "Завершите команду вставки и запустите «Проверить сборку».", ex);
        }
    }

    public void Dispose() { _subscription?.Dispose(); _lifetime.Dispose(); }
    public async ValueTask DisposeAsync() { Dispose(); await _lifetime.DisposeAsync().ConfigureAwait(false); }
}
