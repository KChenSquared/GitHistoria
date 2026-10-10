namespace Historia.Controls;

/// <summary>How a conflict pane row is washed: green for what is kept, red for what is dropped.</summary>
public enum ConflictRowTint { None, Add, Del, Base, Marker }

/// <summary>
/// Maps conflict pane and Result rows to their wash and gutter tints. Kept rows (and both sides'
/// lines of an unresolved block) take the diff addition colours, dropped rows the deletion colours,
/// and a side's deletion row the deletion colours while it is checked; marker and base lines stay neutral.
/// </summary>
public static class ConflictRowTints
{
    /// <summary>The tint of a side-pane row.</summary>
    public static ConflictRowTint ForPane(ConflictPaneLineType type) => type switch
    {
        ConflictPaneLineType.Ours or ConflictPaneLineType.Theirs => ConflictRowTint.Add,
        ConflictPaneLineType.Deletion => ConflictRowTint.Del,
        ConflictPaneLineType.Marker => ConflictRowTint.Marker,
        _ => ConflictRowTint.None,
    };

    /// <summary>
    /// The tint of a Result line: a managed row by its visual, otherwise the line's role inside an
    /// unresolved marker block.
    /// </summary>
    public static ConflictRowTint ForResult(int lineIdx, ConflictRowVisual visual, ConflictParse? parse)
    {
        if (visual != ConflictRowVisual.None)
        {
            return ForManagedRow(visual);
        }

        var region = FindRegion(parse, lineIdx);
        return region == null ? ConflictRowTint.None : ForRegionLine(lineIdx, region);
    }

    /// <summary>The tint of a managed row.</summary>
    internal static ConflictRowTint ForManagedRow(ConflictRowVisual visual)
    {
        if ((visual & ConflictRowVisual.Deletion) != 0)
        {
            return (visual & ConflictRowVisual.Excluded) != 0 ? ConflictRowTint.None : ConflictRowTint.Del;
        }

        if ((visual & ConflictRowVisual.Excluded) != 0)
        {
            return ConflictRowTint.Del;
        }

        return (visual & (ConflictRowVisual.Ours | ConflictRowVisual.Theirs)) != 0 ? ConflictRowTint.Add : ConflictRowTint.None;
    }

    /// <summary>The tint of <paramref name="lineIdx"/>, a line of the unresolved marker block <paramref name="region"/>.</summary>
    internal static ConflictRowTint ForRegionLine(int lineIdx, ConflictRegion region)
    {
        if (lineIdx == region.StartLine || lineIdx == region.SeparatorLine || lineIdx == region.EndLine
            || (region.HasBase && lineIdx == region.BaseMarkerLine))
        {
            return ConflictRowTint.Marker;
        }

        if (lineIdx >= region.OursStartLine && lineIdx <= region.OursEndLine)
        {
            return ConflictRowTint.Add;
        }

        if (region.HasBase && lineIdx >= region.BaseStartLine && lineIdx <= region.BaseEndLine)
        {
            return ConflictRowTint.Base;
        }

        return lineIdx >= region.TheirsStartLine && lineIdx <= region.TheirsEndLine ? ConflictRowTint.Add : ConflictRowTint.None;
    }

    /// <summary>The marker block containing <paramref name="lineIdx"/>, or null.</summary>
    internal static ConflictRegion? FindRegion(ConflictParse? parse, int lineIdx)
    {
        if (parse == null)
        {
            return null;
        }

        var regions = parse.Regions;
        for (int i = 0; i < regions.Count; i++)
        {
            if (lineIdx >= regions[i].StartLine && lineIdx <= regions[i].EndLine)
            {
                return regions[i];
            }
        }

        return null;
    }

    /// <summary>The row wash for <paramref name="tint"/>, or null when the row is unwashed.</summary>
    public static IBrush? BackgroundBrush(ConflictRowTint tint) => tint switch
    {
        ConflictRowTint.Add => PaletteBrushes.Get("ColorDiffAddBg"),
        ConflictRowTint.Del => PaletteBrushes.Get("ColorDiffDelBg"),
        ConflictRowTint.Base => PaletteBrushes.Get("ColorConflictBaseBg"),
        ConflictRowTint.Marker => PaletteBrushes.Get("ColorConflictMarkerBg"),
        _ => null,
    };

    /// <summary>The line-number gutter tint for <paramref name="tint"/>, or null when the gutter is untinted.</summary>
    public static IBrush? GutterBrush(ConflictRowTint tint) => tint switch
    {
        ConflictRowTint.Add => PaletteBrushes.Get("ColorDiffAddGutter"),
        ConflictRowTint.Del => PaletteBrushes.Get("ColorDiffDelGutter"),
        _ => null,
    };
}
