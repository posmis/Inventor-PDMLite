using PdmLite.Core.Naming;

namespace PdmLite.Core.Tests;

public class NaturalComparerTests
{
    [Fact]
    public void Sorts_NumbersByValue()
    {
        string[] sorted =
        [
            "Болт M6-6gx12.88(S10) ГОСТ 7798-70",
            "Болт M6-6gx12.109(S10) ГОСТ 7798-70",
            "Болт M6-6gx20.109(S10) ГОСТ 7798-70",
            "Болт M6-6gx80.109(S10) ГОСТ 7798-70",
            "Болт M8-6gx16.109(S13) ГОСТ 7798-70",
            "Болт M12-6gx20.109(S19) ГОСТ 7798-70",
            "Болт M12-6gx100.129(S19) ГОСТ 7798-70",
            "Болт M12-6gx180.129(S19) ГОСТ 7798-70",
            "Болт M20-6gx130.129(S30) ГОСТ 7798-70",
            "гайка M8",
            "Гайка M10",
        ];
        var shuffled = sorted.Reverse().ToList();

        shuffled.Sort(NaturalComparer.Instance);

        Assert.Equal(sorted, shuffled);
    }

    [Theory]
    [InlineData("a", "a", 0)]
    [InlineData("a2", "a10", -1)]
    [InlineData("a", "a1", -1)]
    [InlineData("x99999999999999999999999", "x100000000000000000000000", -1)]
    [InlineData("007", "7", -1)]
    public void Compare(string x, string y, int expected) =>
        Assert.Equal(expected, Math.Sign(NaturalComparer.Instance.Compare(x, y)));

    [Fact]
    public void Nulls_First() => Assert.True(NaturalComparer.Instance.Compare(null, "a") < 0);
}
