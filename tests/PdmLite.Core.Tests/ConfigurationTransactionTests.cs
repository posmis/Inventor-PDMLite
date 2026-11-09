using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PdmLite.Data;
using PdmLite.Hosting;

namespace PdmLite.Core.Tests;

public sealed class ConfigurationTransactionTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("pdm-configuration-").FullName;
    public sealed class Library { public string RootFolder { get; set; } = "old-library"; }

    [Theory]
    [InlineData("database.json")]
    [InlineData("library.json")]
    public void FailedFirstOrSecondWrite_RestoresFiles_AndDoesNotPublishMutableValues(string blocked)
    {
        var database = new JsonSettings<DatabaseSettings>("database", root); database.Value.Host = "old-host"; database.Save();
        var library = new JsonSettings<Library>("library", root); library.Save();
        var dbValue = database.Value; var libValue = library.Value;
        var dbText = File.ReadAllText(database.FilePath); var libText = File.ReadAllText(library.FilePath);
        var path = Path.Combine(root, blocked); File.Delete(path); Directory.CreateDirectory(path);
        var error = Record.Exception(() => SettingsTransaction.SavePair(database, new() { Host = "new-host" }, library, new() { RootFolder = "new-library" }));
        Assert.True(error is IOException or UnauthorizedAccessException);
        Assert.Same(dbValue, database.Value); Assert.Same(libValue, library.Value);
        Assert.Equal("old-host", database.Value.Host); Assert.Equal("old-library", library.Value.RootFolder);
        if (blocked != "database.json") Assert.Equal(dbText, File.ReadAllText(database.FilePath));
        if (blocked != "library.json") Assert.Equal(libText, File.ReadAllText(library.FilePath));
        Assert.False(File.Exists(Path.Combine(root, ".configuration-before.json")));
    }

    [Fact]
    public async Task IndependentConcurrentSaves_LeaveOneCompleteConfigurationPair()
    {
        await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(() =>
        {
            var database = new JsonSettings<DatabaseSettings>("database", root);
            var library = new JsonSettings<Library>("library", root);
            SettingsTransaction.SavePair(database, new() { Host = "generation-" + i }, library, new() { RootFolder = "generation-" + i });
        })));
        Assert.Equal(new JsonSettings<DatabaseSettings>("database", root).Value.Host, new JsonSettings<Library>("library", root).Value.RootFolder);
    }
    private void WriteInterruptedState(bool blockLibrary)
    {
        File.WriteAllText(Path.Combine(root, "database.json"), "{\"Host\":\"half-written\"}");
        if (blockLibrary) Directory.CreateDirectory(Path.Combine(root, "library.json"));
        else File.WriteAllText(Path.Combine(root, "library.json"), "{\"RootFolder\":\"new-library\"}");
        File.WriteAllText(Path.Combine(root, ".configuration-before.json"), JsonSerializer.Serialize(new Dictionary<string,string?>
        {
            ["database.json"] = "{\"Host\":\"original-host\"}", ["library.json"] = "{\"RootFolder\":\"original-library\"}",
        }));
    }
    [Fact]
    public void InterruptedSave_IsRecoveredBeforeEitherSettingsObjectIsLoaded()
    {
        WriteInterruptedState(false);
        Assert.Equal("original-library", new JsonSettings<Library>("library", root).Value.RootFolder);
        Assert.Equal("original-host", new JsonSettings<DatabaseSettings>("database", root).Value.Host);
        Assert.False(File.Exists(Path.Combine(root, ".configuration-before.json")));
    }
    [Fact]
    public void FailedRecovery_RetainsJournal_RejectsDefaults_AndCanBeRetried()
    {
        WriteInterruptedState(true);
        Assert.Throws<InvalidOperationException>(() => new JsonSettings<DatabaseSettings>("database", root));
        Assert.True(File.Exists(Path.Combine(root, ".configuration-before.json")));
        Directory.Delete(Path.Combine(root, "library.json"));
        Assert.Equal("original-host", new JsonSettings<DatabaseSettings>("database", root).Value.Host);
        Assert.Equal("original-library", new JsonSettings<Library>("library", root).Value.RootFolder);
    }
    [Fact]
    public void ActiveDatabaseSettings_AreIndependentOfInputAndReturnedMutableCopies()
    {
        var input = new DatabaseSettings { Host = "original" };
        var database = new PdmDatabase(input, NullLoggerFactory.Instance);
        input.Host = "mutated-before-save"; Assert.Equal("original", database.Settings.Host);
        var next = new DatabaseSettings { Host = "next" }; database.Configure(next); next.Host = "mutated-after-configure";
        var exposed = database.Settings; exposed.Host = "mutated-getter";
        Assert.Equal("next", database.Settings.Host);
    }
    public void Dispose() => Directory.Delete(root, true);
}
