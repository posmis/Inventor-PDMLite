using PdmLite.Library.Storage;

namespace PdmLite.Library.Tests;

public sealed class StorageAuditTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("storage-audit-").FullName;

    [Theory]
    [InlineData("../outside.ipt")][InlineData(@"..\outside.ipt")][InlineData("/outside.ipt")]
    [InlineData("C:/outside.ipt")][InlineData("a.ipt:stream")][InlineData("a/../../outside.ipt")]
    [InlineData("a./outside.ipt")][InlineData("NUL.ipt")]
    public void CopyIn_RejectsInvalidTargetAndPreservesOutsideFile(string relative)
    {
        var source = Path.Combine(_root, "source.ipt"); File.WriteAllText(source, "source");
        var outside = Path.Combine(_root, "outside.ipt"); File.WriteAllText(outside, "original");
        var storage = new LibraryStorage(Path.Combine(_root, "library"));
        Assert.ThrowsAny<ArgumentException>(() => storage.CopyIn(source, relative));
        Assert.Equal("original", File.ReadAllText(outside));
        Assert.False(Directory.Exists(storage.Root));
    }

    [Theory]
    [InlineData("")][InlineData("ipt")][InlineData("../ipt")][InlineData(".ipt/../bad")]
    [InlineData(".ipt:stream")][InlineData(".ipt ")][InlineData(".")][InlineData(".ipt\\bad")]
    public void GeneratedPath_RejectsExtensionInjection(string extension) =>
        Assert.Throws<ArgumentException>(() => LibraryStorage.BuildRelativePath(["Крепёж"], "ГОСТ", "Болт", extension));

    [Theory]
    [InlineData("COM¹")][InlineData("COM²")][InlineData("COM³")]
    [InlineData("LPT¹")][InlineData("LPT²")][InlineData("LPT³")]
    [InlineData("CONIN$")][InlineData("CONOUT$")]
    public void GeneratedPath_SanitizesAllSpecialWindowsDeviceNames(string name)
    {
        var relative = LibraryStorage.BuildRelativePath([], null, name, ".ipt");
        Assert.Equal(name + "_.ipt", relative);
        Assert.True(new LibraryStorage(_root).Contains(new LibraryStorage(_root).GetFullPath(relative)));
    }

    [Fact]
    public void GetFullPath_NormalizesWindowsRelativeSeparatorsOnLinuxToo()
    {
        var storage = new LibraryStorage(_root);
        Assert.Equal(Path.Combine(_root, "Крепёж", "Болт.ipt"), storage.GetFullPath(@"Крепёж\Болт.ipt"));
    }
    public void Dispose() => Directory.Delete(_root, true);
}
