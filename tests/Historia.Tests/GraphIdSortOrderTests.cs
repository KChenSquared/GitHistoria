namespace Historia.Tests;

[Trait("tier", "unit")]
public class GraphIdSortOrderTests
{
    [Fact]
    public void CompareTo_byte_order_matches_ordinal_hex_comparison()
    {
        var shas = new[]
        {
            "0000000000000000000000000000000000000001",
            "0000000000000000000000000000000000000002",
            "0100000000000000000000000000000000000000",
            "1000000000000000000000000000000000000000",
            "7fffffffffffffffffffffffffffffffffffffff",
            "8000000000000000000000000000000000000000",
            "8000000000000000000000000000000000000001",
            "feffffffffffffffffffffffffffffffffffffff",
            "ffffffffffffffffffffffffffffffffffffffff",
        };

        var oids = shas.Select(GitObjectId.Parse).ToList();

        var sortedOids = oids.OrderBy(x => x).ToList();
        var sortedShas = shas.OrderBy(x => x, StringComparer.Ordinal).ToList();

        Assert.Equal(sortedShas, sortedOids.Select(x => x.ToString()));
    }

    [Fact]
    public void CompareTo_respects_high_bit_byte_values()
    {
        var lower = GitObjectId.Parse("7f00000000000000000000000000000000000000");
        var higher = GitObjectId.Parse("8000000000000000000000000000000000000000");

        Assert.True(lower.CompareTo(higher) < 0);
        Assert.True(higher.CompareTo(lower) > 0);
        Assert.True(lower < higher);
        Assert.True(higher > lower);
    }

    [Fact]
    public void CompareTo_sha256_sorts_consistently()
    {
        var id1 = GitObjectId.Parse("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef");

        // Invalid hex characters fail TryParse
        Assert.False(GitObjectId.TryParse("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdeg", out _));

        var idValid2 = GitObjectId.Parse("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdee");
        Assert.True(idValid2 < id1);
        Assert.True(id1 > idValid2);
    }

    [Fact]
    public void DagLayoutEngine_tie_breaks_identical_commit_timestamps_by_GitObjectId()
    {
        var engine = new DagLayoutEngine();
        var now = DateTimeOffset.UnixEpoch.AddDays(1);

        var idA = GitObjectId.Parse("1111111111111111111111111111111111111111");
        var idB = GitObjectId.Parse("2222222222222222222222222222222222222222");

        var commitA = new RawCommit(idA, [], new CommitMetadata
        {
            Id = idA,
            MessageShort = "commit A",
            AuthorName = "Alice",
            AuthorEmail = "alice@example.com",
            AuthorWhen = now,
            CommitWhen = now,
            ParentCount = 0,
        });

        var commitB = new RawCommit(idB, [], new CommitMetadata
        {
            Id = idB,
            MessageShort = "commit B",
            AuthorName = "Bob",
            AuthorEmail = "bob@example.com",
            AuthorWhen = now,
            CommitWhen = now,
            ParentCount = 0,
        });

        var decorations = new Dictionary<GitObjectId, IReadOnlyList<GraphDecorationInput>>
        {
            [idA] = [new GraphDecorationInput(DecorationType.LocalBranch, "branch-a")],
            [idB] = [new GraphDecorationInput(DecorationType.LocalBranch, "branch-b")],
        };

        // When commitA and commitB are tips with identical timestamps, id tie-breaking (idA < idB)
        // ensures commitA is chosen as the trunk tip (lane 0) even when commitB is emitted first in row order.
        var layout = engine.Compute([commitB, commitA], decorations, maxRows: 100);

        Assert.Equal(idA, layout.State.TrunkTip);
        Assert.Equal(2, layout.Layout.RowCount);
        var rowA = Enumerable.Range(0, layout.Layout.RowCount).First(i => layout.Layout.Commits[i].Id == idA);
        var rowB = Enumerable.Range(0, layout.Layout.RowCount).First(i => layout.Layout.Commits[i].Id == idB);
        Assert.Equal(0, layout.Layout.Rows[rowA].LaneIndex);
        Assert.Equal(1, layout.Layout.Rows[rowB].LaneIndex);
    }
}
