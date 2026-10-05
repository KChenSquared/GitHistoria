namespace HistoriaApp.Tests;

/// <summary>Elbows on one row overlap along the node-height horizontal, so the Graph column
/// paints them widest span first: each stretch then shows the colour of the nearest branch
/// still running past it. Elbows whose vertical leg is in the hovered lane paint last so their
/// thicker pen stays whole.</summary>
public sealed class GraphElbowPaintOrderTests
{
    private static RowElbow E(int from, int to, int color) =>
        E(from, to, color, ElbowKind.EnterTop);

    private static RowElbow E(int from, int to, int color, ElbowKind kind) => new()
    {
        FromLane = from, ToLane = to, ColorIndex = color, Kind = kind,
    };

    private static int[] Order(RowElbow[] source, int hoverLane)
    {
        var dest = new RowElbow[source.Length];
        CommitGraphControl.OrderElbowsForPaint(source, dest, hoverLane);
        return dest.Select(e => e.ColorIndex).ToArray();
    }

    [Fact]
    public void WidestSpanPaintsFirst_NearestLast()
    {
        // Node in lane 1; branches fork out to lanes 3, 7, 5 and to lane 0 on the left.
        var source = new[] { E(3, 1, 30), E(7, 1, 70), E(5, 1, 50), E(0, 1, 0) };
        Assert.Equal(new[] { 70, 50, 30, 0 }, Order(source, hoverLane: -1));
    }

    [Fact]
    public void EqualSpansKeepLayoutOrder()
    {
        var source = new[] { E(1, 3, 1), E(5, 3, 2), E(3, 4, 3) };
        Assert.Equal(new[] { 1, 2, 3 }, Order(source, hoverLane: -1));
    }

    [Fact]
    public void HoverEmphasisedElbowsPaintLast()
    {
        var source = new[] { E(2, 1, 20), E(6, 1, 60), E(4, 1, 40) };
        Assert.Equal(new[] { 40, 20, 60 }, Order(source, hoverLane: 6));
    }

    [Fact]
    public void EnterTop_HoverOnVerticalLane_IsEmphasised()
    {
        var elbow = E(6, 1, 0, ElbowKind.EnterTop);
        Assert.True(CommitGraphControl.IsElbowEmphasised(elbow, hoverLane: 6));
    }

    [Fact]
    public void EnterTop_HoverOnHorizontalEnd_IsPlain()
    {
        var elbow = E(6, 1, 0, ElbowKind.EnterTop);
        Assert.False(CommitGraphControl.IsElbowEmphasised(elbow, hoverLane: 1));
    }

    [Fact]
    public void LeaveBottom_HoverOnVerticalLane_IsEmphasised()
    {
        var elbow = E(1, 4, 0, ElbowKind.LeaveBottom);
        Assert.True(CommitGraphControl.IsElbowEmphasised(elbow, hoverLane: 4));
    }

    [Fact]
    public void LeaveBottom_HoverOnHorizontalEnd_IsPlain()
    {
        var elbow = E(1, 4, 0, ElbowKind.LeaveBottom);
        Assert.False(CommitGraphControl.IsElbowEmphasised(elbow, hoverLane: 1));
    }

    [Fact]
    public void NoHover_NothingEmphasised()
    {
        Assert.False(CommitGraphControl.IsElbowEmphasised(E(6, 1, 0, ElbowKind.EnterTop), hoverLane: -1));
        Assert.False(CommitGraphControl.IsElbowEmphasised(E(1, 4, 0, ElbowKind.LeaveBottom), hoverLane: -1));
    }

    [Fact]
    public void HoveredNodeElbowsKeepSpanOrder()
    {
        var source = new[] { E(3, 1, 30), E(7, 1, 70), E(5, 1, 50), E(0, 1, 0) };
        Assert.Equal(Order(source, hoverLane: -1), Order(source, hoverLane: 1));
    }

    [Fact]
    public void LeaveBottomOnHoveredVerticalPaintsLast()
    {
        var source = new[]
        {
            E(1, 4, 40, ElbowKind.LeaveBottom),
            E(6, 1, 60, ElbowKind.EnterTop),
            E(2, 1, 20, ElbowKind.EnterTop),
        };
        Assert.Equal(new[] { 60, 20, 40 }, Order(source, hoverLane: 4));
    }

    private static int[] Order(RowElbow[] source, int focusLane, int hoverLane)
    {
        var dest = new RowElbow[source.Length];
        CommitGraphControl.OrderElbowsForPaint(source, dest, focusLane, hoverLane);
        return dest.Select(e => e.ColorIndex).ToArray();
    }

    [Fact]
    public void FocusLane_VerticalLane_IsEmphasised()
    {
        var elbow = E(6, 1, 0, ElbowKind.EnterTop);
        Assert.True(CommitGraphControl.IsElbowEmphasised(elbow, focusLane: 6, hoverLane: -1));
    }

    [Fact]
    public void FocusLane_HorizontalEnd_IsPlain()
    {
        var elbow = E(6, 1, 0, ElbowKind.EnterTop);
        Assert.False(CommitGraphControl.IsElbowEmphasised(elbow, focusLane: 1, hoverLane: -1));
    }

    [Fact]
    public void FocusAndHover_BothEmphasised_HoverPaintsLast()
    {
        var source = new[]
        {
            E(2, 1, 20, ElbowKind.EnterTop), // focus lane (2)
            E(6, 1, 60, ElbowKind.EnterTop), // hover lane (6)
            E(3, 1, 30, ElbowKind.EnterTop), // plain lane (3)
        };
        // Plain (30) paints first, focus (20) paints next, hover (60) paints last
        Assert.Equal(new[] { 30, 20, 60 }, Order(source, focusLane: 2, hoverLane: 6));
    }

    [Fact]
    public void SwitchingFocusLane_PreviousUnbolds()
    {
        var elbowOnLane2 = E(2, 1, 0, ElbowKind.EnterTop);
        // While lane 2 is focused (selected):
        Assert.True(CommitGraphControl.IsElbowEmphasised(elbowOnLane2, focusLane: 2, hoverLane: -1));
        // When selection moves to lane 4, lane 2 becomes unbold:
        Assert.False(CommitGraphControl.IsElbowEmphasised(elbowOnLane2, focusLane: 4, hoverLane: -1));
    }
}
