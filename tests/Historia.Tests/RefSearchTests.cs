namespace Historia.Tests;

[Trait("tier", "unit")]
public class RefSearchTests
{
    [Fact]
    public void RankOrdersByScoreDescending()
    {
        var items = new[] { "remotes/origin/main", "main", "feature/main-loop" };
        var query = FuzzyScorer.PrepareQuery("main");

        var ranked = RefSearch.Rank(items, x => x, query);

        Assert.True(ranked.Count >= 2);
        Assert.Equal("main", ranked[0].Item);
        for (int i = 1; i < ranked.Count; i++)
        {
            Assert.True(ranked[i - 1].Score >= ranked[i].Score);
        }
    }

    [Fact]
    public void RankHonoursLimit()
    {
        var items = Enumerable.Range(1, 50).Select(i => $"feature/auth-{i:D2}").ToList();
        var query = FuzzyScorer.PrepareQuery("auth");

        var unlimited = RefSearch.Rank(items, x => x, query);
        var limited = RefSearch.Rank(items, x => x, query, limit: 10);

        Assert.Equal(50, unlimited.Count);
        Assert.Equal(10, limited.Count);
        for (int i = 0; i < 10; i++)
        {
            Assert.Equal(unlimited[i].Item, limited[i].Item);
        }
    }

    [Fact]
    public void RankDropsNonMatches()
    {
        var items = new[] { "feature/auth", "fix/bug", "main" };
        var query = FuzzyScorer.PrepareQuery("auth");

        var ranked = RefSearch.Rank(items, x => x, query);

        Assert.Single(ranked);
        Assert.Equal("feature/auth", ranked[0].Item);
    }

    [Fact]
    public void RankIsStableForEqualScores()
    {
        // Items with equal score and equal length tiebreak on ordinal label.
        var items = new[] { "bravo", "alpha", "charlie" };
        var query = FuzzyScorer.PrepareQuery("a");

        var ranked = RefSearch.Rank(items, x => x, query);

        // "alpha" matches prefix 'a' at start of word (prefix boost + start of word bonus)
        // Let's verify equal items with exact same prefix:
        var equalItems = new[] { "branch-c", "branch-a", "branch-b" };
        var branchQuery = FuzzyScorer.PrepareQuery("branch-");
        var branchRanked = RefSearch.Rank(equalItems, x => x, branchQuery);

        Assert.Equal(3, branchRanked.Count);
        Assert.Equal("branch-a", branchRanked[0].Item);
        Assert.Equal("branch-b", branchRanked[1].Item);
        Assert.Equal("branch-c", branchRanked[2].Item);
    }

    [Fact]
    public void RankOfEmptyQueryIsEmpty()
    {
        var items = new[] { "main", "feature/auth" };
        var empty = RefSearch.Rank(items, x => x, FuzzyScorer.PrepareQuery(""));
        var whitespace = RefSearch.Rank(items, x => x, FuzzyScorer.PrepareQuery("   "));

        Assert.Empty(empty);
        Assert.Empty(whitespace);
    }
}
