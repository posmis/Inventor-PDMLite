using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PdmLite.Core.Domain;
using PdmLite.Core.IO;

namespace PdmLite.Index;

/// <summary>Версионированный формат и атомарное хранение карточки отделены от анализа документов.</summary>
internal sealed class ProjectHealthCardStore(string folder)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public string CardPath(ProjectInfo project) => Path.Combine(folder,
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Identity(project)))) + ".json");

    public async Task<ProjectHealthCard?> LoadAsync(ProjectInfo project, CancellationToken ct)
    {
        var path = CardPath(project);
        if (!File.Exists(path)) return null;
        await using var stream = AtomicFile.OpenRead(path);
        ProjectHealthCard? card;
        try { card = await JsonSerializer.DeserializeAsync<ProjectHealthCard>(stream, Json, ct).ConfigureAwait(false); }
        catch (JsonException ex) { throw new InvalidDataException("Карточка повреждена. Выполните анализ заново.", ex); }
        Validate(card);
        if (Identity(card!.Project) != Identity(project)) throw new InvalidDataException("Карточка относится к другому проекту.");
        return card;
    }

    public Task SaveAsync(ProjectHealthCard card, CancellationToken ct)
    {
        Validate(card);
        return AtomicFile.WriteAllTextAsync(CardPath(card.Project), JsonSerializer.Serialize(card, Json), ct);
    }

    internal static void Validate(ProjectHealthCard? card)
    {
        if (card is null || card.Version != 1 || card.Project is null || string.IsNullOrWhiteSpace(card.Project.RootFolder)
            || string.IsNullOrWhiteSpace(card.Project.Name) || card.CreatedUtc == default
            || card.Files < 0 || card.Read < 0 || card.Read > card.Files
            || card.Parts < 0 || card.Assemblies < 0 || card.Drawings < 0 || card.Presentations < 0
            || (long)card.Parts + card.Assemblies + card.Drawings + card.Presentations != card.Files
            || card.Inputs is null || card.Issues is null
            || card.Inputs.Any(i => string.IsNullOrWhiteSpace(i.Key) || string.IsNullOrEmpty(i.Value))
            || card.Issues.Any(i => i is null || i.Severity is not ("Ошибка" or "Предупреждение")
                || string.IsNullOrWhiteSpace(i.Path) || string.IsNullOrWhiteSpace(i.Category) || i.Details is null))
            throw new InvalidDataException("Карточка повреждена или её формат не поддерживается. Выполните анализ заново.");
    }
    private static string Identity(ProjectInfo p) => ReferenceIndexService.Key(p.RootFolder) + "|" +
        (p.ProjectFile is null ? "" : ReferenceIndexService.Key(p.ProjectFile));
}
