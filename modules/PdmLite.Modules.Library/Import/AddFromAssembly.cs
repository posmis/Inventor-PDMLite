using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PdmLite.Core.Documents;
using PdmLite.UI.Shared;

namespace PdmLite.Modules.Library.Import;

/// <summary>
/// Inventor: выделенные в сборке детали и подсборки → окно «Добавить в библиотеку» → после внесения
/// вхождения в активной сборке заменяются компонентами библиотеки.
/// </summary>
public sealed class AddFromAssembly(IServiceProvider services, IAssemblyComponents assembly, ILogger<AddFromAssembly> logger)
{
    public async Task RunAsync()
    {
        ComponentSelection selection;
        try
        {
            selection = await assembly.GetSelectionAsync();
        }
        catch (InvalidOperationException ex)
        {
            UiMessages.Info(ex.Message);
            return;
        }

        if (selection.Selected.Count == 0)
        {
            UiMessages.Info("Выделите в сборке (в браузере или на экране) детали или подсборки, которые нужно добавить в библиотеку.");
            return;
        }

        if (selection.Unsaved.Count > 0)
        {
            var names = string.Join("\n", selection.Unsaved.Select(Path.GetFileName));
            if (MessageBox.Show($"В библиотеку копируется файл с диска. Есть несохранённые изменения:\n{names}\n\nСохранить их и продолжить?",
                    UiMessages.Caption, MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
                return;
            try
            {
                await assembly.SaveDocumentsAsync(selection.Unsaved);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Не удалось сохранить документы");
                UiMessages.Error("Не удалось сохранить документы.", ex);
                return;
            }
        }

        var viewModel = services.GetRequiredService<ImportViewModel>();
        var selected = selection.Selected.ToHashSet(StringComparer.OrdinalIgnoreCase);
        viewModel.AfterCommit = result => ReplaceAsync(result.Committed.Where(c => selected.Contains(c.Key)));

        var window = new ImportWindow(viewModel, services) { Title = "Добавить в библиотеку — из сборки" };
        new WindowInteropHelper(window).Owner = Process.GetCurrentProcess().MainWindowHandle;
        window.Loaded += async (_, _) =>
        {
            await viewModel.AddAsync([.. selection.Selected, .. selection.Components]);

            // Уже внесённые (тот же файл) — не вносим повторно, но в сборке можно заменить сразу.
            var known = viewModel.ExcludeAlreadyInLibrary().Where(k => selected.Contains(k.Key)).ToList();
            if (known.Count == 0)
                return;
            var names = string.Join("\n", known.Select(k => Path.GetFileName(k.Key)));
            if (MessageBox.Show(window, $"Уже есть в библиотеке:\n{names}\n\nЗаменить их в сборке компонентами библиотеки?",
                    UiMessages.Caption, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;
            try
            {
                UiMessages.Info(await ReplaceAsync(known) ?? "Заменять нечего.");
            }
            catch (Exception ex)
            {
                // Обработчик async void: без перехвата ошибка закрыла бы Inventor.
                logger.LogError(ex, "Замена компонентов в сборке: ошибка");
                UiMessages.Error("Не удалось заменить компоненты в сборке.", ex);
            }
        };
        window.ShowDialog(); // модально: внутри Inventor так работает ввод с клавиатуры
    }

    /// <summary>Заменяет в активной сборке исходные файлы библиотечными; текст итога.</summary>
    private async Task<string?> ReplaceAsync(IEnumerable<KeyValuePair<string, string>> map)
    {
        var replacements = map.Select(m => new ComponentReplacement(m.Key, m.Value)).ToList();
        if (replacements.Count == 0)
            return null;

        var result = await assembly.ReplaceAsync(replacements);
        var text = $"В сборке заменено на компоненты библиотеки: {result.Replaced}. Сохраните сборку.";
        if (result.BackupFolder is not null)
            text += $"\nКопии сборок до замены: {result.BackupFolder}";
        if (result.Problems.Count > 0)
            text += "\n\nНе заменено:\n" + string.Join("\n", result.Problems);
        return text;
    }
}
