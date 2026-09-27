namespace HistoriaApp.Tests;

/// <summary>Elbows on one row overlap along the node-height horizontal, so the Graph column
/// paints them widest span first: each stretch then shows the colour of the nearest branch
/// still running past it. Hover-emphasised elbows paint last so their thicker pen stays whole.</summary>
public sealed class GraphElbowPaintOrderTests
{
    private static RowElbow E(int from, int to, int color) => new()
    {
        FromLane = from, ToLane = to, ColorIndex = color, Kind = ElbowKind.EnterTop,
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
}
