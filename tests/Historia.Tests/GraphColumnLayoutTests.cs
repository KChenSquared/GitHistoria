namespace Historia.Tests;

/// <summary>§V3-8.4: the grow/shrink sizing model.</summary>
public class GraphColumnLayoutTests
{
    private static List<GraphColumn> Defaults() => GraphColumnDefaults.All.Select(c => c with { }).ToList();

    private static GraphColumnLayout.Result Resolve(
        IReadOnlyList<GraphColumn> cols, double viewport, bool pinned = false,
        double lane = 22, int lanes = 4, double refsBase = GraphColumnDefaults.RefsStaticFloor,
        IReadOnlySet<GraphColumnKind>? manual = null)
        => GraphColumnLayout.Resolve(cols, viewport, lane, lanes, pinned, refsBase, manual);

    private static double W(GraphColumnLayout.Result r, GraphColumnKind k) =>
        r.Columns.Single(c => c.Kind == k).Width;

    [Fact]
    public void Grow_fills_Message_to_250_before_Graph_moves()
    {
        // §V6-G4: Base Refs 140 + Graph 150 + Message 150 = 440. A small slack goes entirely
        // to Message until it reaches MessageFirstStop (250); Graph stays at its base.
        var r = Resolve(Defaults(), viewport: 520, lanes: 4); // slack 80
        Assert.Equal(230, W(r, GraphColumnKind.Message), 1);
        Assert.Equal(GraphColumnDefaults.MinWidthFor(GraphColumnKind.Graph), W(r, GraphColumnKind.Graph), 1);
    }

    [Fact]
    public void Grow_then_fills_Graph_to_220()
    {
        // §V6-G4: enough slack for Message→250 (100) then Graph→220 (70), nothing left over.
        var r = Resolve(Defaults(), viewport: 610, lanes: 4); // slack 170
        Assert.Equal(GraphColumnLayout.MessageFirstStop, W(r, GraphColumnKind.Message), 1);
        Assert.Equal(GraphColumnLayout.GraphCoGrowTarget, W(r, GraphColumnKind.Graph), 1);
    }

    [Fact]
    public void Grow_past_both_targets_gives_the_rest_to_Message()
    {
        // §V6-G4: Message→250 (100) + Graph→220 (70) = 170 consumed; the remaining slack is
        // all Message's.
        var r = Resolve(Defaults(), viewport: 900, lanes: 4); // slack 460
        Assert.Equal(GraphColumnLayout.GraphCoGrowTarget, W(r, GraphColumnKind.Graph), 1);
        Assert.Equal(250 + (460 - 170), W(r, GraphColumnKind.Message), 1);
    }

    [Fact]
    public void PinnedGraphKeepsItsWidthAndDoesNotCoGrow()
    {
        // §V3-B: once pinned, a hand-set Graph width sticks even with plenty of slack — the
        // whole surplus goes to Message instead of dragging Graph back toward the ceiling.
        var cols = Defaults();
        cols[1] = cols[1] with { Width = GraphColumnDefaults.MinWidthFor(GraphColumnKind.Graph) }; // 150
        var r = Resolve(cols, viewport: 1200, pinned: true, lanes: 8);

        Assert.Equal(150, W(r, GraphColumnKind.Graph), 1);
        Assert.True(W(r, GraphColumnKind.Message) > GraphColumnLayout.CoGrowCeiling);
    }

    [Fact]
    public void UnpinnedGraphCoGrowsToItsTargetThenStops()
    {
        // §V6-G4: with ample slack Graph settles at GraphCoGrowTarget (220), not GraphAutoMax.
        var r = Resolve(Defaults(), viewport: 900, pinned: false, lanes: 4);
        Assert.Equal(GraphColumnLayout.GraphCoGrowTarget, W(r, GraphColumnKind.Graph), 1);
    }

    [Fact]
    public void BeyondBothTargetsOnlyMessageGrows()
    {
        var r = Resolve(Defaults(), viewport: 2000, lanes: 4);
        Assert.Equal(GraphColumnLayout.GraphCoGrowTarget, W(r, GraphColumnKind.Graph), 1);
        Assert.True(W(r, GraphColumnKind.Message) > 900);
    }

