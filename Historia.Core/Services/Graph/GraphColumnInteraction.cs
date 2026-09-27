namespace Historia.Services.Graph;

/// <summary>
/// Pure geometry for the commit-graph column header's pointer interactions: which
/// boundary a resize drag grabs, where a reorder drag would insert, which column sits under
/// the pointer, and the reorder itself. Lifted out of <c>CommitGraphControl</c> so the
/// arithmetic is unit-testable. The control keeps the drag state,
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

    private const double MinLabelRoom = 24;
    private const double DefaultIconGap = 4;

    /// <summary>Returns the right-aligned bounding rect for header icon <paramref name="slot"/>
    /// (0 = right-most, increasing slots sit further left) of <paramref name="kind"/>'s column,
    /// or <c>null</c> when the column is not visible or too narrow to fit every slot up to and
    /// including this one without colliding with its label.</summary>
    public static HeaderIconRect? HeaderIconRectFor(
        IReadOnlyList<ResolvedColumn> resolved,
        GraphColumnKind kind,
        double hScrollOffset,
        double headerHeight,
        double iconSize,
        double inset,
        int slot = 0,
        double gap = DefaultIconGap)
    {
        for (var i = 0; i < resolved.Count; i++)
        {
            var col = resolved[i];
            if (col.Kind == kind)
            {
                var occupiedWidth = (slot + 1) * iconSize + slot * gap;
                if (col.Width < occupiedWidth + inset + MinLabelRoom)
                    return null;

                var right = col.Left + col.Width - hScrollOffset;
                var x = right - inset - iconSize - slot * (iconSize + gap);
                var y = (headerHeight - iconSize) / 2;
                return new HeaderIconRect(x, y, iconSize, iconSize);
            }
        }
        return null;
    }

    /// <summary>The single-slot (Refs, Message) search list every pre-multi-slot caller used —
    /// the default for <see cref="HeaderIconAt"/> so those call sites keep compiling unchanged.</summary>
    private static readonly (GraphColumnKind Kind, int Slot)[] DefaultHeaderIconCandidates =
        [(GraphColumnKind.Refs, 0), (GraphColumnKind.Message, 0)];

    /// <summary>Returns the (kind, slot) pair of the header icon containing
    /// (<paramref name="x"/>, <paramref name="y"/>), restricted to the candidates in
    /// <paramref name="candidates"/> (default: the single-slot Refs/Message search every caller
    /// used before multi-slot columns existed), or <c>null</c>.</summary>
    public static (GraphColumnKind Kind, int Slot)? HeaderIconAt(
        IReadOnlyList<ResolvedColumn> resolved,
        double x,
        double y,
        double hScrollOffset,
        double headerHeight,
        double iconSize,
        double inset,
        IReadOnlyList<(GraphColumnKind Kind, int Slot)>? candidates = null,
        double gap = DefaultIconGap)
    {
        if (y < 0 || y > headerHeight)
            return null;

        foreach (var candidate in candidates ?? DefaultHeaderIconCandidates)
        {
            var rect = HeaderIconRectFor(resolved, candidate.Kind, hScrollOffset, headerHeight, iconSize, inset, candidate.Slot, gap);
            if (rect != null && rect.Value.Contains(x, y))
                return candidate;
        }

        return null;
    }
}

/// <summary>
/// A bounding rectangle for a column header icon in viewport pixels.
/// </summary>
public readonly record struct HeaderIconRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;

    public bool Contains(double px, double py) =>
        px >= X && px <= X + Width && py >= Y && py <= Y + Height;
}

