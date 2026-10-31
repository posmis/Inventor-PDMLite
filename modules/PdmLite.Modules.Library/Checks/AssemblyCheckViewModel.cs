using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PdmLite.Core.Documents;
using PdmLite.Core.UI;
using PdmLite.Library.Checks;
using PdmLite.Library.Designations;
using PdmLite.Library.Items;
using PdmLite.Library.Storage;
using PdmLite.UI.Shared;

namespace PdmLite.Modules.Library.Checks;

/// <summary>Проверка активной сборки: стандартные компоненты не из библиотеки и их замена.</summary>
public sealed partial class AssemblyCheckViewModel(
    IAssemblyComponents assembly,
    LibraryItemService items,
    Func<LibraryStorage> storage,
    ILogger<AssemblyCheckViewModel> logger) : ObservableObject
{
    public ObservableCollection<CheckRow> Rows { get; } = [];

    /// <summary>Вся библиотека — для ручного выбора замены.</summary>
    public IReadOnlyList<LibraryItemSummary> Library { get; private set; } = [];

    [ObservableProperty] private string _header = "";
    [ObservableProperty] private string _summary = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ReplaceCommand), nameof(FixNamesCommand))]
    private bool _isBusy;

    /// <summary>Файлы компонентов библиотеки в сборке — для проверки имён в обозревателе.</summary>
    private IReadOnlyList<string> _libraryFiles = [];

    /// <summary>Вхождения со старыми именами (например, оставшимися от Content Center).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(FixNamesCommand))]
    private int _namesToFix;

    /// <summary>Показ окна подтверждения (задаёт окно проверки).</summary>
    public Func<Catalog.ConfirmChangesWindow, bool> Ask { get; set; } = w => w.ShowDialog() == true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ReplaceCommand))]
    private int _toReplace;

    [RelayCommand]
    public async Task RunAsync()
    {
        IsBusy = true;
        try
        {
            var inspection = await assembly.InspectActiveAsync();
            var catalog = await items.LoadCatalogAsync();
            var library = await items.ListAsync(catalog);
            Library = library;
            var result = AssemblyCheck.Run(inspection.Components, library, storage());

            Rows.Clear();
            foreach (var finding in result.Findings)
                Rows.Add(new CheckRow(finding, UpdateCount));
            UpdateCount();

            var root = storage();
            _libraryFiles = inspection.Components.Select(c => c.Path).Where(root.Contains).ToList();
            NamesToFix = (await assembly.FixOccurrenceNamesAsync(_libraryFiles, apply: false)).Count;

            Header = Path.GetFileName(inspection.AssemblyPath);
            Summary = (result.Findings.Count == 0
                          ? $"Разных компонентов: {result.Total}, из библиотеки: {result.InLibrary}. Замечаний нет."
                          : $"Разных компонентов: {result.Total}, из библиотеки: {result.InLibrary}, замечаний: {result.Findings.Count}.")
                      + (NamesToFix > 0 ? $" Старых имён в обозревателе: {NamesToFix}." : "");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Не удалось проверить сборку");
            Rows.Clear();
            Header = "";
            Summary = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanReplace() => !IsBusy && ToReplace > 0;

    [RelayCommand(CanExecute = nameof(CanReplace))]
    private async Task ReplaceAsync()
    {
        var root = storage();
        var replacements = Rows.Where(r => r.Replace && r.SelectedCandidate is not null)
            .Select(r => new ComponentReplacement(r.Check.Component.Path, root.GetFullPath(r.SelectedCandidate!.Item.RelativePath)))
            .ToList();

        IsBusy = true;
        ReplacementResult? result = null;
        try
        {
            result = await assembly.ReplaceAsync(replacements);
        }
        catch (Exception ex)
        {
            UiMessages.Error("Не удалось заменить компоненты.", ex);
        }
        finally
        {
            IsBusy = false;
        }

        if (result is null)
            return;

        var text = $"Заменено: {result.Replaced}.";
        if (result.ModifiedAssemblies.Count > 0)
            text += "\n\nИзменены сборки (сохраните их в Inventor):\n" + string.Join("\n", result.ModifiedAssemblies.Select(Path.GetFileName));
        if (result.BackupFolder is not null)
            text += $"\n\nКопии сборок до замены:\n{result.BackupFolder}";
        if (result.Problems.Count > 0)
            text += "\n\nНе удалось:\n" + string.Join("\n", result.Problems);
        if (result.Problems.Count > 0)
            UiMessages.Error(text);
        else
            UiMessages.Info(text);

        await RunAsync();
    }

    private bool CanFixNames() => !IsBusy && NamesToFix > 0;

    /// <summary>Имена вхождений компонентов библиотеки — по их файлам: список «было → станет», затем запись.</summary>
    [RelayCommand(CanExecute = nameof(CanFixNames))]
    private async Task FixNamesAsync()
    {
        IsBusy = true;
        try
        {
            var plan = await assembly.FixOccurrenceNamesAsync(_libraryFiles, apply: false);
            if (plan.Count == 0)
            {
                UiMessages.Info("Имена компонентов библиотеки в обозревателе уже в порядке.");
                return;
            }

            var confirm = new Catalog.ConfirmChangesWindow("Имена в обозревателе",
                $"Будут переименованы вхождения компонентов библиотеки: {plan.Count}.",
                "Имя вхождения станет как у файла библиотеки: «обозначение:номер».",
                "Меняются только имена узлов в обозревателе — компоненты, их положение и зависимости не меняются. Сохраните сборку.",
                "Переименовать", ("Сборка", "Что", "Сейчас", "Станет"),
                plan.Select(r => new Catalog.ChangeLine(r.Assembly, "Имя в обозревателе", r.OldName, r.NewName)).ToList());
            if (!Ask(confirm))
                return;

            var done = await assembly.FixOccurrenceNamesAsync(_libraryFiles, apply: true);
            UiMessages.Info($"Переименовано вхождений: {done.Count}. Сохраните сборку.");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Не удалось обновить имена вхождений");
            UiMessages.Error("Не удалось обновить имена в обозревателе.", ex);
        }
        finally
        {
            IsBusy = false;
        }
        await RunAsync();
    }

    private void UpdateCount() => ToReplace = Rows.Count(r => r.Replace);
}

public sealed partial class CheckRow : ObservableObject
{
    private readonly Action _changed;

    public CheckRow(ComponentCheck check, Action changed)
    {
        Check = check;
        _changed = changed;
        Candidates = new ObservableCollection<CandidateOption>(check.Candidates.Select(c => new CandidateOption(c)));
        _selectedCandidate = Candidates.FirstOrDefault();
        // Отмечаем сразу точное совпадение или единственный лучший похожий; при равных вариантах выбирает пользователь.
        _replace = check.IsExact
                   || (check.Candidates.Count > 0 && check.Candidates.Count(c => c.Extra == check.Candidates[0].Extra) == 1);
    }

    /// <summary>Варианты замены для выпадающего списка (найденные и выбранные вручную).</summary>
    public ObservableCollection<CandidateOption> Candidates { get; }

    /// <summary>Замена, выбранная вручную в библиотеке: становится вариантом строки и отмечается.</summary>
    public void Choose(LibraryItemSummary item)
    {
        var option = Candidates.FirstOrDefault(c => c.Item.Id == item.Id);
        if (option is null)
        {
            option = new CandidateOption(new LibraryMatch(item, false, 0), Manual: true);
            Candidates.Insert(0, option);
        }
        SelectedCandidate = option;
        Replace = true;
        OnPropertyChanged(nameof(HasChoice));
        OnPropertyChanged(nameof(ReplacementText));
        OnPropertyChanged(nameof(CanReplace));
    }

    /// <summary>Начальный поиск для ручного выбора: наименование и размер из обозначения (без стандарта).</summary>
    public string SearchHint
    {
        get
        {
            var words = DesignationNormalizer.LeadingWords(Check.Designation);
            return words.Count > 0 ? string.Join(' ', words) : "";
        }
    }

    [ObservableProperty] private CandidateOption? _selectedCandidate;

    partial void OnSelectedCandidateChanged(CandidateOption? value)
    {
        OnPropertyChanged(nameof(ReplacementText));
        if (value is not null && !Replace)
            Replace = true; // выбрал вариант — значит, хочет заменить
    }

    public bool HasChoice => Candidates.Count > 1;

    public ComponentCheck Check { get; }

    [ObservableProperty] private bool _replace;

    partial void OnReplaceChanged(bool value) => _changed();

    public bool CanReplace => Candidates.Count > 0;
    public string Designation => Check.Designation;
    public string FilePath => Check.Component.Path;
    public string FileName => System.IO.Path.GetFileName(Check.Component.Path);
    public int Count => Check.Component.Count;
    public string ReplacementText => SelectedCandidate?.Text ?? "не найдено — выберите вручную";
    public string Message => Check.Message;

    public string FindingText => Check.Finding switch
    {
        ComponentFinding.ContentCenter => "Content Center",
        ComponentFinding.CopyOfLibraryItem => "Копия",
        ComponentFinding.MissingFile => "Файл не найден",
        _ => "Нет в библиотеке",
    };

    public string Icon => Check.Finding switch
    {
        ComponentFinding.MissingFile => IconKeys.Error,
        ComponentFinding.ContentCenter => IconKeys.Warning,
        _ => IconKeys.StateInfo,
    };
}

/// <summary>Вариант замены в списке: обозначение и пометка, точное ли совпадение.</summary>
public sealed record CandidateOption(LibraryMatch Match, bool Manual = false)
{
    public LibraryItemSummary Item => Match.Item;
    public string Text => Manual ? $"{Match.Item.Designation}  (выбран вручную)"
        : Match.Exact ? Match.Item.Designation
        : $"{Match.Item.Designation}  (похожий)";
    public override string ToString() => Text;
}
