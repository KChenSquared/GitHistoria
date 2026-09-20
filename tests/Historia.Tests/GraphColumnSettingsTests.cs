namespace Historia.Tests;

/// <summary>§V2-11.1 / §V2-15.1: validation of a hand-edited or migrated-forward column list.</summary>
[Trait("tier", "unit")]
public class GraphColumnSettingsTests
{
    private static List<GraphColumn> Defaults() => GraphColumnDefaults.All.Select(c => c with { }).ToList();

    [Fact]
    public void NullOrEmptyResolvesToDefaults()
    {
        Assert.Equal(GraphColumnDefaults.All, GraphColumnDefaults.Normalize(null));
        Assert.Equal(GraphColumnDefaults.All, GraphColumnDefaults.Normalize([]));
    }

    [Fact]
    public void DuplicateKindKeepsFirstOccurrence()
    {
        var input = Defaults();
        input.Insert(0, input[2] with { Width = 999 }); // duplicate Message at the front

        var r = GraphColumnDefaults.Normalize(input);
        Assert.Single(r, c => c.Kind == GraphColumnKind.Message);
        // First occurrence (the width-999 one, clamped to MaxWidth) is the one kept.
        Assert.Equal(999, r.First(c => c.Kind == GraphColumnKind.Message).Width, 3);
        Assert.Equal(0, r.ToList().FindIndex(c => c.Kind == GraphColumnKind.Message));
    }

    [Fact]
    public void MissingKindsAreAppendedHidden()
    {
        var input = new List<GraphColumn>
        {
            new() { Kind = GraphColumnKind.Graph, Width = 200, Visible = true },
            new() { Kind = GraphColumnKind.Message, Width = 300, Visible = true, IsFlex = true },
        };

        var r = GraphColumnDefaults.Normalize(input);
        Assert.Equal(6, r.Count);
        foreach (var kind in Enum.GetValues<GraphColumnKind>())
            Assert.Contains(r, c => c.Kind == kind);
        Assert.False(r.First(c => c.Kind == GraphColumnKind.Sha).Visible);
        Assert.False(r.First(c => c.Kind == GraphColumnKind.Refs).Visible);
    }

    [Fact]
    public void WidthsAreClamped()
    {
        var input = Defaults();
        input[2] = input[2] with { Width = 50 };     // below Message min (120)
        input[5] = input[5] with { Width = 99999 };  // above max

        var r = GraphColumnDefaults.Normalize(input);
        Assert.Equal(GraphColumnDefaults.MinWidthFor(GraphColumnKind.Message),
            r.First(c => c.Kind == GraphColumnKind.Message).Width, 3);
        Assert.Equal(GraphColumnDefaults.MaxWidth,
            r.First(c => c.Kind == GraphColumnKind.Sha).Width, 3);
    }

    [Fact]
    public void Graph_keeps_a_width_above_2000_but_other_kinds_still_clamp()
    {
        var input = Defaults();
        input[1] = input[1] with { Width = 5922 }; // Graph, stretched to ~328 lanes
        input[4] = input[4] with { Width = 5000 }; // Date

        var r = GraphColumnDefaults.Normalize(input);
        Assert.Equal(5922, r.First(c => c.Kind == GraphColumnKind.Graph).Width, 3);
        Assert.Equal(GraphColumnDefaults.MaxWidth, r.First(c => c.Kind == GraphColumnKind.Date).Width, 3);
    }

    [Fact]
    public void AllHiddenResetsToDefaults()
    {
        var input = Defaults().Select(c => c with { Visible = false }).ToList();
        Assert.Equal(GraphColumnDefaults.All, GraphColumnDefaults.Normalize(input));
    }

    [Fact]
    public void ZeroOrMultipleFlexResetsToDefaults()
    {
        var noFlex = Defaults().Select(c => c with { IsFlex = false }).ToList();
        Assert.Equal(GraphColumnDefaults.All, GraphColumnDefaults.Normalize(noFlex));

        var twoFlex = Defaults();
        twoFlex[0] = twoFlex[0] with { IsFlex = true }; // Refs + Message both flex
        Assert.Equal(GraphColumnDefaults.All, GraphColumnDefaults.Normalize(twoFlex));
    }

    // ── §V3-8.1 / §V3-9.4 ──

    [Fact]
    public void DefaultsHideAuthorDateAndSha()
    {
        Assert.False(GraphColumnDefaults.All.Single(c => c.Kind == GraphColumnKind.Author).Visible);
        Assert.False(GraphColumnDefaults.All.Single(c => c.Kind == GraphColumnKind.Date).Visible);
        Assert.False(GraphColumnDefaults.All.Single(c => c.Kind == GraphColumnKind.Sha).Visible);
        Assert.True(GraphColumnDefaults.All.Single(c => c.Kind == GraphColumnKind.Refs).Visible);
        Assert.Equal(GraphColumnDefaults.RefsStaticFloor,
            GraphColumnDefaults.All.Single(c => c.Kind == GraphColumnKind.Refs).Width, 3);
    }

    [Fact]
    public void MinWidthForIsOneFiftyExceptRefsAndSha()
    {
        Assert.Equal(GraphColumnDefaults.RefsStaticFloor, GraphColumnDefaults.MinWidthFor(GraphColumnKind.Refs), 3);
        Assert.Equal(80, GraphColumnDefaults.MinWidthFor(GraphColumnKind.Sha), 3);
        // The Date cell carries a full "MM/DD/YYYY - HH:MM AM/PM (ago)" string.
        Assert.Equal(200, GraphColumnDefaults.MinWidthFor(GraphColumnKind.Date), 3);
        foreach (var k in new[] { GraphColumnKind.Graph, GraphColumnKind.Message, GraphColumnKind.Author })
            Assert.Equal(150, GraphColumnDefaults.MinWidthFor(k), 3);
    }

    [Fact]
    public void ResetLayout_preserves_visibility_but_resets_order_and_width()
    {
        var scrambled = new List<GraphColumn>
        {
            new() { Kind = GraphColumnKind.Sha,     Width = 400, Visible = true },
            new() { Kind = GraphColumnKind.Message, Width = 999, Visible = true, IsFlex = true },
            new() { Kind = GraphColumnKind.Graph,   Width = 700, Visible = true },
            new() { Kind = GraphColumnKind.Refs,    Width = 500, Visible = true },
            new() { Kind = GraphColumnKind.Author,  Width = 500, Visible = true },
            new() { Kind = GraphColumnKind.Date,    Width = 500, Visible = false },
        };
        var reset = GraphColumnDefaults.ResetLayout(scrambled);

        // Canonical order and default widths.
        Assert.Equal(
            GraphColumnDefaults.All.Select(c => c.Kind),
            reset.Select(c => c.Kind));
        foreach (var def in GraphColumnDefaults.All)
            Assert.Equal(def.Width, reset.Single(c => c.Kind == def.Kind).Width, 3);

        // Visibility survives (Sha was toggled on, Date off).
        Assert.True(reset.Single(c => c.Kind == GraphColumnKind.Sha).Visible);
        Assert.False(reset.Single(c => c.Kind == GraphColumnKind.Date).Visible);
    }

    [Fact]
    public void Normalize_then_ResetLayout_yields_canonical_order()
    {
        var input = Defaults();
        input.Reverse();

        var reset = GraphColumnDefaults.ResetLayout(GraphColumnDefaults.Normalize(input));
        Assert.Equal(GraphColumnDefaults.All.Select(c => c.Kind), reset.Select(c => c.Kind));
    }
}
