namespace Historia.Services.UI;

/// <summary>
/// Pure geometry for the app-band tab-strip drag reorder. Lifted out of
/// <c>AppBandControl</c> so the drag threshold, the cursor-to-slot mapping and the edge-scroll
/// trigger are unit-testable. The control keeps the pointer capture, the
/// drag ghost, the edge timer and the child-collection surgery.
/// </summary>
public static class AppBandDragReorder
{
    /// <summary>True once the pointer has travelled at least <paramref name="threshold"/> px
    /// along the strip from where it was pressed — the point a click becomes a drag.</summary>
    public static bool PassesThreshold(double pressX, double currentX, double threshold)
        => Math.Abs(currentX - pressX) >= threshold;

    /// <summary>The slot the dragged tab should occupy for a cursor at <paramref name="panelX"/>
    /// (tab-panel space): walk the laid-out tab widths and stop at the first whose mid-point is
    /// right of the cursor. A not-yet-measured tab (width ≤ 0) counts as
    /// <paramref name="fallbackWidth"/>. Clamped to a valid index.</summary>
    public static int TargetIndex(double panelX, IReadOnlyList<double> tabWidths, double fallbackWidth)
    {
        var target = 0;
        double acc = 0;
        for (var i = 0; i < tabWidths.Count; i++)
        {
            var w = tabWidths[i] <= 0 ? fallbackWidth : tabWidths[i];
            if (panelX < acc + w / 2)
                break;
            acc += w;
            target = i + 1;
        }
        return Math.Clamp(target, 0, tabWidths.Count - 1);
    }

    /// <summary>Edge-scroll direction for a cursor at <paramref name="viewportX"/> within a
    /// strip <paramref name="viewportWidth"/> wide: -1 in the left <paramref name="zone"/>,
    /// +1 in the right zone, 0 in between.</summary>
    public static int EdgeDirection(double viewportX, double viewportWidth, double zone)
        => viewportX < zone ? -1
         : viewportX > viewportWidth - zone ? 1
         : 0;

    /// <summary>clamps a linked child tab's live drag target into its own group's legal
    /// sibling range — <c>[groupStart + 1, groupStart + groupCount - 1]</c> — so a linked tab's
    /// preview can never cross into another group or ahead of its own owner. Returns
    /// <paramref name="groupStart"/> itself (a "no legal move" sentinel) when the group has one
    /// member or fewer (nothing to reorder among).</summary>
    public static int ClampChildTarget(int rawTarget, int groupStart, int groupCount)
        => groupCount <= 1 ? groupStart : Math.Clamp(rawTarget, groupStart + 1, groupStart + groupCount - 1);

    /// <summary>
    /// Returns the flat index at which the moving group's block should be inserted
    /// after it has been removed: the sum of the sizes of the first
    /// <paramref name="targetGroup"/> groups, skipping <paramref name="currentGroup"/>.
    /// <paramref name="targetGroup"/> is the group slot the block should occupy afterwards,
    /// which is what <see cref="TargetIndex"/> returns.
    /// </summary>
    public static int BlockInsertIndex(IReadOnlyList<int> groupSizes, int currentGroup, int targetGroup)
    {
        var insertAt = 0;
        var count = 0;
        for (var i = 0; i < groupSizes.Count && count < targetGroup; i++)
        {
            if (i == currentGroup)
                continue;
            insertAt += groupSizes[i];
            count++;
        }
        return insertAt;
    }
}
