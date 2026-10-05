namespace Historia.Tests;

/// <summary>Remote numbers are 1-based positions in case-insensitive alphabetical order, handed
/// out only when two or more remotes are configured.</summary>
[Trait("tier", "unit")]
public sealed class RemoteNumberingTests
{
    [Fact]
    public void Numbers_follow_case_insensitive_alphabetical_order()
    {
        var n = RemoteNumbering.From(["Upstream", "origin", "fork"]);

        Assert.True(n.IsActive);
        Assert.Equal(1, n.NumberOf("fork"));
        Assert.Equal(2, n.NumberOf("origin"));
        Assert.Equal(3, n.NumberOf("Upstream"));
    }

    [Fact]
    public void A_single_remote_is_not_numbered()
    {
        var n = RemoteNumbering.From(["origin"]);

        Assert.False(n.IsActive);
        Assert.Equal(0, n.NumberOf("origin"));
        Assert.Equal("origin", n.TooltipOf("origin"));
    }

    [Fact]
    public void An_unknown_name_gets_no_number()
    {
        var n = RemoteNumbering.From(["origin", "upstream"]);

        Assert.Equal(0, n.NumberOf("ghost"));
        Assert.Equal("ghost", n.TooltipOf("ghost"));
    }

    [Fact]
    public void Tooltips_carry_number_and_name()
    {
        var n = RemoteNumbering.From(["upstream", "origin"]);

        Assert.Equal("1 · origin", n.TooltipOf("origin"));
        Assert.Equal("2 · upstream", n.TooltipOf("upstream"));
        Assert.Same(n.TooltipOf("origin"), n.TooltipOf("origin"));
    }

    [Fact]
    public void Names_differing_only_in_case_are_ordered_ordinally()
    {
        var n = RemoteNumbering.From(["a", "A"]);

        Assert.Equal(1, n.NumberOf("A"));
        Assert.Equal(2, n.NumberOf("a"));
        Assert.True(RemoteNumbering.Comparer.Compare("A", "a") < 0);
        Assert.True(RemoteNumbering.Comparer.Compare("alpha", "Zeta") < 0);
    }

    [Fact]
    public void Empty_input_numbers_nothing()
    {
        var n = RemoteNumbering.From([]);

        Assert.False(n.IsActive);
        Assert.Equal(0, n.NumberOf("origin"));
        Assert.False(RemoteNumbering.Empty.IsActive);
        Assert.Equal(0, RemoteNumbering.Empty.NumberOf("origin"));
    }
}
