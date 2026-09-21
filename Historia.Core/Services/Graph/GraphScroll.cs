namespace Historia.Services.Graph;

/// <summary>
/// Pure scroll offset arithmetic for the commit graph (§V3-8.4).
/// </summary>
public static class GraphScroll
{
    /// <summary>
    /// Computes the vertical scroll offset to center <paramref name="row"/> within the viewport.
    /// Returns <c>null</c> when content fits within the viewport (<paramref name="contentHeight"/> &lt;= <paramref name="viewportHeight"/>),
    /// guaranteeing that no scroll bar moves and no blank space is invented when the graph has no scroll bar (R12, R13).
    /// Otherwise clamps the ideal center offset into [0, max], which monotonically lands on whichever end brings the row
    /// closest to center without introducing blank padding (R13), and lands at top (0) in case of an exact tie (R14).
    /// </summary>
    public static double? CenterOffset(int row, double rowHeight, double viewportHeight, double contentHeight)
    {
        var max = contentHeight - viewportHeight;
        if (max <= 0)
            return null;

        return Math.Clamp(row * rowHeight + rowHeight / 2 - viewportHeight / 2, 0, max);
    }
}
