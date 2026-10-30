using PdmLite.Core.Documents;
using PdmLite.Library.Designations;
using PdmLite.Library.Items;
using PdmLite.Library.Storage;

namespace PdmLite.Library.Checks;

public enum ComponentFinding
{
    /// <summary>Компонент из Content Center Inventor.</summary>
    ContentCenter,

    /// <summary>Копия библиотечного компонента вне библиотеки (например, в папке проекта).</summary>
    CopyOfLibraryItem,

    /// <summary>Стандартное изделие (в обозначении есть стандарт), которого нет в библиотеке.</summary>
    StandardNotInLibrary,
}

/// <param name="Replacement">Библиотечный компонент с тем же обозначением, если есть.</param>
public sealed record ComponentCheck(AssemblyComponentInfo Component, string Designation, ComponentFinding Finding, LibraryItemSummary? Replacement)
{
    public bool CanReplace => Replacement is not null;

    public string Message => Finding switch
    {
        ComponentFinding.ContentCenter when CanReplace => "Из Content Center — в библиотеке есть такой же компонент.",
        ComponentFinding.ContentCenter => "Из Content Center — в библиотеке такого компонента нет. Добавьте его в библиотеку.",
        ComponentFinding.CopyOfLibraryItem => "Копия библиотечного компонента вне библиотеки.",
        _ => "Стандартное изделие не из библиотеки. Добавьте его в библиотеку.",
    };
}

public sealed record AssemblyCheckResult(int InLibrary, int Total, IReadOnlyList<ComponentCheck> Findings);

/// <summary>
/// Проверка сборки: какие стандартные компоненты взяты не из библиотеки и чем их заменить.
/// Сопоставление — по обозначению (без учёта похожих букв, пробелов и регистра).
/// </summary>
public static class AssemblyCheck
{
    public static AssemblyCheckResult Run(IEnumerable<AssemblyComponentInfo> components, IEnumerable<LibraryItemSummary> library, LibraryStorage storage)
    {
        var byKey = new Dictionary<string, LibraryItemSummary>();
        foreach (var item in library)
            byKey.TryAdd(DesignationNormalizer.ToKey(item.Designation), item);

        var findings = new List<ComponentCheck>();
        var inLibrary = 0;
        var total = 0;
        foreach (var component in components)
        {
            total++;
            if (storage.Contains(component.Path))
            {
                inLibrary++;
                continue;
            }

            var designation = DesignationNormalizer.ToCanonical(
                !string.IsNullOrWhiteSpace(component.PartNumber) ? component.PartNumber! : FileNameWithoutExtension(component.Path));
            var match = byKey.GetValueOrDefault(DesignationNormalizer.ToKey(designation));

            if (component.IsContentCenter)
                findings.Add(new ComponentCheck(component, designation, ComponentFinding.ContentCenter, match));
            else if (match is not null)
                findings.Add(new ComponentCheck(component, designation, ComponentFinding.CopyOfLibraryItem, match));
            else if (StandardParser.Find(designation) is not null)
                findings.Add(new ComponentCheck(component, designation, ComponentFinding.StandardNotInLibrary, null));
            // Остальное — собственные детали проекта: не проверяем.
        }

        return new AssemblyCheckResult(inLibrary, total,
            findings.OrderBy(f => f.Finding).ThenBy(f => f.Designation, StringComparer.CurrentCulture).ToList());
    }

    /// <summary>Имя файла без расширения для путей Windows на любой платформе (пути приходят из Inventor).</summary>
    private static string FileNameWithoutExtension(string path) =>
        Path.GetFileNameWithoutExtension(path[(path.LastIndexOfAny(['\\', '/']) + 1)..]);
}
