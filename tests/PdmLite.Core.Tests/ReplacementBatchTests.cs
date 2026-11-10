using PdmLite.Core.Documents;

namespace PdmLite.Core.Tests;

public class ReplacementBatchTests
{
    [Fact]
    public void EveryReplacementFailsWithoutChanges_DoesNotReportDocumentModifiedOrRename()
    {
        var renames = 0;
        var result = ReplacementBatch.Apply(new[] { 1, 2 }, _ => throw new IOException("replacement failed"),
            _ => false, _ => { renames++; return 0; });
        Assert.Equal(0, result.Replaced); Assert.False(result.Changed); Assert.Equal(0, renames);
        Assert.Equal(2, result.Failures.Count);
        Assert.All(result.Failures, failure => Assert.Equal(false, failure.Changed));
    }

    [Fact]
    public void PartialSuccess_RenamesOnlySuccessfulTargets_AndReportsChange()
    {
        IReadOnlyList<int>? renamed = null;
        var result = ReplacementBatch.Apply(new[] { 1, 2, 3 }, i => { if (i == 2) throw new IOException("failed"); },
            _ => false, succeeded => { renamed = succeeded; return succeeded.Count; });
        Assert.Equal(2, result.Replaced); Assert.True(result.Changed); Assert.Equal([1, 3], renamed);
        Assert.Equal(2, Assert.Single(result.Failures).Item);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [InlineData(null)]
    public void FailedCall_ReadbackDistinguishesPartialChangeFromUnknown(bool? observed)
    {
        var result = ReplacementBatch.Apply(new[] { "component" }, _ => throw new IOException("COM failed"), _ => observed,
            _ => throw new Exception("no successful targets"));
        Assert.Equal(0, result.Replaced); Assert.Equal(observed == true, result.Changed);
        Assert.Equal(observed, Assert.Single(result.Failures).Changed);
    }

    [Fact]
    public void ReadbackFails_PreservesBothErrorsAndDoesNotClaimUnchanged()
    {
        var result = ReplacementBatch.Apply(new[] { 1 }, _ => throw new IOException("replacement"),
            _ => throw new InvalidOperationException("readback"), _ => 0);
        var failure = Assert.Single(result.Failures);
        Assert.Null(failure.Changed);
        Assert.Equal(2, Assert.IsType<AggregateException>(failure.Error).InnerExceptions.Count);
    }
}
