namespace Historia.Tests;

/// <summary>T3.2 of the debloat audit: the graph find cursor lifted out of
/// <c>CommitGraphControl</c>.</summary>
[Trait("tier", "unit")]
public class GraphMatchCursorTests
{
    [Fact]
    public void Reset_puts_the_cursor_on_the_first_match()
    {
        var c = new GraphMatchCursor();
        c.Reset([4, 9, 15]);

        Assert.Equal(3, c.Count);
        Assert.True(c.HasMatches);
        Assert.Equal(0, c.Cursor);
        Assert.Equal(4, c.CurrentRow);
        Assert.Equal([4, 9, 15], c.Rows);
        Assert.NotNull(c.RowSet);
        Assert.Contains(9, c.RowSet!);
    }

    [Fact]
    public void Empty_reset_clears_everything()
    {
        var c = new GraphMatchCursor();
        c.Reset([1, 2]);
        c.Reset([]);

        Assert.Equal(0, c.Count);
        Assert.False(c.HasMatches);
        Assert.Equal(-1, c.Cursor);
        Assert.Equal(-1, c.CurrentRow);
        Assert.Null(c.RowSet);
    }

    [Fact]
    public void Step_wraps_forward_and_backward()
    {
        var c = new GraphMatchCursor();
        c.Reset([10, 20, 30]);

        Assert.Equal(20, c.Step(+1));
        Assert.Equal(30, c.Step(+1));
        Assert.Equal(10, c.Step(+1));   // wrap past the end
        Assert.Equal(30, c.Step(-1));   // wrap past the start
        Assert.Equal(20, c.Step(-1));
    }

    [Fact]
    public void Step_on_an_empty_set_is_a_no_op_returning_minus_one()
    {
        var c = new GraphMatchCursor();
        Assert.Equal(-1, c.Step(1));
        Assert.Equal(-1, c.Cursor);
    }

    [Fact]
    public void Single_match_step_stays_put()
    {
        var c = new GraphMatchCursor();
        c.Reset([7]);
        Assert.Equal(7, c.Step(1));
        Assert.Equal(7, c.Step(1));
        Assert.Equal(7, c.Step(-1));
    }

    [Theory]
    [InlineData(true, "0 / 0")]
    [InlineData(false, "")]
    public void Label_distinguishes_no_search_from_no_results(bool hasText, string expected)
    {
        Assert.Equal(expected, new GraphMatchCursor().Label(hasText));
    }

    [Fact]
    public void Label_tracks_the_one_based_cursor_position()
    {
        var c = new GraphMatchCursor();
        c.Reset([3, 6, 9, 12]);
        Assert.Equal("1 / 4", c.Label(true));
        c.Step(1);
        Assert.Equal("2 / 4", c.Label(true));
        c.Step(-1);
        c.Step(-1);
        Assert.Equal("4 / 4", c.Label(true));  // wrapped to the last
    }
}
