using PdmLite.Library.Items;

namespace PdmLite.Library.Tests;

public class GeometrySearchTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(42)]
    [InlineData(12345)]
    public void SortedSearch_MatchesExhaustivePairs_IncludingToleranceBoundariesAndTypes(int seed)
    {
        var random = new Random(seed);
        var rows = Enumerable.Range(0, 250).Select(i =>
        {
            var cluster = random.Next(5);
            return (Item: i, Type: random.Next(2), Volume: (double?)(100 + cluster + random.NextDouble() * .02),
                Area: (double?)(50 + cluster + random.NextDouble() * .01));
        }).ToList();
        rows.AddRange([
            (250, 0, 100d, 50d), (251, 0, 100d / (1 - 1e-4), 50d),
            (252, 0, 100d / (1 - 1e-4) + 1e-9, 50d), (253, 1, 100d, 50d),
            (254, 0, null, 50d), (255, 0, 0d, 50d)]);
        var expected = new HashSet<(int, int)>();
        for (var a = 0; a < rows.Count; a++)
            for (var b = a + 1; b < rows.Count; b++)
                if (rows[a].Type == rows[b].Type && LibraryGeometry.Same(rows[a].Volume, rows[a].Area, rows[b].Volume, rows[b].Area))
                    expected.Add((a, b));
        var actual = LibraryGeometry.MatchingPairs(rows).Select(p => (Math.Min(p.First, p.Second), Math.Max(p.First, p.Second))).ToHashSet();
        Assert.True(expected.SetEquals(actual));
    }

    [Fact]
    public void LargeSparseLibrary_HasNoFalseMatches()
    {
        var rows = Enumerable.Range(1, 20000).Select(i => (i, 0, (double?)(100 * Math.Pow(1.001, i)), (double?)i));
        Assert.Empty(LibraryGeometry.MatchingPairs(rows));
    }

    [Fact]
    public void CancellationDuringDenseGroup_StopsBeforeNextPair()
    {
        using var stop = new CancellationTokenSource();
        var rows = Enumerable.Range(0, 5000).Select(i => (i, 0, (double?)100, (double?)50));
        using var pairs = LibraryGeometry.MatchingPairs(rows, stop.Token).GetEnumerator();
        Assert.True(pairs.MoveNext());
        stop.Cancel();
        Assert.Throws<OperationCanceledException>(() => pairs.MoveNext());
    }

    [Fact]
    public void AlreadyCancelled_RejectsEmptyInput()
    {
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        Assert.Throws<OperationCanceledException>(() => LibraryGeometry.MatchingPairs<int>([], stop.Token).ToList());
    }

    [Fact]
    public void ToleranceChain_KeepsBothEdges()
    {
        var rows = new[] { (0, 0, (double?)100, (double?)50), (1, 0, (double?)100.009, (double?)50), (2, 0, (double?)100.018, (double?)50) };
        Assert.Equal([(0, 1), (1, 2)], LibraryGeometry.MatchingPairs(rows));
    }
}
