using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PdmLite.Core.Events;
using PdmLite.Hosting;
using PdmLite.Library.Backup;
using PdmLite.Library.Storage;
using PdmLite.Modules.Library.Catalog;
using PdmLite.UI.Shared;

namespace PdmLite.Modules.Library.Backup;

/// <summary>Окно резервных копий: список, создание, восстановление (с окном «что изменится»), настройки.</summary>
public sealed partial class BackupViewModel(
    LibraryBackupService service,
    JsonSettings<LibrarySettings> settings,
    IEventBus events,
    ILogger<BackupViewModel> logger) : ObservableObject
{
    public ObservableCollection<BackupRow> Rows { get; } = [];

    /// <summary>Показ окна подтверждения (задаёт окно).</summary>
    public Func<ConfirmChangesWindow, bool> Ask { get; set; } = w => w.ShowDialog() == true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RestoreCommand), nameof(DeleteCommand), nameof(ShowCommand))]
    private BackupRow? _selected;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateCommand), nameof(RestoreCommand), nameof(DeleteCommand))]
    private bool _isBusy;

    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private string _summary = "";

    [ObservableProperty] private string _folder = settings.Value.BackupFolder;
    [ObservableProperty] private string _keep = settings.Value.BackupKeep.ToString();
    [ObservableProperty] private string _intervalDays = settings.Value.BackupIntervalDays.ToString();

    /// <summary>База или файлы восстановлены — каталог стоит перечитать.</summary>
    public bool Restored { get; private set; }

    [RelayCommand]
    public void Refresh()
    {
        var selected = Selected?.Info.Path;
        Rows.Clear();
        foreach (var backup in service.List())
            Rows.Add(new BackupRow(backup));
        Selected = Rows.FirstOrDefault(r => r.Info.Path == selected);
        var total = Rows.Sum(r => r.Info.Size);
        Summary = Rows.Count == 0
            ? "Копий пока нет."
            : $"Копий: {Rows.Count}, занимают {BackupRow.FormatSize(total)}. Последняя: {Rows[0].Created}.";
    }

    private bool CanCreate() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanCreate))]
    private async Task CreateAsync()
    {
        BackupInfo? created = null;
        await RunAsync("Резервная копия", async (progress, ct) => created = await service.CreateAsync(BackupReason.Manual, progress, ct));
        Refresh();
        if (created is not null)
        {
            Selected = Rows.FirstOrDefault(r => r.Info.Path == created.Path);
            UiMessages.Info($"Копия сделана: {created.FileName}\n\nКомпонентов: {created.Manifest.Items}, файлов: {created.Manifest.Files}, " +
                            $"размер архива: {BackupRow.FormatSize(created.Size)}.");
        }
    }

    private bool HasSelection() => Selected is not null;

    private bool CanRestore() => !IsBusy && Selected is not null;

    [RelayCommand(CanExecute = nameof(CanRestore))]
    private Task RestoreAsync() => RestoreFromAsync(Selected!.Info.Path);

    /// <summary>Восстановление: окно «что изменится» → копия текущего состояния → замена базы и файлов.</summary>
    public async Task RestoreFromAsync(string path)
    {
        RestorePlan plan;
        try
        {
            plan = await service.PlanRestoreAsync(path);
        }
        catch (Exception ex)
        {
            UiMessages.Error("Не удалось прочитать копию.", ex);
            return;
        }

        if (plan.IsDamaged)
        {
            UiMessages.Error("Копия повреждена, восстанавливать её нельзя:\n" + string.Join("\n", plan.MissingFiles.Take(20)));
            return;
        }

        var lines = plan.Added.Select(d => new ChangeLine(d, "Появится", "", "из копии"))
            .Concat(plan.Removed.Select(d => new ChangeLine(d, "Пропадёт", "есть сейчас", "")))
            .Concat(plan.Changed.Select(d => new ChangeLine(d, "Вернётся к копии", "сейчас", "как в копии")))
            .ToList();
        var m = plan.Backup.Manifest;
        var confirm = new ConfirmChangesWindow("Восстановление библиотеки",
            $"Библиотека будет заменена копией от {m.CreatedAt.ToLocalTime():dd.MM.yyyy HH:mm} ({LibraryBackupService.ReasonText(m.Reason)}).",
            $"Сейчас: компонентов {plan.ItemsNow}, файлов {plan.FilesNow}. В копии: компонентов {m.Items}, файлов {m.Files}. " +
            $"Появится: {plan.Added.Count}, пропадёт: {plan.Removed.Count}, изменится: {plan.Changed.Count}.",
            "Перед восстановлением автоматически делается копия текущего состояния — восстановление можно отменить, восстановив её. " +
            "Папка библиотеки и база заменяются целиком (правила, журнал, разделы — тоже). Закройте в Inventor документы из библиотеки.",
            "Восстановить", ("Компонент", "Что произойдёт", "", ""), lines);
        if (!Ask(confirm))
            return;

        BackupInfo? before = null;
        await RunAsync("Восстановление", async (progress, ct) => before = await service.RestoreAsync(path, progress, ct));
        Refresh();
        if (before is null)
            return;
        Restored = true;
        events.Publish(new LibraryChanged());
        UiMessages.Info($"Библиотека восстановлена.\n\nКопия состояния до восстановления: {before.FileName}.");
    }

    private bool CanDelete() => !IsBusy && Selected is not null;

    [RelayCommand(CanExecute = nameof(CanDelete))]
    private void Delete()
    {
        if (Selected is not { } row)
            return;
        if (System.Windows.MessageBox.Show($"Удалить копию {row.Info.FileName}?", UiMessages.Caption,
                System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Warning) != System.Windows.MessageBoxResult.OK)
            return;
        try
        {
            service.Delete(row.Info);
        }
        catch (Exception ex)
        {
            UiMessages.Error("Не удалось удалить копию.", ex);
        }
        Refresh();
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Show()
    {
        if (Selected is { } row)
            UiClipboard.ShowInExplorer(row.Info.Path);
    }

    /// <summary>Сохраняет настройки копий. Возвращает текст ошибки или <c>null</c>.</summary>
    public string? SaveSettings()
    {
        if (!int.TryParse(Keep, out var keep) || keep < 1)
            return "Сколько хранить — целое число от 1.";
        if (!int.TryParse(IntervalDays, out var days) || days < 0)
            return "Интервал — целое число дней (0 — не делать копии по расписанию).";
        if (string.IsNullOrWhiteSpace(Folder))
            return "Укажите папку для копий.";
        try
        {
            Directory.CreateDirectory(Folder);
        }
        catch (Exception ex)
        {
            return $"Не удалось создать папку: {ex.Message}";
        }

        settings.Value.BackupFolder = Path.GetFullPath(Folder);
        settings.Value.BackupKeep = keep;
        settings.Value.BackupIntervalDays = days;
        settings.Save();
        Refresh();
        return null;
    }

    private async Task RunAsync(string title, Func<IProgress<(int Done, int Total, string? Current)>, CancellationToken, Task> work)
    {
        IsBusy = true;
        Progress = 0;
        ProgressText = title + "…";
        var progress = new Progress<(int Done, int Total, string? Current)>(p =>
        {
            Progress = p.Total == 0 ? 100 : 100.0 * p.Done / p.Total;
            ProgressText = p.Current is null ? title : $"{title}: {p.Done + 1} из {p.Total} — {p.Current}";
        });
        try
        {
            await work(progress, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "{Title}: ошибка", title);
            UiMessages.Error($"{title}: ошибка. Библиотека не изменена.", ex);
        }
        finally
        {
            IsBusy = false;
            ProgressText = "";
            Progress = 0;
        }
    }
}

public sealed record BackupRow(BackupInfo Info)
{
    public string Created => Info.Manifest.CreatedAt.ToLocalTime().ToString("dd.MM.yyyy HH:mm");
    public string Reason => LibraryBackupService.ReasonText(Info.Manifest.Reason);
    public int Items => Info.Manifest.Items;
    public int Files => Info.Manifest.Files;
    public string Size => FormatSize(Info.Size);
    public string Who => $"{Info.Manifest.User} ({Info.Manifest.Machine})";
    public string FileName => Info.FileName;

    public static string FormatSize(long bytes) => bytes switch
    {
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} КБ",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024:0.#} МБ",
        _ => $"{bytes / 1024.0 / 1024 / 1024:0.##} ГБ",
    };
}
