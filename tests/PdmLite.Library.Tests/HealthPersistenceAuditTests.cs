using System.Text.Json.Nodes;
using PdmLite.Core.Documents;
using PdmLite.Core.Domain;
using PdmLite.Index;

namespace PdmLite.Library.Tests;

public sealed class HealthPersistenceAuditTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("health-persist-audit-").FullName;
    private ProjectInfo Project => new("Тест", _root);
    private ProjectHealthService Service => new(new EmptyStore(), Path.Combine(_root, "cards"));

    [Theory]
    [InlineData("Version", "2")][InlineData("Version", "0")][InlineData("Project", "null")]
    [InlineData("Files", "-1")][InlineData("Files", "3")][InlineData("Read", "1")][InlineData("Read", "-1")]
    [InlineData("Parts", "-1")][InlineData("Assemblies", "-1")][InlineData("Drawings", "-1")][InlineData("Presentations", "-1")]
    [InlineData("Inputs", "null")][InlineData("Issues", "null")][InlineData("Issues", "[null]")]
    [InlineData("CreatedUtc", "\"0001-01-01T00:00:00\"")]
    public async Task InvalidCard_IsRejectedWithActionableError(string field, string json)
    {
        var service = Service; await service.AnalyzeAsync(Project);
        var node = JsonNode.Parse(File.ReadAllText(service.CardPath(Project)))!;
        node[field] = JsonNode.Parse(json);
        File.WriteAllText(service.CardPath(Project), node.ToJsonString());
        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => service.LoadAsync(Project));
        Assert.Contains("анализ", ex.Message.ToLowerInvariant());
    }

    [Theory]
    [InlineData("Severity", "null")][InlineData("Severity", "\"Неизвестный\"")]
    [InlineData("Path", "null")][InlineData("Path", "\" \"")]
    [InlineData("Category", "null")][InlineData("Category", "\"\"")][InlineData("Details", "null")]
    public async Task InvalidIssue_IsRejectedBeforeUiUsesIt(string field, string json)
    {
        var service = Service; await service.AnalyzeAsync(Project);
        var node = JsonNode.Parse(File.ReadAllText(service.CardPath(Project)))!;
        node["Issues"]![0]![field] = JsonNode.Parse(json);
        File.WriteAllText(service.CardPath(Project), node.ToJsonString());
        await Assert.ThrowsAsync<InvalidDataException>(() => service.LoadAsync(Project));
    }

    [Theory]
    [InlineData("null")][InlineData("{")][InlineData("[]")][InlineData("{}")]
    public async Task TruncatedOrWrongRootJson_IsRejected(string json)
    {
        var service = Service; Directory.CreateDirectory(Path.GetDirectoryName(service.CardPath(Project))!);
        File.WriteAllText(service.CardPath(Project), json);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.LoadAsync(Project));
    }

    private sealed class EmptyStore : IDocumentPropertyStore
    {
        public bool IsAvailable => true;
        public string? UnavailableReason => null;
        public Task<DocumentProperties> ReadAsync(string path, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task WriteAsync(string path, DocumentPropertyChanges changes, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ReplaceReferencesAsync(string path, IReadOnlyDictionary<string, string> map, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
    public void Dispose() => Directory.Delete(_root, true);
}
