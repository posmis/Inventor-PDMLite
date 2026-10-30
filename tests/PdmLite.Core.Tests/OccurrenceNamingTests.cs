using PdmLite.Core.Documents;

namespace PdmLite.Core.Tests;

public class OccurrenceNamingTests
{
    private const string Bolt = "Болт M8-6gx30.109(S13) ГОСТ 7798-70";

    [Theory]
    [InlineData(Bolt + ":1", true)]
    [InlineData(Bolt + ":12", true)]
    [InlineData("[c]:Болт ГОСТ 7798-70 M8-6g×30(S13):9", false)]
    [InlineData(Bolt, false)]
    [InlineData(Bolt + ":x", false)]
    public void IsDefault(string name, bool expected) => Assert.Equal(expected, OccurrenceNaming.IsDefault(name, Bolt));

    [Fact]
    public void Plan_RenamesOnlyOldNames_WithFreeNumbers()
    {
        string[] all = ["Рама:1", Bolt + ":1", "[c]:Болт ГОСТ 7798-70 M8-6g×30(S13):9", "Болт ГОСТ 7798-70 M8-6g×30(S13):10"];

        var plan = OccurrenceNaming.Plan(all,
        [
            (Bolt + ":1", Bolt),
            ("[c]:Болт ГОСТ 7798-70 M8-6g×30(S13):9", Bolt),
            ("Болт ГОСТ 7798-70 M8-6g×30(S13):10", Bolt),
        ]);

        Assert.Equal(
        [
            ("[c]:Болт ГОСТ 7798-70 M8-6g×30(S13):9", Bolt + ":2"),
            ("Болт ГОСТ 7798-70 M8-6g×30(S13):10", Bolt + ":3"),
        ], plan);
    }

    [Fact]
    public void Plan_EmptyBaseName_IsSkipped() =>
        Assert.Empty(OccurrenceNaming.Plan(["a:1"], [("a:1", "")]));
}
