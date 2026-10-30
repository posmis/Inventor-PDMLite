using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PdmLite.Data;
using PdmLite.Data.Library;

namespace PdmLite.Library.Rules;

/// <summary>Правила замены в обозначениях (в базе): чтение и сохранение списком.</summary>
public sealed class DesignationRuleService(IPdmDatabase database)
{
    public async Task<IReadOnlyList<DesignationRule>> ListAsync(CancellationToken ct = default)
    {
        await database.MigrateAsync(ct).ConfigureAwait(false);
        await using var db = database.CreateContext();
        return await db.DesignationRules.AsNoTracking().OrderBy(r => r.SortOrder).ThenBy(r => r.Id)
            .ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task<DesignationRuleSet> LoadSetAsync(CancellationToken ct = default) => new(await ListAsync(ct).ConfigureAwait(false));

    /// <summary>
    /// Сохраняет весь список (как в окне правил): порядок — по позиции в списке.
    /// Правила с ошибкой не сохраняются — бросается исключение с описанием.
    /// </summary>
    public async Task SaveAsync(IReadOnlyList<DesignationRule> rules, CancellationToken ct = default)
    {
        var errors = rules.Select((r, i) => (Rule: r, Index: i, Error: DesignationRuleSet.Validate(r)))
            .Where(x => x.Error is not null).ToList();
        if (errors.Count > 0)
            throw new InvalidOperationException(string.Join("\n", errors.Select(e => $"Правило {e.Index + 1} («{e.Rule.Pattern}»): {e.Error}")));

        await database.MigrateAsync(ct).ConfigureAwait(false);
        await using var db = database.CreateContext();
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        db.DesignationRules.RemoveRange(await db.DesignationRules.ToListAsync(ct).ConfigureAwait(false));
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        for (var i = 0; i < rules.Count; i++)
        {
            var r = rules[i];
            db.DesignationRules.Add(new DesignationRule
            {
                SortOrder = i,
                Enabled = r.Enabled,
                Pattern = r.Pattern,
                Replacement = r.Replacement ?? "",
                IsRegex = r.IsRegex,
                Comment = string.IsNullOrWhiteSpace(r.Comment) ? null : r.Comment.Trim(),
            });
        }

        db.LibraryLog.Add(new LibraryLogEntry
        {
            At = DateTime.UtcNow,
            User = Environment.UserName,
            Action = "rules",
            Details = JsonSerializer.Serialize(new { count = rules.Count }),
        });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }
}
