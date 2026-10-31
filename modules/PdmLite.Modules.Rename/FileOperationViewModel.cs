using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdmLite.Core.Documents;
using PdmLite.Core.Events;
using PdmLite.Core.Naming;
using PdmLite.Core.Projects;
using PdmLite.Index;
using PdmLite.Index.Operations;

namespace PdmLite.Modules.Rename;

public enum FileOperationMode
{
    Rename,
    Move,
}

/// <summary>Строка «было → станет».</summary>
public sealed partial class FileRow : ObservableObject
{
    public FileRow(string root, string from, string relativeTarget, string note)
    {
        Root = root;
        From = from;
        RelativeTarget = relativeTarget;
        Note = note;
        _to = from;
    }

    public string Root { get; }
    public string From { get; }

    /// <summary>Для перемещения: путь относительно папки назначения.</summary>
    public string RelativeTarget { get; }

    public string Note { get; }
    public string FromName => Path.GetFileName(From);
    public string FromFolder => Relative(Path.GetDirectoryName(From)!);

    [ObservableProperty] private bool _include = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToName), nameof(ToFolder), nameof(Changes))]
    private string _to;

    public string ToName => Path.GetFileName(To);
    public string ToFolder => Relative(Path.GetDirectoryName(To)!);
    public bool Changes => !To.Equals(From, StringComparison.Ordinal);

    private string Relative(string folder)
    {
        var relative = Path.GetRelativePath(Root, folder);
        return relative == "." ? "(папка проекта)" : relative;
    }
}

/// <summary>Документ, в котором заменятся ссылки.</summary>
public sealed record ReferenceRow(string Name, string Folder, int Count, string Details);

/// <summary>
/// Окно переименования или перемещения: поля ввода, список файлов (вместе со связанными чертежами),
/// проверка (где заменятся ссылки, что мешает) и выполнение. Документы, открытые в Inventor, внутри Inventor
/// сохраняются, закрываются и после операции открываются снова; из Explorer — нужно закрыть.
/// </summary>
public sealed partial class FileOperationViewModel : ObservableObject
{
    private readonly ProjectFileOperations _operations;
    private readonly IOpenDocuments _openDocuments;
    private readonly IProjectSession _session;
    private readonly IEventBus _events;
    private readonly IDocumentPropertyStore _store;
    private readonly IReadOnlyList<string> _sources;
    private FileOperationPlan? _plan;
    private bool _initializing = true;

    public FileOperationViewModel(ProjectFileOperations operations, IOpenDocuments openDocuments, IProjectSession session, IEventBus events,
        IDocumentPropertyStore store, string root, FileOperationMode mode, IReadOnlyList<string> sources, string? targetFolder)
    {
        _operations = operations;
        _openDocuments = openDocuments;
        _session = session;
        _events = events;
        _store = store;
        Root = Path.TrimEndingDirectorySeparator(root);
        Mode = mode;
        _sources = sources;
        _targetFolder = targetFolder ?? "";
    }

    public string Root { get; }
    public FileOperationMode Mode { get; }
    public bool IsRename => Mode == FileOperationMode.Rename;
    public bool IsMove => Mode == FileOperationMode.Move;
    public string Title => IsRename ? "Переименовать" : "Переместить";
    public string ActionText => IsRename ? "Переименовать" : "Переместить";

    public string Header => IsRename
        ? $"Переименование: {Path.GetFileName(_sources[0])}"
        : _sources.Count == 1 ? $"Перемещение: {Path.GetFileName(_sources[0])}" : $"Перемещение: {_sources.Count} объектов";

    public ObservableCollection<FileRow> Files { get; } = [];
    public ObservableCollection<ReferenceRow> References { get; } = [];

    /// <summary>Файл, который выбрали для переименования (после операции — по новому пути).</summary>
    public string Main => _sources[0];

