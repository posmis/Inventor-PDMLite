using Microsoft.EntityFrameworkCore;
using PdmLite.Core.Naming;
using PdmLite.Data;
using PdmLite.Data.Library;
using PdmLite.Library.Designations;

namespace PdmLite.Library.Catalog;

/// <summary>Раздел классификатора с полным путём («Крепёж/Болты»).</summary>
public sealed record CategoryInfo(int Id, int? ParentId, string Name, string Path, bool IsLeaf)
{
    public IReadOnlyList<string> PathParts => Path.Split('/');
    public override string ToString() => Path;
}

/// <summary>Классификатор и правила выбора раздела (снимок из базы).</summary>
public sealed class LibraryCatalog
{
    private readonly Dictionary<int, CategoryInfo> _byId;
    private readonly Dictionary<string, int> _standardRules;
    private readonly Dictionary<string, int> _wordRules;

    private LibraryCatalog(IEnumerable<CategoryInfo> categories, IEnumerable<LibraryCategoryRule> rules)
    {
        _byId = categories.ToDictionary(c => c.Id);
        Categories = _byId.Values.OrderBy(c => c.Path, NaturalComparer.Instance).ToList();

        // Запомненные правила важнее встроенных.
        // Повтор образца (например, из резервной копии) не должен ронять загрузку: последнее — запомненное — побеждает.
        _standardRules = [];
        _wordRules = [];
        foreach (var rule in rules.OrderBy(r => r.IsLearned))
            (rule.Kind == LibraryRuleKind.Standard ? _standardRules : _wordRules)[rule.Pattern] = rule.CategoryId;
    }

    public IReadOnlyList<CategoryInfo> Categories { get; }

    /// <summary>Разделы, в которые можно класть компоненты (группы без подгрупп).</summary>
    public IEnumerable<CategoryInfo> Leaves => Categories.Where(c => c.IsLeaf);

    public CategoryInfo? Get(int? id) => id is { } value && _byId.TryGetValue(value, out var c) ? c : null;

    public CategoryInfo? FindByPath(string path) => Categories.FirstOrDefault(c => c.Path == path);

    /// <summary>Раздел для обозначения: сначала по стандарту, затем по начальным словам.</summary>
    public CategoryInfo? Suggest(string designation, string? standard)
    {
        if (standard is not null && _standardRules.TryGetValue(DesignationNormalizer.ToKey(standard), out var byStandard))
            return Get(byStandard);

        var words = DesignationNormalizer.LeadingWords(designation);
        for (var n = words.Count; n > 0; n--)
            if (_wordRules.TryGetValue(string.Join(' ', words.Take(n)), out var byWords))
                return Get(byWords);

        return null;
    }

    public static async Task<LibraryCatalog> LoadAsync(PdmDbContext db, CancellationToken ct = default)
    {
        var categories = await db.LibraryCategories.AsNoTracking().ToListAsync(ct);
        var rules = await db.LibraryCategoryRules.AsNoTracking().ToListAsync(ct);
        return Create(categories, rules);
    }

    /// <summary>Классификатор из готовых записей (база не нужна — для проверок и тестов).</summary>
    internal static LibraryCatalog Create(IReadOnlyCollection<LibraryCategory> categories, IEnumerable<LibraryCategoryRule> rules)
    {
        var byId = categories.ToDictionary(c => c.Id);
        // Путь — вверх по родителям; зацикленные родители (испорченные данные) не должны переполнять стек.
        string PathOf(LibraryCategory c)
        {
            var parts = new List<string> { c.Name };
            var seen = new HashSet<int> { c.Id };
            while (c.ParentId is { } p && byId.TryGetValue(p, out var parent) && seen.Add(parent.Id))
            {
                parts.Add(parent.Name);
                c = parent;
            }
            parts.Reverse();
            return string.Join('/', parts);
        }
        var parents = categories.Where(c => c.ParentId is not null).Select(c => c.ParentId!.Value).ToHashSet();

        return new LibraryCatalog(categories.Select(c => new CategoryInfo(c.Id, c.ParentId, c.Name, PathOf(c), !parents.Contains(c.Id))), rules);
    }

    /// <summary>Заполняет классификатор и правила по умолчанию, если база пустая.</summary>
    public static async Task SeedAsync(PdmDbContext db, CancellationToken ct = default)
    {
        if (await db.LibraryCategories.AnyAsync(ct))
            return;

        var groups = new Dictionary<string, LibraryCategory>();
        var order = 0;
        foreach (var (section, names) in DefaultCatalog.Sections)
        {
            var parent = new LibraryCategory { Name = section, SortOrder = order++ };
            db.LibraryCategories.Add(parent);
            var groupOrder = 0;
            foreach (var name in names)
            {
                var group = new LibraryCategory { Name = name, Parent = parent, SortOrder = groupOrder++ };
                db.LibraryCategories.Add(group);
                groups[$"{section}/{name}"] = group;
            }
        }

        foreach (var (word, path) in DefaultCatalog.FirstWordRules)
            db.LibraryCategoryRules.Add(new LibraryCategoryRule { Kind = LibraryRuleKind.FirstWord, Pattern = word, Category = groups[path] });

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Запоминает выбор пользователя: если раздел исправили вручную, следующие компоненты
    /// того же стандарта (или с тем же началом обозначения) попадут туда же.
    /// </summary>
    public static async Task LearnAsync(PdmDbContext db, string designation, string? standard, int categoryId, CancellationToken ct = default)
    {
        var (kind, pattern) = standard is not null
            ? (LibraryRuleKind.Standard, DesignationNormalizer.ToKey(standard))
            : (LibraryRuleKind.FirstWord, string.Join(' ', DesignationNormalizer.LeadingWords(designation)));
        if (pattern.Length == 0)
            return;

        var rule = await db.LibraryCategoryRules.FirstOrDefaultAsync(r => r.Kind == kind && r.Pattern == pattern, ct);
        if (rule is null)
            db.LibraryCategoryRules.Add(new LibraryCategoryRule { Kind = kind, Pattern = pattern, CategoryId = categoryId, IsLearned = true });
        else if (rule.CategoryId != categoryId)
        {
            rule.CategoryId = categoryId;
            rule.IsLearned = true;
        }
    }
}
