namespace Historia.Services.UI;

/// <summary>
/// Pure geometry for the app-band tab-strip drag reorder (§V2-9.3). Lifted out of
/// <c>AppBandControl</c> so the drag threshold, the cursor-to-slot mapping and the edge-scroll
/// trigger are unit-testable (debloat audit T3.6). The control keeps the pointer capture, the
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
}
