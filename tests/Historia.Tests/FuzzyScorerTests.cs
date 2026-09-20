namespace Historia.Tests;

[Trait("tier", "unit")]
public class FuzzyScorerTests
{
    [Fact]
    public void PrefixMatchOutranksInteriorMatch()
    {
        var query = FuzzyScorer.PrepareQuery("main");
        var prefixScore = FuzzyScorer.ScoreItem("main", query);
        var interiorScore = FuzzyScorer.ScoreItem("remotes/origin/main", query);

        Assert.True(prefixScore.Score > interiorScore.Score);
    }

    [Fact]
    public void ShorterLabelWinsOnEqualPrefix()
    {
        var query = FuzzyScorer.PrepareQuery("feat");
        var shortScore = FuzzyScorer.ScoreItem("feat", query);
        var longScore = FuzzyScorer.ScoreItem("feature-flags", query);

        Assert.True(shortScore.Score > longScore.Score);
    }

    [Fact]
    public void SeparatorBoostBeatsPlainInterior()
    {
        var sepScore = FuzzyScorer.ScoreFuzzy("feature/auth", "fa");
        var plainScore = FuzzyScorer.ScoreFuzzy("sofalike", "fa");

        Assert.True(sepScore.Score > plainScore.Score);
    }

    [Fact]
    public void ConsecutiveRunIsBoosted()
    {
        var consecutive = FuzzyScorer.ScoreFuzzy("auth", "auth");
        var scattered = FuzzyScorer.ScoreFuzzy("a-u-t-h-x", "auth");

        Assert.True(consecutive.Score > scattered.Score);
    }

    [Fact]
    public void CaseMatchAddsOne()
    {
        var sameCase = FuzzyScorer.ScoreFuzzy("auth", "auth");
        var differentCase = FuzzyScorer.ScoreFuzzy("auth", "Auth");

        Assert.Equal(1, sameCase.Score - differentCase.Score);
    }

    [Fact]
    public void NonContiguousDisallowedWhenQuoted()
    {
        var query = FuzzyScorer.PrepareQuery("\"feat\"");
        var nonContig = FuzzyScorer.ScoreItem("f-e-a-t", query);
        var contig = FuzzyScorer.ScoreItem("feature", query);

        Assert.Equal(0, nonContig.Score);
        Assert.True(contig.Score > 0);
    }

    [Fact]
    public void MultiPieceRequiresEveryPiece()
    {
        var query = FuzzyScorer.PrepareQuery("feat auth");
        var both = FuzzyScorer.ScoreItem("feature/auth-rework", query);
        var partial = FuzzyScorer.ScoreItem("feature/rework", query);

        Assert.True(both.Score > 0);
        Assert.Equal(0, partial.Score);
    }

    [Fact]
    public void BackslashQueryMatchesSlashTarget()
    {
        var query = FuzzyScorer.PrepareQuery(@"feature\auth");
        var result = FuzzyScorer.ScoreItem("feature/auth", query);

        Assert.True(result.Score > 0);
    }

    [Fact]
    public void EmptyQueryScoresNothing()
    {
        var empty = FuzzyScorer.ScoreItem("feature", "");
        var whitespace = FuzzyScorer.ScoreItem("feature", "   ");

        Assert.Equal(0, empty.Score);
        Assert.Empty(empty.Matches);
        Assert.Equal(0, whitespace.Score);
        Assert.Empty(whitespace.Matches);
    }

    [Fact]
    public void PositionsAreAscendingAndInBounds()
    {
        const string target = "feature/auth-provider/oauth2";
        const string query = "faoth2";
        var result = FuzzyScorer.ScoreFuzzy(target, query);

        Assert.True(result.Score > 0);
        Assert.Equal(query.Length, result.Positions.Count);
        for (int i = 0; i < result.Positions.Count; i++)
        {
            Assert.InRange(result.Positions[i], 0, target.Length - 1);
            if (i > 0)
            {
                Assert.True(result.Positions[i] > result.Positions[i - 1]);
            }
        }
    }

    [Fact]
    public void OverlongInputIsCappedNotThrown()
    {
        string target = new string('a', 10_000);
        string query = new string('a', 500);

        var score = FuzzyScorer.ScoreFuzzy(target, query);
        Assert.True(score.Score > 0);
    }
}
