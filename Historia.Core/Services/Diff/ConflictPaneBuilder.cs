namespace Historia.Services.Diff;

/// <summary>
/// Type of line within a conflict pane.
/// </summary>
public enum ConflictPaneLineType
{
    Common,
    Marker,
    Ours,
    Theirs,
    Padding,
    [Obsolete("Never produced")]
    Base
}

/// <summary>
/// Represents a single line in an aligned conflict pane.
/// </summary>
public readonly record struct ConflictPaneLine(
    ConflictPaneLineType Type,
    string Text,
    int? LineNumber,
    int DocumentLineIndex);

/// <summary>
/// Builds 1:1 aligned pane lines for conflict resolution editors (Ours, Theirs, Base, Result).
/// Ensures all panes have the exact same line count as the Result document for shared vertical scrolling.
/// </summary>
public static class ConflictPaneBuilder
{
    /// <summary>
    /// Counter incremented on every Build call, used to verify pane lines are not rebuilt per render.
    /// </summary>
    public static int BuildCallCount;

    /// <summary>
    /// Builds aligned pane lines for the specified side.
    /// </summary>
    public static IReadOnlyList<ConflictPaneLine> Build(ConflictParse parse, ConflictSide side)
        => Build(parse, side, null);

    /// <summary>
    /// Builds aligned pane lines for the specified side, with <paramref name="managed"/> regions
    /// overlaid: a managed line shows its row's original text on the side it came from and pads
    /// the other side (added lines pad both). Spans are sorted, never overlap marker regions, and
    /// are walked alongside the regions in one pass.
    /// </summary>
    public static IReadOnlyList<ConflictPaneLine> Build(ConflictParse parse, ConflictSide side, IReadOnlyList<ConflictManagedSpan>? managed)
    {
        Interlocked.Increment(ref BuildCallCount);
        var documentLines = parse.Lines;
        int lineCount = parse.LineCount;

        var result = new List<ConflictPaneLine>(lineCount);
        int regionIndex = 0;
        int spanIndex = 0;
        int sideLineNumber = 1;
        var sideOrigin = side == ConflictSide.Ours ? ConflictRowOrigin.Ours : ConflictRowOrigin.Theirs;
        var sideType = side == ConflictSide.Ours ? ConflictPaneLineType.Ours : ConflictPaneLineType.Theirs;

        for (int lineIdx = 0; lineIdx < lineCount; lineIdx++)
        {
            string lineText = lineIdx < documentLines.Count ? documentLines[lineIdx] : string.Empty;

            if (managed != null)
            {
                while (spanIndex < managed.Count && managed[spanIndex].EndLine < lineIdx)
                {
                    spanIndex++;
                }

                if (spanIndex < managed.Count && lineIdx >= managed[spanIndex].StartLine)
                {
                    var origin = managed[spanIndex].Lines[lineIdx - managed[spanIndex].StartLine];
                    result.Add(origin.Origin == sideOrigin
                        ? new ConflictPaneLine(sideType, origin.SourceText, sideLineNumber++, lineIdx)
                        : new ConflictPaneLine(ConflictPaneLineType.Padding, string.Empty, null, lineIdx));
                    continue;
                }
            }

            ConflictRegion? currentRegion = null;
            if (regionIndex < parse.Regions.Count)
            {
                var candidate = parse.Regions[regionIndex];
                if (lineIdx >= candidate.StartLine && lineIdx <= candidate.EndLine)
                {
                    currentRegion = candidate;
                }
            }

            if (currentRegion == null)
            {
                // Common line outside any well-formed conflict region
                result.Add(new ConflictPaneLine(
                    ConflictPaneLineType.Common,
                    lineText,
                    sideLineNumber++,
                    lineIdx));
                continue;
            }

            if (lineIdx == currentRegion.StartLine)
            {
                result.Add(new ConflictPaneLine(ConflictPaneLineType.Marker, string.Empty, null, lineIdx));
            }
            else if (lineIdx >= currentRegion.OursStartLine && lineIdx <= currentRegion.OursEndLine)
            {
                if (side == ConflictSide.Ours)
                {
                    result.Add(new ConflictPaneLine(ConflictPaneLineType.Ours, lineText, sideLineNumber++, lineIdx));
                }
                else
                {
                    result.Add(new ConflictPaneLine(ConflictPaneLineType.Padding, string.Empty, null, lineIdx));
                }
            }
            else if (currentRegion.HasBase && lineIdx == currentRegion.BaseMarkerLine)
            {
                result.Add(new ConflictPaneLine(ConflictPaneLineType.Marker, string.Empty, null, lineIdx));
            }
            else if (currentRegion.HasBase && lineIdx >= currentRegion.BaseStartLine && lineIdx <= currentRegion.BaseEndLine)
            {
                result.Add(new ConflictPaneLine(ConflictPaneLineType.Padding, string.Empty, null, lineIdx));
            }
            else if (lineIdx == currentRegion.SeparatorLine)
            {
                result.Add(new ConflictPaneLine(ConflictPaneLineType.Marker, string.Empty, null, lineIdx));
            }
            else if (lineIdx >= currentRegion.TheirsStartLine && lineIdx <= currentRegion.TheirsEndLine)
            {
                if (side == ConflictSide.Theirs)
                {
                    result.Add(new ConflictPaneLine(ConflictPaneLineType.Theirs, lineText, sideLineNumber++, lineIdx));
                }
                else
                {
                    result.Add(new ConflictPaneLine(ConflictPaneLineType.Padding, string.Empty, null, lineIdx));
                }
            }
            else if (lineIdx == currentRegion.EndLine)
            {
                result.Add(new ConflictPaneLine(ConflictPaneLineType.Marker, string.Empty, null, lineIdx));
                regionIndex++;
            }
        }

        return result;
    }
}
