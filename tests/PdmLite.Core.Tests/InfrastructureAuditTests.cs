using System.Text.Json;
using PdmLite.Core.IO;
using PdmLite.Hosting;

namespace PdmLite.Core.Tests;

public sealed class InfrastructureAuditTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("pdm-audit-").FullName;
    public sealed class Settings { public string Value { get; set; } = "default"; }

    public static IEnumerable<object[]> UnsafePaths() => new[] {
        "../a.ipt", "a/../../b.ipt", @"..\a.ipt", @"a\..\..\b.ipt", "/tmp/a.ipt", @"C:\a.ipt", "C:a.ipt",
        @"\\server\share\a.ipt", @"\a.ipt", "a//b.ipt", "./a.ipt", "a/./b.ipt", "a/../b.ipt",
        "a.ipt:stream", "a\0.ipt", "a\n.ipt", "a?.ipt", "a*.ipt", "a|b.ipt", "a<b.ipt", "a>b.ipt", "a\"b.ipt",
        "a./b.ipt", "a /b.ipt", "a.ipt ", "a.ipt.", "a/", "", "  ", "CON.ipt", "nul.ipt", "COM1/a.ipt",
        "LPT9.ipt", "COM¹.ipt", "LPT².ipt", "CONIN$.ipt", "CONOUT$.ipt"
    }.Select(p => new object[] { p });

    [Theory, MemberData(nameof(UnsafePaths))]
    public void RelativePaths_RejectEscapesAliasesAndWindowsDevices(string relative) =>
        Assert.ThrowsAny<ArgumentException>(() => ContainedPath.Resolve(_root, relative));

    [Theory]
    [InlineData("Крепёж/Болт.ipt")]
    [InlineData(@"Крепёж\Болт.ipt")]
    [InlineData("_versions/123/Болт.ipt")]
    [InlineData("DIN 471/Кольцо 15x1.ipt")]
    [InlineData("CON_/a.ipt")]
    public void RelativePaths_KeepValidLibraryFilesInsideRoot(string relative)
    {
        var full = ContainedPath.Resolve(_root, relative);
        Assert.True(ContainedPath.Contains(_root, full));
        Assert.EndsWith(relative.Replace('\\', '/').Replace('/', Path.DirectorySeparatorChar), full);
    }

    [Fact]
    public void Contains_SeparatesSiblingFolderAndSupportsFilesystemRoot()
    {
        Assert.False(ContainedPath.Contains(_root, _root));
        Assert.False(ContainedPath.Contains(_root, _root + "-other/a.ipt"));
        Assert.False(ContainedPath.Contains(_root, Path.Combine(_root, "..", "a.ipt")));
        Assert.True(ContainedPath.Contains(Path.GetPathRoot(_root)!, Path.Combine(_root, "a.ipt")));
    }

    [Fact]
    public async Task AtomicWrites_ConcurrentWritersProduceOneCompleteValueAndNoTemporaryFiles()
    {
        var file = Path.Combine(_root, "data.json");
        var contents = Enumerable.Range(0, 32).Select(i => JsonSerializer.Serialize(new { id = i, text = new string((char)('a' + i % 26), 20000) })).ToArray();
        await Task.WhenAll(contents.Select(text => Task.Run(() => AtomicFile.WriteAllText(file, text))));
        Assert.Contains(File.ReadAllText(file), contents);
        Assert.Single(Directory.GetFiles(_root));
    }

    [Fact]
    public async Task AtomicWrite_CanceledWriteKeepsOldContent()
    {
        var file = Path.Combine(_root, "data"); File.WriteAllText(file, "old");
        using var ct = new CancellationTokenSource(); ct.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AtomicFile.WriteAllTextAsync(file, "new", ct.Token));
        Assert.Equal("old", File.ReadAllText(file)); Assert.Single(Directory.GetFiles(_root));
    }

    [Fact]
    public void AtomicWrite_RenameFailureCleansOnlyItsOwnTemporaryFile()
    {
        var target = Path.Combine(_root, "directory"); Directory.CreateDirectory(target);
        var unrelated = target + ".tmp"; File.WriteAllText(unrelated, "keep");
        Assert.ThrowsAny<IOException>(() => AtomicFile.WriteAllText(target, "data"));
        Assert.Equal("keep", File.ReadAllText(unrelated)); Assert.Single(Directory.GetFiles(_root));
    }

    [Fact]
    public async Task Settings_ConcurrentInstancesDoNotShareTemporaryFile()
    {
        await Task.WhenAll(Enumerable.Range(0, 24).Select(i => Task.Run(() =>
        {
            var settings = new JsonSettings<Settings>("settings", _root);
            settings.Value.Value = new string((char)('a' + i), 1000); settings.Save();
        })));
        Assert.Equal(1000, new JsonSettings<Settings>("settings", _root).Value.Value.Length);
        Assert.Single(Directory.GetFiles(_root));
    }

    [Theory]
    [InlineData("null")][InlineData("[]")][InlineData("42")][InlineData("\"text\"")]
    [InlineData("")][InlineData("{")][InlineData("{\"Value\": []}")]
    public void Settings_DamagedContentUsesDefaults(string json)
    {
        File.WriteAllText(Path.Combine(_root, "settings.json"), json);
        Assert.Equal("default", new JsonSettings<Settings>("settings", _root).Value.Value);
    }

    [Theory]
    [InlineData("../escape")][InlineData(@"..\escape")][InlineData("/absolute")]
    [InlineData("a:stream")][InlineData(" ")][InlineData("")][InlineData("CON")]
    public void Settings_InvalidNamesDoNotWriteOutsideFolder(string name) =>
        Assert.ThrowsAny<ArgumentException>(() => new JsonSettings<Settings>(name, _root));

    [Fact]
    public void Settings_SerializationFailurePreservesLastSavedFile()
    {
        var valid = new JsonSettings<Settings>("settings", _root); valid.Save();
        var original = File.ReadAllText(valid.FilePath);
        var cyclic = new JsonSettings<Node>("settings", _root); cyclic.Value.Next = cyclic.Value;
        Assert.Throws<JsonException>(() => cyclic.Save());
        Assert.Equal(original, File.ReadAllText(valid.FilePath)); Assert.Single(Directory.GetFiles(_root));
    }
    public sealed class Node { public Node? Next { get; set; } }

    [Theory]
    [InlineData("null")][InlineData("{\"Modules\":null}")][InlineData("{\"Modules\":[null]}")]
    [InlineData("{\"Modules\":[{}]}")][InlineData("{\"Modules\":[{\"Id\":\" \"}]}")]
    [InlineData("{\"Modules\":[{\"Id\":\"A\"},{\"Id\":\"a\"}]}")]
    public void Modules_InvalidShapeFallsBackWithDiagnostic(string json)
    {
        var file = Path.Combine(_root, "modules.json"); File.WriteAllText(file, json);
        var config = ModulesConfig.Load(file);
        Assert.NotNull(config.LoadError); Assert.True(config.IsEnabled("A"));
    }

    public void Dispose() => Directory.Delete(_root, true);
}