    [Fact]
    public void Under_the_sequence_Graph_is_hard_capped_at_220_even_with_many_lanes()
    {
        // §V6-G4: the lane-count auto-size would want 250+ here, but the staged sequence caps
        // Graph at GraphCoGrowTarget (220); the surplus is Message's.
        var r = Resolve(Defaults(), viewport: 900, lanes: 40, lane: 22);
        Assert.Equal(GraphColumnLayout.GraphCoGrowTarget, W(r, GraphColumnKind.Graph), 1);
        Assert.True(W(r, GraphColumnKind.Message) > GraphColumnLayout.MessageFirstStop);
    }

    [Fact]
    public void Under_the_sequence_a_deficit_leaves_Graph_and_Message_at_their_floor()
    {
        // §V6-G4: the sequence starts Graph AND Message at 150. Once the viewport can't even
        // fit the base row, both stay floored and the deficit comes off the trailing columns
        // (Date/Author/Sha) via the priority pass — never off Graph or Message.
        var cols = Defaults().Select(c => c with { Visible = true }).ToList();
        cols[4] = cols[4] with { Width = 320 }; // Date, generous
        var graphMin = GraphColumnDefaults.MinWidthFor(GraphColumnKind.Graph);
        var msgMin = GraphColumnDefaults.MinWidthFor(GraphColumnKind.Message);

        // base = Refs 140 + Graph 150 + Message 150 + Author 150 + Date 320 + Sha 80 = 990.
        var mild = Resolve(cols, viewport: 940, lanes: 40, lane: 22); // deficit 50
        Assert.Equal(graphMin, W(mild, GraphColumnKind.Graph), 1);
        Assert.Equal(msgMin, W(mild, GraphColumnKind.Message), 1);
        Assert.Equal(270, W(mild, GraphColumnKind.Date), 1);          // 320 - 50

        var hard = Resolve(cols, viewport: 820, lanes: 40, lane: 22); // deficit 170
        Assert.Equal(graphMin, W(hard, GraphColumnKind.Graph), 1);
        Assert.Equal(msgMin, W(hard, GraphColumnKind.Message), 1);
        Assert.Equal(GraphColumnDefaults.MinWidthFor(GraphColumnKind.Date), W(hard, GraphColumnKind.Date), 1);
    }

    [Fact]
    public void Custom_Graph_width_opts_out_of_the_sequence()
    {
        // §V6-G5: a pinned Graph width means the pre-v6 equal-rate co-grow runs instead — Graph
        // holds its hand-set width and Message takes the whole surplus.
        var cols = Defaults();
        cols[1] = cols[1] with { Width = 180 };
        var r = Resolve(cols, viewport: 1000, pinned: true, lanes: 8);

        Assert.Equal(180, W(r, GraphColumnKind.Graph), 1);
        Assert.True(W(r, GraphColumnKind.Message) > GraphColumnLayout.CoGrowCeiling);
    }

    [Fact]
    public void Custom_Message_width_opts_out_of_the_sequence()
    {
        // §V6-G5: once Message is manually sized the staged sequence is off. With no slack
        // (viewport == base sum) the hand-set Message width stands exactly and Graph keeps its
        // lane-count autosize — neither is pulled toward a v6 target.
        var cols = Defaults();
        cols[2] = cols[2] with { Width = 400 };
        var manual = ImmutableHashSet.Create(GraphColumnKind.Message);

        // Base = Refs 140 + Graph 250 (autosize) + Message 400 = 790.
        var r = Resolve(cols, viewport: 790, lanes: 40, lane: 22, manual: manual);
        Assert.Equal(400, W(r, GraphColumnKind.Message), 1);
        Assert.Equal(GraphColumnLayout.GraphAutoMax, W(r, GraphColumnKind.Graph), 1);
    }

