
namespace Historia.Services.Graph;

/// <summary>
/// The polyline for one graph edge: a run of way-points joined by straight segments, with
/// rounded 90° corners of radius <see cref="CornerRadius"/> at every interior way-point
/// (§V2-7). Way-point 0 is the start, the last is the end; interior points are the sharp
/// corners the renderer rounds. §V3-B correction: the corner may fill a whole lane×row cell
/// (radius up to the full segment length), so a bend "takes up a whole square" instead of
/// clipping a 6&#160;DIU nub.
/// </summary>
public readonly record struct EdgeRoute(IReadOnlyList<PointD> Waypoints, double CornerRadius)
{
    public bool IsDegenerate => Waypoints.Count <= 2;
}

/// <summary>
/// Pure orthogonal edge routing (§V2-7.2). No drawing types beyond <see cref="PointD"/>,
/// so it is unit-testable like <c>DagLayoutEngine</c> / <c>GraphColumnLayout</c>.
/// </summary>
public static class EdgeRouter
{
    /// <summary>
    /// §V3-B: default corner radius is now "as large as the cell allows" — the per-call clamp
    /// below caps it at the lane width, the row height, and the segment lengths, so a 1×1 bend
    /// becomes a full quarter turn across the square. Callers that want a tighter nub still pass
    /// an explicit <c>cornerRadius</c>.
    /// </summary>
    public const double DefaultCornerRadius = double.PositiveInfinity;

    /// <param name="x1">Start x (the row that owns the lane change for MergeIn).</param>
    /// <param name="y1">Start y (centre of <c>FromRow</c>).</param>
    /// <param name="x2">End x.</param>
    /// <param name="y2">End y (centre of <c>ToRow</c>).</param>
    public static EdgeRoute Route(
        EdgeType type, double x1, double y1, double x2, double y2,
        double laneWidth, double rowHeight, double cornerRadius = DefaultCornerRadius)
    {
        // Straight always renders as a single vertical; so does any edge whose endpoints share
        // an x. An OffWindow stub with a lane change now routes as an elbow rather than
        // short-circuiting (§V3-3.4).
        if (type is EdgeType.Straight || Math.Abs(x1 - x2) < 0.5)
            return new EdgeRoute([new(x1, y1), new(x2, y2)], 0);

        PointD corner = type switch
        {
            // BranchOut: fall down x1, then turn horizontally into the joined commit at y2.
            EdgeType.BranchOut => new(x1, y2),
            // MergeIn, and OffWindow with a lane change: leave the child node sideways —
            // horizontal at y1, then descend the parent's lane at x2.
            _ => new(x2, y1),
        };

        // §V3-B: a bend may consume the whole lane×row cell it turns in — the cap is the full
        // lane width / row height (was half), and the full remaining segment length (was half).
        var r = Math.Min(cornerRadius, Math.Min(laneWidth, rowHeight));
        r = Math.Min(r, Math.Min(Math.Abs(x2 - x1), Math.Abs(y2 - y1)));
        r = Math.Max(0, r);

        return new EdgeRoute([new(x1, y1), corner, new(x2, y2)], r);
    }
}