    [ObservableProperty] private string _designation = "";
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private bool _writeProperties = true;
    [ObservableProperty] private string _targetFolder;
    [ObservableProperty] private bool _includeRelated = true;

    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _problems = "";
    [ObservableProperty] private string _warnings = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckCommand), nameof(ExecuteCommand))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExecuteCommand))]
    private bool _isChecked;

    /// <summary>Итог выполнения — для сообщения после закрытия окна.</summary>
    public string? Summary { get; private set; }

    public event EventHandler? Completed;

    public string ReferencesHeader => References.Count == 0 ? "Ссылки" : $"Ссылки будут обновлены в документах ({References.Count})";

    /// <summary>Собирает список файлов (и связанные), заполняет поля.</summary>
    public async Task InitializeAsync()
    {
        IsBusy = true;
        try
        {
            if (IsRename)
                await InitializeRenameAsync();
            else
                InitializeMove();
            foreach (var row in Files)
                row.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(FileRow.Include))
                        Invalidate();
                };
            _initializing = false;
            Recompute();
            Status = IsRename
                ? "Введите новое обозначение и наименование и нажмите «Проверить»."
                : TargetFolder.Length == 0 ? "Выберите папку назначения." : "Нажмите «Проверить».";
        }
        catch (Exception ex)
        {
            Problems = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task InitializeRenameAsync()
    {
        var main = _sources[0];
        var stem = Path.GetFileNameWithoutExtension(main);
        if (FileNameBuilder.TryParse(stem) is { } parsed)
        {
            Designation = parsed.Designation;
            Name = parsed.Name;
        }
        else
        {
            Name = stem;
            try
            {
                if (_store.IsAvailable && Core.Domain.DocumentKinds.IsKnown(main))
                {
                    var props = await _store.ReadWithoutGeometryAsync(main);
                    Designation = props.PartNumber?.Trim() ?? "";
                    if (!string.IsNullOrWhiteSpace(props.Title))
                        Name = props.Title.Trim();
                }
            }
            catch (Exception)
            {
                // Свойства не прочитались — поля заполнятся по имени файла.
            }
        }

        Files.Add(new FileRow(Root, main, "", "выбранный файл"));
        Status = "Поиск связанных файлов…";
        var progress = new Progress<string>(s => Status = s);
        foreach (var related in await _operations.RelatedFilesAsync(Root, main, progress))
            Files.Add(new FileRow(Root, related, "", Path.GetDirectoryName(related)!.Equals(Path.GetDirectoryName(main), StringComparison.OrdinalIgnoreCase)
                ? "связанный (то же обозначение)"
                : "чертёж из другой папки"));
    }

    private void InitializeMove()
    {
        var rows = new List<FileRow>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in _sources)
        {
            if (Directory.Exists(source))
            {
                foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
                {
                    var relative = Path.GetRelativePath(source, file);
                    if (relative.Split(Path.DirectorySeparatorChar).Any(FileOperationRequests.IsOldVersions) || !seen.Add(file))
                        continue;
                    rows.Add(new FileRow(Root, file, Path.Combine(Path.GetFileName(source), relative), $"из папки «{Path.GetFileName(source)}»"));
                }
                continue;
            }
            if (seen.Add(source))
                rows.Add(new FileRow(Root, source, Path.GetFileName(source), "выбранный файл"));
        }
        // Связанные файлы (чертежи, PDF с тем же обозначением) — из той же папки.
        foreach (var source in _sources.Where(File.Exists).ToList())
            foreach (var related in FileOperationRequests.RelatedInFolder(source, Directory.EnumerateFiles(Path.GetDirectoryName(source)!)))
                if (seen.Add(related))
                    rows.Add(new FileRow(Root, related, Path.GetFileName(related), "связанный (то же обозначение)"));
        foreach (var row in rows)
            Files.Add(row);
    }

    partial void OnDesignationChanged(string value) => Recompute();
    partial void OnNameChanged(string value) => Recompute();
    partial void OnWritePropertiesChanged(bool value) => Invalidate();
    partial void OnTargetFolderChanged(string value) => Recompute();

    partial void OnIncludeRelatedChanged(bool value)
    {
        foreach (var row in Files.Where(r => r.Note.StartsWith("связанный") || r.Note.StartsWith("чертёж")))
            row.Include = value;
        Invalidate();
    }

    private void Recompute()
    {
        if (_initializing)
            return;
        foreach (var row in Files)
        {
            try
            {
                row.To = IsRename
                    ? FileOperationRequests.Rename([row.From], Designation, Name).FirstOrDefault()?.To ?? row.From
                    : TargetFolder.Length == 0 ? row.From : Path.Combine(TargetFolder, row.RelativeTarget);
            }
            catch (ArgumentException)
            {
                row.To = row.From; // пустые обозначение и наименование — имя не построить
            }
        }
        Invalidate();
    }

    private void Invalidate()
    {
        IsChecked = false;
        _plan = null;
        References.Clear();
        OnPropertyChanged(nameof(ReferencesHeader));
        Problems = "";
        Warnings = "";
    }

    private bool CanCheck() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanCheck))]
    private async Task CheckAsync()
    {
        Invalidate();
        var problems = new List<string>();
        if (IsRename && string.IsNullOrWhiteSpace(Designation) && string.IsNullOrWhiteSpace(Name))
            problems.Add("Введите обозначение или наименование.");
        if (IsMove && TargetFolder.Length == 0)
            problems.Add("Выберите папку назначения.");
        if (IsMove && TargetFolder.Length > 0)
            foreach (var folder in _sources.Where(Directory.Exists))
                if ((Path.TrimEndingDirectorySeparator(TargetFolder) + Path.DirectorySeparatorChar).StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    problems.Add($"Нельзя переместить папку «{Path.GetFileName(folder)}» в саму себя.");
        var included = Files.Where(r => r.Include).ToList();
        var moves = included.Where(r => r.Changes).Select(r => new FileMove(r.From, r.To)).ToList();
        var properties = IsRename && WriteProperties
            ? FileOperationRequests.RenameProperties(included.Select(r => r.From), Designation, Name)
            : [];
        if (problems.Count == 0 && moves.Count == 0 && properties.Count == 0)
            problems.Add(IsRename ? "Имена не меняются." : "Файлы уже в этой папке.");
        if (problems.Count > 0)
        {
            Problems = string.Join(Environment.NewLine, problems);
            return;
        }

        IsBusy = true;
        try
        {
            var plan = await _operations.PlanAsync(Root, moves, properties, new Progress<string>(s => Status = s));
            foreach (var update in plan.References)
                References.Add(new ReferenceRow(Path.GetFileName(update.Target), RelativeFolder(update.Document), update.Map.Count,
                    string.Join("; ", update.Map.Select(m => $"{Path.GetFileName(m.Key)} → {RelativeOrName(m.Key, m.Value)}"))));
            OnPropertyChanged(nameof(ReferencesHeader));
            Problems = string.Join(Environment.NewLine, plan.Problems);
            Warnings = string.Join(Environment.NewLine, plan.Warnings);
            _plan = plan;
            IsChecked = plan.CanExecute;
            Status = plan.CanExecute
                ? $"Файлов: {plan.Moves.Count}, документов со ссылками: {plan.References.Count}, свойств: {plan.Properties.Count}. Нажмите «{ActionText}»."
                : "Исправьте ошибки и проверьте снова.";
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

    private bool CanExecute() => IsChecked && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanExecute))]
    private async Task ExecuteAsync()
    {
        if (_plan is not { CanExecute: true } plan)
            return;
        IsBusy = true;
        IReadOnlyList<string> closed = [];
        var done = false;
        try
        {
            // Документы, загруженные в Inventor, — закрыть (внутри Inventor) или попросить закрыть.
            var involved = plan.InvolvedFiles.Select(ReferenceIndexService.Key).ToHashSet();
            var open = (await _openDocuments.ListAsync()).Where(d => involved.Contains(ReferenceIndexService.Key(d.Path))).ToList();
            if (open.Count > 0)
            {
                var names = string.Join(Environment.NewLine, open.Select(d => "  " + Path.GetFileName(d.Path)).Distinct().Take(15));
                if (!_openDocuments.CanClose)
                {
                    Problems = "Эти документы загружены в Inventor — закройте их (или сборки, в которые они входят) и нажмите снова:"
                               + Environment.NewLine + names;
                    return;
                }
                if (!Confirm("Документы открыты в Inventor:" + Environment.NewLine + names + Environment.NewLine + Environment.NewLine
                             + "Они будут сохранены и закрыты, а после операции открыты снова. Продолжить?"))
                    return;
                Status = "Закрытие документов в Inventor…";
                closed = await _openDocuments.CloseAsync(plan.InvolvedFiles);
            }

            var result = await _operations.ExecuteAsync(plan, new Progress<string>(s => Status = s));
            done = true;
            Summary = $"{ActionText}: файлов {result.Moved}, ссылки обновлены в {result.ReferencesUpdated} док., свойства записаны в {result.PropertiesWritten}."
                      + (result.Warnings.Count == 0 ? "" : Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine, result.Warnings))
                      + Environment.NewLine + Environment.NewLine + "Журнал и копии изменённых документов: " + result.BackupFolder;

            _events.Publish(new ProjectFilesMoved(plan.Moves.ToDictionary(m => m.From, m => m.To, StringComparer.OrdinalIgnoreCase)));
            try
            {
                await _session.RefreshAsync();
                _events.Publish(new RevealDocumentRequested(plan.After(Main)));
            }
            catch (Exception)
            {
                // Дерево обновится при следующем открытии проекта.
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
            if (closed.Count > 0)
            {
                try
                {
                    await _openDocuments.OpenAsync(closed.Select(p => done ? plan.After(p) : p).Where(File.Exists).ToList());
                }
                catch (Exception ex)
                {
                    Warnings = $"Документы не открылись снова: {ex.Message}";
                }
            }
            IsBusy = false;
        }
    }

    /// <summary>Подтверждение (в окне задаёт представление).</summary>
    public Func<string, bool> Confirm { get; set; } = _ => true;

    private string RelativeFolder(string path)
    {
        var relative = Path.GetRelativePath(Root, Path.GetDirectoryName(path)!);
        return relative == "." ? "(папка проекта)" : relative;
    }

    private string RelativeOrName(string from, string to) =>
        Path.GetDirectoryName(from)!.Equals(Path.GetDirectoryName(to), StringComparison.OrdinalIgnoreCase)
            ? Path.GetFileName(to)
            : Path.GetRelativePath(Root, to);
}