    [Fact]
    public void ShrinkFollowsThePriorityOrderMessageGraphDateAuthorSha()
    {
        // All six visible, Author/Date/Sha given generous widths so there is something to take.
        var cols = Defaults().Select(c => c with { Visible = true }).ToList();
        cols[3] = cols[3] with { Width = 300 }; // Author
        cols[4] = cols[4] with { Width = 300 }; // Date
        cols[5] = cols[5] with { Width = 300 }; // Sha

        // Big lane count so Graph has room above its 150 floor.
        // Base ~ 140 + 320 + 150 + 300 + 300 + 300 = 1510; deficit ~ 410.
        var r = Resolve(cols, viewport: 1100, lanes: 40, lane: 22);

        // Message is already at its min, so width comes off Graph, then Date, then Author,
        // in that order; Sha and Refs are last and stay untouched.
        Assert.Equal(GraphColumnDefaults.MinWidthFor(GraphColumnKind.Graph), W(r, GraphColumnKind.Graph), 1);
        Assert.Equal(GraphColumnDefaults.MinWidthFor(GraphColumnKind.Date), W(r, GraphColumnKind.Date), 1);
        Assert.True(W(r, GraphColumnKind.Author) < 300);
        Assert.Equal(300, W(r, GraphColumnKind.Sha), 1);
        Assert.True(W(r, GraphColumnKind.Refs) >= GraphColumnDefaults.RefsStaticFloor - 0.5);
    }

    [Fact]
    public void DraggedMessageIsHeldWhileGraphShrinksToItsFloor()
    {
        // §V3-AC 21: drag Message wide, then narrow — Graph shrinks, Message keeps its width.
        var cols = Defaults();
        cols[2] = cols[2] with { Width = 420 };
        var manual = ImmutableHashSet.Create(GraphColumnKind.Message);

        var r = Resolve(cols, viewport: 700, lanes: 40, lane: 22, manual: manual);
        Assert.Equal(420, W(r, GraphColumnKind.Message), 1);
        Assert.Equal(GraphColumnDefaults.MinWidthFor(GraphColumnKind.Graph), W(r, GraphColumnKind.Graph), 1);
    }

    [Fact]
    public void ManuallySizedColumnsAreExcludedFromShrink()
    {
        var cols = Defaults();
        cols[2] = cols[2] with { Width = 500 }; // Message dragged wide
        var manual = ImmutableHashSet.Create(GraphColumnKind.Message);

        var r = Resolve(cols, viewport: 500, lanes: 4, manual: manual);
        Assert.Equal(500, W(r, GraphColumnKind.Message), 1); // held at its dragged width
        Assert.True(r.BandWidth > 500);                       // deficit becomes band overflow
    }

    [Fact]
    public void RefsFloorIsHonoured()
    {
        var r = Resolve(Defaults(), viewport: 300, lanes: 4, refsBase: 190);
        Assert.True(W(r, GraphColumnKind.Refs) >= 190 - 0.5);
    }

    [Fact]
    public void GraphAutoSizesToLaneCountOnTheLegacyPath()
    {
        // §V6-G4: the staged sequence hard-caps Graph at 220, so the lane-count auto-size only
        // shows on the legacy path — here, Graph manually sized so the sequence opts out
        // (this lever doesn't inflate the Message base the way a Message opt-out would).
        var manual = ImmutableHashSet.Create(GraphColumnKind.Graph);

        // Base = Refs 140 + Graph (11*22=242) + Message 150 = 532.
        var wide = Resolve(Defaults(), viewport: 532, lanes: 10, lane: 22, pinned: false, manual: manual);
        Assert.Equal((10 + 1) * 22, W(wide, GraphColumnKind.Graph), 1);

        // Lane-count base exceeds the auto max, so it clamps to GraphAutoMax.
        var capped = Resolve(Defaults(), viewport: 610, lanes: 40, lane: 22, pinned: false, manual: manual);
        Assert.Equal(GraphColumnLayout.GraphAutoMax, W(capped, GraphColumnKind.Graph), 1);
    }

    [Fact]
    public void ColumnsAreLaidOutLeftToRightWithoutGaps()
    {
        var r = Resolve(Defaults(), viewport: 1000);
        double expected = 0;
        foreach (var c in r.Columns)
        {
            Assert.Equal(expected, c.Left, 3);
            expected += c.Width;
        }
        Assert.Equal(expected, r.BandWidth, 3);
    }
}
