namespace Historia.Tests;

public class EdgeRouterTests
{
    private const double Lane = 15;
    private const double Row = 24;

    [Fact]
    public void StraightIsTwoWaypointLine()
    {
        var r = EdgeRouter.Route(EdgeType.Straight, 10, 0, 10, 100, Lane, Row);
        Assert.True(r.IsDegenerate);
        Assert.Equal(2, r.Waypoints.Count);
        Assert.Equal(new Point(10, 0), r.Waypoints[0]);
        Assert.Equal(new Point(10, 100), r.Waypoints[1]);
    }

    [Fact]
    public void EqualXDegeneratesToLineRegardlessOfType()
    {
        var r = EdgeRouter.Route(EdgeType.MergeIn, 30, 0, 30, 72, Lane, Row);
        Assert.True(r.IsDegenerate);
    }

    [Fact]
    public void OffWindowWithoutLaneChangeHasNoCorner()
    {
        var r = EdgeRouter.Route(EdgeType.OffWindow, 45, 0, 45, -50, Lane, Row);
        Assert.True(r.IsDegenerate);
    }

    [Fact]
    public void OffWindowWithLaneChangeRoutesAsAnElbow()
    {
        // §V3-3.4: a pending second-parent stub whose child and parent lanes differ must not
        // short-circuit to a straight line.
        var r = EdgeRouter.Route(EdgeType.OffWindow, x1: 20, y1: 10, x2: 80, y2: 200, Lane, Row);
        Assert.False(r.IsDegenerate);
        Assert.Equal(3, r.Waypoints.Count);
        Assert.Equal(80, r.Waypoints[1].X, 3);
        Assert.Equal(10, r.Waypoints[1].Y, 3); // corner at (x2, y1)
    }

    [Fact]
    public void MergeInTurnsAtTheMergeCommitRow()
    {
        // From the merge node at (x1,y1) sideways to a parent in another lane.
        var r = EdgeRouter.Route(EdgeType.MergeIn, x1: 20, y1: 10, x2: 80, y2: 130, Lane, Row);
        Assert.False(r.IsDegenerate);
        Assert.Equal(3, r.Waypoints.Count);
        // The corner (interior waypoint) sits at the merge row's y — the horizontal jog is at y1.
        Assert.Equal(10, r.Waypoints[1].Y, 3);
        Assert.Equal(80, r.Waypoints[1].X, 3);
    }

    [Fact]
    public void BranchOutTurnsAtTheJoinedCommitRow()
    {
        var r = EdgeRouter.Route(EdgeType.BranchOut, x1: 80, y1: 10, x2: 20, y2: 130, Lane, Row);
        Assert.Equal(3, r.Waypoints.Count);
        // Falls down x1, then turns horizontally at y2 — the corner is at (x1, y2).
        Assert.Equal(80, r.Waypoints[1].X, 3);
        Assert.Equal(130, r.Waypoints[1].Y, 3);
    }

    [Fact]
    public void CornerRadiusClampsForAdjacentRows()
    {
        // Rows one apart: |y2 - y1| = Row, so the radius must not exceed Row/2.
        var r = EdgeRouter.Route(EdgeType.MergeIn, 0, 0, 60, Row, Lane, Row, cornerRadius: 6);
        Assert.True(r.CornerRadius <= Row / 2 + 1e-9);
        Assert.True(r.CornerRadius <= 6 + 1e-9);
    }

    [Fact]
    public void CornerRadiusClampsForAdjacentLanes()
    {
        // Lanes one apart: |x2 - x1| = Lane, radius must not exceed Lane/2.
        var r = EdgeRouter.Route(EdgeType.BranchOut, 0, 0, Lane, 120, Lane, Row, cornerRadius: 6);
        Assert.True(r.CornerRadius <= Lane / 2 + 1e-9);
    }

    [Fact]
    public void CornerFillsTheWholeCellByDefault()
    {
        // §V3-B: with the default corner radius, a 1-lane / 1-row bend turns across the whole
        // lane×row square — radius == min(Lane, Row), not min(Lane, Row) / 2.
        var r = EdgeRouter.Route(EdgeType.BranchOut, Lane, 0, 0, Row, Lane, Row);
        Assert.Equal(Math.Min(Lane, Row), r.CornerRadius, 9);
        Assert.True(r.CornerRadius > Math.Min(Lane, Row) / 2 + 1e-9);
    }

    [Fact]
    public void RouteEndpointsAreExact()
    {
        var r = EdgeRouter.Route(EdgeType.MergeIn, 12, 4, 92, 220, Lane, Row);
        Assert.Equal(new Point(12, 4), r.Waypoints[0]);
        Assert.Equal(new Point(92, 220), r.Waypoints[^1]);
    }
}
