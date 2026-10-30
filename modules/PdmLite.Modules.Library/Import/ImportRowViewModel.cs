using CommunityToolkit.Mvvm.ComponentModel;
using PdmLite.Core.UI;
using PdmLite.Data.Library;
using PdmLite.Library.Catalog;
using PdmLite.Library.Import;

namespace PdmLite.Modules.Library.Import;

/// <summary>Строка таблицы добавления. Правка поля сразу перепроверяет весь список.</summary>
public sealed class ImportRowViewModel(ImportCandidate candidate, ImportViewModel owner) : ObservableObject
{
    public static readonly IReadOnlyList<string> KindNames = ["Стандартное", "Покупное"];

    public ImportCandidate Candidate => candidate;

    public string FileName => candidate.FileName;
    public string SourcePath => candidate.SourcePath;
    public bool IsAssembly => candidate.DocumentType == LibraryDocumentType.Assembly;

    public bool Include
    {
        get => candidate.Include;
        set => Edit(() => candidate.Include = value);
    }

    public string Designation
    {
        get => candidate.Designation;
        set => Edit(() => candidate.Designation = value ?? "");
    }

    public string KindName
    {
        get => candidate.Kind == LibraryItemKind.Standard ? KindNames[0] : KindNames[1];
        set => Edit(() => candidate.Kind = value == KindNames[1] ? LibraryItemKind.Purchased : LibraryItemKind.Standard);
    }

    public bool IsPurchased => candidate.Kind == LibraryItemKind.Purchased;

    public string? Standard => candidate.Standard;

    public string? Manufacturer
    {
        get => candidate.Manufacturer;
        set => Edit(() => candidate.Manufacturer = string.IsNullOrWhiteSpace(value) ? null : value.Trim());
    }

    public CategoryInfo? Category
    {
        get => owner.Catalog?.Get(candidate.CategoryId);
        set => Edit(() => candidate.CategoryId = value?.Id);
    }

    public string? TargetPath => candidate.TargetRelativePath;

    public ImportStatus Status => candidate.Status;

    public string StatusIcon => candidate.Status switch
    {
        ImportStatus.Ready => IconKeys.StateOk,
        ImportStatus.Warning => IconKeys.Warning,
        ImportStatus.Error => IconKeys.Error,
        _ => IconKeys.Exclude,
    };

    public string StatusText => candidate.Status switch
    {
        ImportStatus.Ready => "Готов",
        ImportStatus.Warning => "Замечания",
        ImportStatus.Error => "Ошибка",
        _ => "Исключён",
    };

    /// <summary>Главная проблема — для колонки таблицы; полный список — в нижней панели.</summary>
    public string Problem =>
        candidate.Issues.Where(i => i.Severity != ImportSeverity.Info).OrderByDescending(i => i.Severity).FirstOrDefault()?.Message ?? "";

    public IReadOnlyList<IssueViewModel> Issues =>
        candidate.Issues.OrderByDescending(i => i.Severity).Select(i => new IssueViewModel(i)).ToList();

    public string PartNumberInFile => candidate.Properties?.PartNumber ?? "—";
    public string DescriptionInFile => candidate.Properties?.Description ?? "—";
    public string TitleInFile => candidate.Properties?.Title ?? "—";

    private void Edit(Action change)
    {
        change();
        owner.Revalidate();
    }

    /// <summary>Перечитать все поля после проверки.</summary>
    public void Refresh() => OnPropertyChanged(string.Empty);
}

public sealed record IssueViewModel(ImportIssue Issue)
{
    public string Icon => Issue.Severity switch
    {
        ImportSeverity.Error => IconKeys.Error,
        ImportSeverity.Warning => IconKeys.Warning,
        _ => IconKeys.StateInfo,
    };

    public string Message => Issue.Message;
}
