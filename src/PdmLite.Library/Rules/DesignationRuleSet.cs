using System.Text.RegularExpressions;
using PdmLite.Data.Library;

namespace PdmLite.Library.Rules;

/// <summary>
/// Правила замены в обозначениях (снимок из базы), применяются по порядку при подготовке
/// обозначения. Формат обозначения по стандарту намеренно не проверяется: у каждого ГОСТ
/// свои исполнения, размеры «под ключ», классы прочности и покрытия.
/// </summary>
public sealed class DesignationRuleSet
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);

    private readonly List<(DesignationRule Rule, Regex? Regex)> _replacements = [];

    public DesignationRuleSet(IEnumerable<DesignationRule> rules)
    {
        foreach (var rule in rules.Where(r => r.Enabled).OrderBy(r => r.SortOrder).ThenBy(r => r.Id))
        {
            if (Validate(rule) is not null)
                Invalid.Add(rule);
            else
                _replacements.Add((rule, rule.IsRegex ? new Regex(rule.Pattern, RegexOptions.CultureInvariant, RegexTimeout) : null));
        }
    }

    public static DesignationRuleSet Empty { get; } = new([]);

    /// <summary>Правила с ошибкой (например, неверное регулярное выражение) — не применяются.</summary>
    public List<DesignationRule> Invalid { get; } = [];

    public bool HasReplacements => _replacements.Count > 0;

    /// <summary>Применяет замены по порядку. Возвращает результат и пояснения применённых правил.</summary>
    public (string Result, IReadOnlyList<string> Applied) ApplyReplacements(string designation)
    {
        var result = designation;
        var applied = new List<string>();
        foreach (var (rule, regex) in _replacements)
        {
            string next;
            try
            {
                next = regex is null
                    ? result.Replace(rule.Pattern, rule.Replacement ?? "", StringComparison.Ordinal)
                    : regex.Replace(result, rule.Replacement ?? "");
            }
            catch (RegexMatchTimeoutException)
            {
                continue; // зависшее выражение не должно останавливать работу
            }

            if (next != result)
                applied.Add(Describe(rule));
            result = next;
        }
        return (result, applied);
    }

    /// <summary>Ошибка в правиле или <c>null</c>, если правило корректно.</summary>
    public static string? Validate(DesignationRule rule)
    {
        if (string.IsNullOrEmpty(rule.Pattern))
            return "Не указано, что искать.";
        if (rule.IsRegex)
        {
            try
            {
                _ = new Regex(rule.Pattern, RegexOptions.CultureInvariant, RegexTimeout);
            }
            catch (ArgumentException ex)
            {
                return $"Ошибка в регулярном выражении: {ex.Message}";
            }
        }
        return null;
    }

    public static string Describe(DesignationRule rule) =>
        !string.IsNullOrWhiteSpace(rule.Comment)
            ? rule.Comment!
            : $"«{rule.Pattern}» → «{rule.Replacement}»";
}
