using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdmLite.Core.Documents;
using PdmLite.Core.Projects;
using PdmLite.Index.Operations;

namespace PdmLite.Modules.Rename;

/// <summary>Потерянная ссылка и выбранная замена.</summary>
public sealed partial class BrokenLinkRow : ObservableObject
{
    public BrokenLinkRow(string root, BrokenLink link)
    {
        Root = root;
        Link = link;
        Candidates = new ObservableCollection<string>(link.Candidates);
        _chosen = link.Candidates.Count == 1 ? link.Candidates[0] : null;
    }

    public string Root { get; }
    public BrokenLink Link { get; }
    public string Name => Link.Name;
    public string OldFolder => Path.GetDirectoryName(Link.Missing) ?? "";
    public int UsedCount => Link.UsedIn.Count;
    public string UsedIn => string.Join(", ", Link.UsedIn.Select(Path.GetFileName));
    public ObservableCollection<string> Candidates { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(State))]
    private string? _chosen;

    public string State => Chosen is not null ? "будет восстановлена"
        : Candidates.Count > 1 ? $"найдено {Candidates.Count} файла — выберите"
        : "не найден в проекте — укажите файл";

    /// <summary>Файл — относительно папки проекта (для списка).</summary>
    public string Display(string path) => path.StartsWith(Root, StringComparison.OrdinalIgnoreCase) ? Path.GetRelativePath(Root, path) : path;
}

/// <summary>
/// «Обновить связи проекта»: после переноса или переименования файлов в проводнике Windows — найти потерянные
/// ссылки, подобрать файлы проекта с тем же именем и заменить ссылки во всех документах (с проверкой и откатом).
/// </summary>
public sealed partial class RepairViewModel(ProjectFileOperations operations, IOpenDocuments openDocuments, IProjectSession session, string root)
    : ObservableObject
{
    public string Root { get; } = Path.TrimEndingDirectorySeparator(root);
    public ObservableCollection<BrokenLinkRow> Links { get; } = [];

    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _problems = "";
    [ObservableProperty] private string _warnings = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(FindCommand), nameof(RepairCommand))]
    private bool _isBusy;

    public string? Summary { get; private set; }
    public event EventHandler? Completed;
    public Func<string, bool> Confirm { get; set; } = _ => true;

    private bool CanFind() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanFind))]
    private async Task FindAsync()
    {
        IsBusy = true;
        Problems = "";
        Warnings = "";
        Links.Clear();
        try
        {
            var links = await operations.FindBrokenLinksAsync(Root, new Progress<string>(s => Status = s));
            foreach (var link in links)
            {
                var row = new BrokenLinkRow(Root, link);
                row.PropertyChanged += (_, _) => RepairCommand.NotifyCanExecuteChanged();
                Links.Add(row);
            }
            var auto = Links.Count(l => l.Chosen is not null);
            Status = Links.Count == 0
                ? "Потерянных ссылок в проекте нет."
                : $"Потерянных файлов: {Links.Count}, найдено однозначно: {auto}. Проверьте список и нажмите «Обновить связи».";
        }
        catch (Exception ex)
        {
            Problems = ex.Message;
            Status = "";
        }
        finally
        {
            IsBusy = false;
            RepairCommand.NotifyCanExecuteChanged();
        }
    }

    private bool CanRepair() => !IsBusy && Links.Any(l => l.Chosen is not null);

    [RelayCommand(CanExecute = nameof(CanRepair))]
    private async Task RepairAsync()
    {
        IsBusy = true;
        Problems = "";
        try
        {
            var choices = Links.Where(l => l.Chosen is not null).ToDictionary(l => l.Link.Missing, l => l.Chosen!, StringComparer.OrdinalIgnoreCase);
            var plan = operations.PlanRepair(Root, Links.Select(l => l.Link).ToList(), choices);
            if (!plan.CanExecute)
            {
                Problems = string.Join(Environment.NewLine, plan.Problems);
                return;
            }
            if (!Confirm($"Ссылки будут заменены в документах: {plan.References.Count}." + Environment.NewLine
                         + "Копии документов сохраняются, при ошибке всё вернётся. Продолжить?"))
                return;
            var result = await OperationRunner.RunAsync(operations, openDocuments, plan, Confirm, text => Problems = text,
                text => Warnings = text, new Progress<string>(s => Status = s));
            if (result is null)
                return;
            Summary = $"Связи обновлены: восстановлено ссылок на {choices.Count} файл(ов) в {result.ReferencesUpdated} документах."
                      + (result.Warnings.Count == 0 ? "" : Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine, result.Warnings))
                      + Environment.NewLine + Environment.NewLine + "Журнал и копии документов: " + result.BackupFolder;
            try
            {
                await session.RefreshAsync();
            }
            catch (Exception)
            {
                // Дерево обновится позже.
            }
            Completed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Problems = ex.Message;
            Status = "";
        }
        finally
        {
            IsBusy = false;
        }
    }
}
