using CommunityToolkit.Mvvm.ComponentModel;
using PdmLite.Core.UI;
using PdmLite.Data.Library;
using PdmLite.Library;
using PdmLite.Library.Catalog;
using PdmLite.Library.Designations;
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
        set => Edit(() =>
        {
            var designation = value ?? "";
            if (designation == candidate.Designation)
                return;
            candidate.Designation = designation;
            candidate.Source = DesignationSource.Manual; // правила к ручной правке больше не применяются
            candidate.AppliedRules = [];
        });
    }

    /// <summary>Откуда взято обозначение — для колонки «Источник».</summary>
    public string SourceName => SourceNames[candidate.Source];

    public static readonly IReadOnlyDictionary<DesignationSource, string> SourceNames = new Dictionary<DesignationSource, string>
    {
        [DesignationSource.PartNumber] = "Обозначение",
        [DesignationSource.Description] = "Описание",
        [DesignationSource.Title] = "Наименование",
        [DesignationSource.FileName] = "Имя файла",
        [DesignationSource.Manual] = "Вручную",
    };

    public string KindName
    {
        get => candidate.Kind == LibraryItemKind.Standard ? KindNames[0] : KindNames[1];
        set => Edit(() => candidate.Kind = value == KindNames[1] ? LibraryItemKind.Purchased : LibraryItemKind.Standard);
    }

    public bool IsPurchased => candidate.Kind == LibraryItemKind.Purchased;

    /// <summary>
    /// Стандарт: распознаётся из обозначения; правка в таблице задаёт его вручную,
    /// пустое значение возвращает распознавание.
    /// </summary>
    public string? Standard
    {
        get => candidate.Standard;
        set => Edit(() =>
        {
            var manual = LibraryRules.NormalizeStandard(value);
            candidate.ManualStandard = manual == StandardParser.Find(DesignationNormalizer.ToCanonical(candidate.Designation)) ? null : manual;
        });
    }

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

    /// <summary>Что уже есть в библиотеке (тот же компонент, файл или геометрия); пусто — нет.</summary>
    public string LibraryMatch => candidate.LibraryMatch ?? "";

    public ImportStatus Status => candidate.Status;

    public string StatusIcon => candidate.Status switch
    {
        ImportStatus.Ready => IconKeys.StateOk,
        ImportStatus.Warning => IconKeys.Warning,
        ImportStatus.Error => IconKeys.Error,
        _ => IconKeys.Error,
    };

    public string StatusText => candidate.Status switch
    {
        ImportStatus.Ready => "Готов",
        ImportStatus.Warning => "Замечания",
        _ => "Ошибка",
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
