using PdmLite.Data.Library;
using PdmLite.Library.Catalog;
using PdmLite.Library.Designations;
using PdmLite.Library.Import;
using PdmLite.Library.Storage;

namespace PdmLite.Library;

/// <summary>Где и под каким обозначением компонент окажется в библиотеке.</summary>
public sealed record LibraryPlacement(
    string Designation,
    string? Standard,
    string? Manufacturer,
    CategoryInfo? Category,
    string? RelativePath,
    IReadOnlyList<ImportIssue> Issues)
{
    public bool HasErrors => Issues.Any(i => i.Severity == ImportSeverity.Error);
}

/// <summary>
/// Общие правила размещения компонента — одинаковые при добавлении и при изменении карточки.
/// </summary>
public static class LibraryRules
{
    /// <summary>
    /// Приводит обозначение к единому виду, определяет стандарт и путь файла,
    /// проверяет обязательные поля и длину пути. Дубликаты и занятость пути проверяет вызывающий.
    /// <paramref name="manualStandard"/> — стандарт, указанный пользователем (если распознан неверно).
    /// </summary>
    public static LibraryPlacement Place(LibraryCatalog catalog, LibraryStorage storage, string? designation,
        LibraryItemKind kind, string? manufacturer, int? categoryId, string extension, string? manualStandard = null)
    {
        var issues = new List<ImportIssue>();
        void Error(string m) => issues.Add(new ImportIssue(ImportSeverity.Error, m));
        void Warn(string m) => issues.Add(new ImportIssue(ImportSeverity.Warning, m));

        var canonical = DesignationNormalizer.ToCanonical(designation ?? "");
        var manual = NormalizeStandard(manualStandard);
        var standard = kind != LibraryItemKind.Standard ? null : manual ?? StandardParser.Find(canonical);
        var maker = string.IsNullOrWhiteSpace(manufacturer) ? null : manufacturer.Trim();
        var category = catalog.Get(categoryId);

        if (canonical.Length == 0)
        {
            Error("Не указано обозначение.");
            return new LibraryPlacement(canonical, standard, maker, category, null, issues);
        }

        if (kind == LibraryItemKind.Standard && standard is null)
            Error("В обозначении не найден стандарт (ГОСТ, ISO, DIN…). Укажите стандарт вручную или выберите тип «Покупное».");
        if (manual is not null && kind == LibraryItemKind.Standard && manual != StandardParser.Find(canonical))
            issues.Add(new ImportIssue(ImportSeverity.Info, $"Стандарт указан вручную: {manual}."));
        if (kind == LibraryItemKind.Purchased && maker is null)
            Warn($"Не указан производитель — компонент попадёт в папку «{LibraryStorage.NoManufacturerFolder}».");

        string? relativePath = null;
        if (category is null)
        {
            Error("Не выбран раздел библиотеки.");
        }
        else if (!category.IsLeaf)
        {
            Error($"«{category.Path}» — раздел; выберите группу внутри него.");
        }
        else
        {
            var subfolder = kind == LibraryItemKind.Standard ? standard : maker ?? LibraryStorage.NoManufacturerFolder;
            relativePath = LibraryStorage.BuildRelativePath(category.PathParts, subfolder, canonical, extension);
            var length = storage.GetFullPath(relativePath).Length;
            if (length > LibraryStorage.MaxPathLength)
                Error($"Слишком длинный путь в библиотеке ({length} символов, допустимо {LibraryStorage.MaxPathLength}).");
        }

        return new LibraryPlacement(canonical, standard, maker, category, relativePath, issues);
    }

    /// <summary>Стандарт, введённый вручную: пробелы схлопнуты; пустой — <c>null</c>.</summary>
    public static string? NormalizeStandard(string? standard) =>
        string.IsNullOrWhiteSpace(standard)
            ? null
            : DesignationNormalizer.ToCanonical(string.Join(' ', standard.Split(' ', StringSplitOptions.RemoveEmptyEntries)));
}
