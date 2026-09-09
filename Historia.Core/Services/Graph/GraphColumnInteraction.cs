namespace Historia.Services.Graph;

/// <summary>
/// Pure geometry for the commit-graph column header's pointer interactions (§V3-8.4): which
/// boundary a resize drag grabs, where a reorder drag would insert, which column sits under
/// the pointer, and the reorder itself. Lifted out of <c>CommitGraphControl</c> so the
/// arithmetic is unit-testable (debloat audit T3.3). The control keeps the drag state,
/// pointer capture, persistence and <c>ContextMenu</c> building. All <c>x</c> values are
/// viewport pixels; <paramref name="hScrollOffset"/> is subtracted here so callers pass the
/// raw pointer x.
/// </summary>
public static class GraphColumnInteraction
{
    /// <summary>Index of the resolved column boundary within <paramref name="grab"/> px of
    /// <paramref name="x"/>, or -1. The final (rightmost) boundary is excluded — there is no
    /// column past it to resize against.</summary>
    public static int BoundaryAt(IReadOnlyList<ResolvedColumn> resolved, double x, double hScrollOffset, double grab)
    {
        for (var i = 0; i < resolved.Count - 1; i++)
        {
            var edge = resolved[i].Left + resolved[i].Width - hScrollOffset;
            if (Math.Abs(x - edge) <= grab)
                return i;
        }
        return -1;
    }

    /// <summary>The insert-caret index for a reorder drag at <paramref name="x"/>: the first
    /// column whose mid-point is right of <paramref name="x"/>, else <c>resolved.Count</c>
    /// (drop at the end).</summary>
    public static int CaretIndexAt(IReadOnlyList<ResolvedColumn> resolved, double x, double hScrollOffset)
    {
        for (var i = 0; i < resolved.Count; i++)
        {
            var mid = resolved[i].Left + resolved[i].Width / 2 - hScrollOffset;
            if (x < mid)
                return i;
        }
        return resolved.Count;
    }

    /// <summary>The kind of the resolved column under <paramref name="x"/>, or <c>null</c>
    /// when <paramref name="x"/> is outside every column.</summary>
    public static GraphColumnKind? ColumnKindAt(IReadOnlyList<ResolvedColumn> resolved, double x, double hScrollOffset)
    {
        foreach (var c in resolved)
        {
            var left = c.Left - hScrollOffset;
            if (x >= left && x < left + c.Width)
                return c.Kind;
        }
        return null;
    }

    /// <summary>Move <paramref name="moved"/> to the <paramref name="caret"/> slot among the
    /// visible columns, preserving the hidden tail. Returns a fresh full column list, or the
    /// input reference unchanged when <paramref name="moved"/> is not currently visible.</summary>
    public static IReadOnlyList<GraphColumn> Reorder(IReadOnlyList<GraphColumn> columns, GraphColumnKind moved, int caret)
    {
        var visible = columns.Where(c => c.Visible).ToList();
        var hidden = columns.Where(c => !c.Visible).ToList();
        var from = visible.FindIndex(c => c.Kind == moved);
        if (from < 0)
            return columns;

        var col = visible[from];
        visible.RemoveAt(from);
        var to = Math.Clamp(caret > from ? caret - 1 : caret, 0, visible.Count);
        visible.Insert(to, col);

        return [.. visible, .. hidden];
    }
}
