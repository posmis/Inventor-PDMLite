using System.Text.Json;
using PdmLite.Data;
using PdmLite.Hosting;
using PdmLite.Installation;
using PdmLite.Library.Storage;

namespace PdmLite.Library.Tests;

public sealed class InstallerStateTests : IDisposable
{
    private readonly string temp = Path.Combine(Path.GetTempPath(), "pdmlite-setup-tests-" + Guid.NewGuid().ToString("N"));
    private string Settings => Path.Combine(temp, "Settings");
    private string Root => Path.Combine(temp, "Library");

    [Fact]
    public void ExistingSettings_NeverGenerateNewCredentialsOrSeed_RegardlessOfLibraryContents()
    {
        Directory.CreateDirectory(Settings);
        Directory.CreateDirectory(Root);
        File.WriteAllText(Path.Combine(Root, "part.ipt"), "original");
        var configured = new DatabaseSettings { Host = "my-server", Port = 1234 };
        configured.SetPassword("existing");
        var json = JsonSerializer.Serialize(configured);
        File.WriteAllText(Path.Combine(Settings, "database.json"), json);
        File.WriteAllText(Path.Combine(Settings, "setup-pending.json"), "broken pending state");
        var state = SetupState.Open(Settings, Root);
        Assert.NotNull(state.Existing);
        Assert.Null(state.Pending);
        Assert.Equal("my-server", state.Existing.Host);
        Assert.Equal("existing", state.Existing.GetPassword());
        Assert.Equal(json, File.ReadAllText(Path.Combine(Settings, "database.json")));
        Assert.Equal("original", File.ReadAllText(Path.Combine(Root, "part.ipt")));
    }

    [Theory]
    [InlineData("{", typeof(JsonException))]
    [InlineData("null", typeof(InvalidDataException))]
    [InlineData("{}", typeof(InvalidDataException))]
    public void BrokenExistingSettings_AreNotSilentlyReplacedByFreshInstallation(string json, Type expectedError)
    {
        Directory.CreateDirectory(Settings);
        var file = Path.Combine(Settings, "database.json");
        File.WriteAllText(file, json);
        Assert.Throws(expectedError, () => SetupState.Open(Settings, Root));
        Assert.Equal(json, File.ReadAllText(file));
        Assert.False(File.Exists(Path.Combine(Settings, "setup-pending.json")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NonEmptyLibrary_WithFilesOrOnlyFolders_IsPreserved(bool onlyFolder)
    {
        Directory.CreateDirectory(Root);
        if (onlyFolder) Directory.CreateDirectory(Path.Combine(Root, "Keep"));
        else File.WriteAllText(Path.Combine(Root, "part.ipt"), "original");
        Assert.Throws<InvalidOperationException>(() => SetupState.Open(Settings, Root));
        Assert.Single(Directory.EnumerateFileSystemEntries(Root));
        Assert.False(File.Exists(Path.Combine(Settings, "setup-pending.json")));
    }

    [Fact]
    public void InterruptedInstall_ReusesCredentialsAndCanResumeWithRestoredFiles()
    {
        var first = SetupState.Open(Settings, Root).Pending!;
        Directory.CreateDirectory(Root);
        File.WriteAllText(Path.Combine(Root, "part.ipt"), "restored");
        var retry = SetupState.Open(Settings, Root).Pending!;
        Assert.Equal(first.Admin.GetPassword(), retry.Admin.GetPassword());
        Assert.Equal(first.Database.GetPassword(), retry.Database.GetPassword());
        Assert.NotEqual(first.Admin.GetPassword(), first.Database.GetPassword());
        Assert.Equal(64, retry.Database.GetPassword().Length);
        Assert.False(File.Exists(Path.Combine(Settings, "database.json")));
    }

    [Fact]
    public void InterruptedInstall_CannotSwitchRootOrOverwriteItsState()
    {
        SetupState.Open(Settings, Root);
        var path = Path.Combine(Settings, "setup-pending.json");
        var before = File.ReadAllText(path);
        Assert.Throws<InvalidOperationException>(() => SetupState.Open(Settings, Root + "-other"));
        Assert.Equal(before, File.ReadAllText(path));
    }

    [Fact]
    public void Complete_PublishesConnectionOnlyAfterRestore_ThenUpdatesPreserveLibrary()
    {
        var state = SetupState.Open(Settings, Root);
        Assert.Throws<InvalidOperationException>(() => state.Complete(new LibrarySettings { RootFolder = Root }));
        Assert.False(File.Exists(Path.Combine(Settings, "database.json")));
        state.Pending!.Restored = true;
        state.Save();
        state.Complete(new LibrarySettings { RootFolder = Root });
        var saved = new JsonSettings<LibrarySettings>("library", Settings).Value;
        Assert.Equal(Root, saved.RootFolder);
        Assert.True(File.Exists(Path.Combine(Settings, "local-postgres-admin.json")));
        Assert.False(File.Exists(Path.Combine(Settings, "setup-pending.json")));
        Assert.NotNull(SetupState.Open(Settings, Root + "-ignored-on-update").Existing);
    }

    [Fact]
    public void IndependentInstallations_UseDifferentCredentials()
    {
        var one = SetupState.Open(Settings, Root).Pending!;
        var two = SetupState.Open(Settings + "-other", Root + "-other").Pending!;
        Assert.NotEqual(one.Admin.GetPassword(), two.Admin.GetPassword());
        Assert.NotEqual(one.Database.GetPassword(), two.Database.GetPassword());
    }

    public void Dispose() { if (Directory.Exists(temp)) Directory.Delete(temp, true); }
}
