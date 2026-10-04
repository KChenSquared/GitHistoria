namespace Historia.Tests;
[Trait("tier", "unit")]

public class GraphCrawlThresholdTests
{
    [Fact]
    public void Starts_at_one_step()
    {
        var threshold = new GraphCrawlThreshold(100_000, 2_000_000);
        Assert.Equal(100_000, threshold.Target);
    }

    [Fact]
    public void Starts_at_ceiling_when_uncapped_or_step_exceeds_ceiling()
    {
        var uncapped = new GraphCrawlThreshold(0, 2_000_000);
        Assert.Equal(2_000_000, uncapped.Target);

        var negative = new GraphCrawlThreshold(-1, 2_000_000);
        Assert.Equal(2_000_000, negative.Target);

        var stepExceeds = new GraphCrawlThreshold(3_000_000, 2_000_000);
        Assert.Equal(2_000_000, stepExceeds.Target);
    }

    [Fact]
    public void Does_not_advance_before_the_viewport_reaches_the_target()
    {
        var threshold = new GraphCrawlThreshold(100_000, 2_000_000);
        Assert.False(threshold.Advance(0));
        Assert.Equal(100_000, threshold.Target);

        Assert.False(threshold.Advance(50_000));
        Assert.Equal(100_000, threshold.Target);

        Assert.False(threshold.Advance(99_999));
        Assert.Equal(100_000, threshold.Target);
    }

    [Fact]
    public void Passing_100k_moves_the_target_to_200k_only()
    {
        var threshold = new GraphCrawlThreshold(100_000, 2_000_000);
        Assert.True(threshold.Advance(100_000));
        Assert.Equal(200_000, threshold.Target);

        // A second notification on row 100k does not advance it again
        Assert.False(threshold.Advance(100_000));
        Assert.Equal(200_000, threshold.Target);
    }

    [Fact]
    public void A_fast_scroll_to_150k_targets_200k_not_300k()
    {
        var threshold = new GraphCrawlThreshold(100_000, 2_000_000);
        Assert.True(threshold.Advance(150_000));
        Assert.Equal(200_000, threshold.Target);
    }

    [Fact]
    public void Reaching_200k_moves_to_300k()
    {
        var threshold = new GraphCrawlThreshold(100_000, 2_000_000);
        Assert.True(threshold.Advance(150_000));
        Assert.Equal(200_000, threshold.Target);

        Assert.False(threshold.Advance(199_999));
        Assert.Equal(200_000, threshold.Target);

        Assert.True(threshold.Advance(200_000));
        Assert.Equal(300_000, threshold.Target);
    }

    [Fact]
    public void A_jump_well_past_the_target_lands_on_the_next_step_above_it()
    {
        var threshold = new GraphCrawlThreshold(100_000, 2_000_000);
        // Jumping straight to 550,000 sets target to (550k / 100k + 1) * 100k = 600,000
        Assert.True(threshold.Advance(550_000));
        Assert.Equal(600_000, threshold.Target);
    }

    [Fact]
    public void Never_exceeds_the_ceiling()
    {
        var threshold = new GraphCrawlThreshold(100_000, 250_000);
        Assert.Equal(100_000, threshold.Target);

        Assert.True(threshold.Advance(100_000));
        Assert.Equal(200_000, threshold.Target);

        Assert.True(threshold.Advance(200_000));
        Assert.Equal(250_000, threshold.Target); // Capped at ceiling 250,000, not 300,000

        Assert.False(threshold.Advance(250_000));
        Assert.Equal(250_000, threshold.Target);

        Assert.False(threshold.Advance(500_000));
        Assert.Equal(250_000, threshold.Target);
    }

    [Fact]
    public void Uncapped_never_advances()
    {
        var uncapped = new GraphCrawlThreshold(0, 2_000_000);
        Assert.Equal(2_000_000, uncapped.Target);
        Assert.False(uncapped.Advance(100_000));
        Assert.False(uncapped.Advance(2_500_000));
        Assert.Equal(2_000_000, uncapped.Target);
    }
}
