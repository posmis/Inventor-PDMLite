using PdmLite.Core.Documents;
using PdmLite.Core.Naming;
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

/// <param name="Candidates">Варианты замены из библиотеки, лучшие первыми: точное совпадение обозначения или похожие.</param>
public sealed record ComponentCheck(AssemblyComponentInfo Component, string Designation, ComponentFinding Finding, IReadOnlyList<LibraryMatch> Candidates)
{
    /// <summary>Предлагаемая замена — лучший вариант.</summary>
    public LibraryItemSummary? Replacement => Candidates.Count > 0 ? Candidates[0].Item : null;

    public bool CanReplace => Candidates.Count > 0;

    /// <summary>Обозначение совпадает точно — замена надёжная.</summary>
    public bool IsExact => Candidates is [{ Exact: true }, ..];

    public string Message => Finding switch
    {
        ComponentFinding.ContentCenter when IsExact => "Из Content Center — в библиотеке есть такой же компонент.",
        ComponentFinding.ContentCenter when CanReplace => "Из Content Center — в библиотеке есть похожие: проверьте выбранный вариант.",
        ComponentFinding.ContentCenter => "Из Content Center — в библиотеке такого компонента нет. Добавьте его в библиотеку.",
        ComponentFinding.CopyOfLibraryItem => "Копия библиотечного компонента вне библиотеки.",
        _ when CanReplace => "Стандартное изделие не из библиотеки — в библиотеке есть похожие: проверьте выбранный вариант.",
        _ => "Стандартное изделие не из библиотеки. Добавьте его в библиотеку.",
    };
}

public sealed record AssemblyCheckResult(int InLibrary, int Total, IReadOnlyList<ComponentCheck> Findings);

/// <summary>
/// Проверка сборки: какие стандартные компоненты взяты не из библиотеки и чем их заменить.
/// Сопоставление — по обозначению: точное (без учёта похожих букв, пробелов и регистра) или похожее (<see cref="LibraryMatcher"/>).
/// </summary>
public static class AssemblyCheck
{
    public static AssemblyCheckResult Run(IEnumerable<AssemblyComponentInfo> components, IEnumerable<LibraryItemSummary> library, LibraryStorage storage)
    {
        var matcher = new LibraryMatcher(library);
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
            var candidates = matcher.Find(designation);
            var exact = candidates is [{ Exact: true }, ..];

            if (component.IsContentCenter)
                findings.Add(new ComponentCheck(component, designation, ComponentFinding.ContentCenter, candidates));
            else if (exact)
                findings.Add(new ComponentCheck(component, designation, ComponentFinding.CopyOfLibraryItem, candidates));
            else if (StandardParser.Find(designation) is not null)
                findings.Add(new ComponentCheck(component, designation, ComponentFinding.StandardNotInLibrary, candidates));
            // Остальное — собственные детали проекта: не проверяем.
        }

        return new AssemblyCheckResult(inLibrary, total,
            findings.OrderBy(f => f.Finding).ThenBy(f => f.Designation, NaturalComparer.Instance).ToList());
    }

    /// <summary>Имя файла без расширения для путей Windows на любой платформе (пути приходят из Inventor).</summary>
    private static string FileNameWithoutExtension(string path) =>
        Path.GetFileNameWithoutExtension(path[(path.LastIndexOfAny(['\\', '/']) + 1)..]);
}
