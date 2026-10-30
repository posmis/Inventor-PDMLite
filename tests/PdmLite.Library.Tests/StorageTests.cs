using PdmLite.Library.Storage;

namespace PdmLite.Library.Tests;

public class StorageTests
{
    [Fact]
    public void BuildRelativePath_UsesCategoryStandardAndDesignation() =>
        Assert.Equal("Крепёж/Болты/ГОСТ 7798-70/Болт M10-6gx25.109 ГОСТ 7798-70.ipt",
            LibraryStorage.BuildRelativePath(["Крепёж", "Болты"], "ГОСТ 7798-70", "Болт M10-6gx25.109 ГОСТ 7798-70", ".IPT"));

    [Theory]
    [InlineData("Манжета 1.1-25x47-1/4 ГОСТ 8752-79", "Манжета 1.1-25x47-1-4 ГОСТ 8752-79")]
    [InlineData("Ввод \"M20\" Lapp.", "Ввод -M20- Lapp")]
    public void SafeName_ReplacesForbiddenCharacters(string name, string expected) =>
        Assert.Equal(expected, LibraryStorage.SafeName(name));
}
