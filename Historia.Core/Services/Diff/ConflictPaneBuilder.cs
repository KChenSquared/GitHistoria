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
    Base,
    /// <summary>The one row standing for a side that removed every line of the conflict.</summary>
    Deletion
}

/// <summary>
/// Represents a single line in an aligned conflict pane. <paramref name="DocumentLineIndex"/> is
/// the Result line the row stands for: a side row's source line, or, on a marker or padding row,
/// the Result line at that row's position.
/// </summary>
public readonly record struct ConflictPaneLine(
    ConflictPaneLineType Type,
    string Text,
    int? LineNumber,
    int DocumentLineIndex);

/// <summary>
/// Builds the pane lines for conflict resolution editors (Ours, Theirs). Every pane has the same
/// row count as the Result document, so the panes scroll together with the Result. Inside a
/// conflict both sides start on the conflict's first content row and the shorter side pads below
/// (or shows a single deletion row if it has no lines at all).
/// </summary>
public static class ConflictPaneBuilder
{
    /// <summary>
    /// Counter incremented on every Build call, used to verify pane lines are not rebuilt per render.
    /// </summary>
    public static int BuildCallCount;

    /// <summary>
    /// Builds pane lines for the specified side.
    /// </summary>
    public static IReadOnlyList<ConflictPaneLine> Build(ConflictParse parse, ConflictSide side)
        => Build(parse, side, null);

    /// <summary>
    /// Builds pane lines for the specified side, with <paramref name="managed"/> regions overlaid:
    /// a managed region shows its rows' original text on the side they came from, from the region's
    /// first row down, and pads the rest (added lines are never shown; a side's deletion row shows
    /// as an unnumbered deletion row). Spans are sorted, never overlap marker regions, and are walked
    /// alongside the regions in one pass. An unresolved marker region with no lines on one side and lines
    /// on the other shows a single deletion row on that side in place of its first padding row.
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

        int lineIdx = 0;
        while (lineIdx < lineCount)
        {
            if (managed != null)
            {
                while (spanIndex < managed.Count && managed[spanIndex].EndLine < lineIdx)
                {
                    spanIndex++;
                }

                if (spanIndex < managed.Count && lineIdx >= managed[spanIndex].StartLine)
                {
                    var span = managed[spanIndex];
                    int offset = lineIdx - span.StartLine;
                    int rows = Math.Min(span.Lines.Count - offset, lineCount - lineIdx);
                    int shown = 0;
                    for (int i = 0; i < rows; i++)
                    {
                        var origin = span.Lines[offset + i];
                        if (origin.Origin == sideOrigin)
                        {
                            result.Add(origin.IsDeletion
                                ? new ConflictPaneLine(ConflictPaneLineType.Deletion, string.Empty, null, lineIdx + i)
                                : new ConflictPaneLine(sideType, origin.SourceText, sideLineNumber++, lineIdx + i));
                            shown++;
                        }
                    }

                    for (int i = shown; i < rows; i++)
                    {
                        result.Add(new ConflictPaneLine(ConflictPaneLineType.Padding, string.Empty, null, lineIdx + i));
                    }

                    lineIdx += rows;
                    spanIndex++;
                    continue;
                }
            }

            while (regionIndex < parse.Regions.Count && parse.Regions[regionIndex].EndLine < lineIdx)
            {
                regionIndex++;
            }

            if (regionIndex < parse.Regions.Count && parse.Regions[regionIndex].StartLine == lineIdx)
            {
                var region = parse.Regions[regionIndex++];
                int rows = region.EndLine - region.StartLine + 1;
                int sideStart = side == ConflictSide.Ours ? region.OursStartLine : region.TheirsStartLine;
                int sideCount = side == ConflictSide.Ours ? region.OursLineCount : region.TheirsLineCount;
                int otherCount = side == ConflictSide.Ours ? region.TheirsLineCount : region.OursLineCount;
                System.Diagnostics.Debug.Assert(sideCount <= rows - 2, "a conflict's side lines must fit between its markers");

                result.Add(new ConflictPaneLine(ConflictPaneLineType.Marker, string.Empty, null, lineIdx));
                if (sideCount == 0 && otherCount > 0)
                {
                    result.Add(new ConflictPaneLine(ConflictPaneLineType.Deletion, string.Empty, null, sideStart));
                    for (int row = 2; row < rows - 1; row++)
                    {
                        result.Add(new ConflictPaneLine(ConflictPaneLineType.Padding, string.Empty, null, lineIdx + row));
                    }
                }
                else
                {
                    for (int k = 0; k < sideCount; k++)
                    {
                        int source = sideStart + k;
                        string text = source < documentLines.Count ? documentLines[source] : string.Empty;
                        result.Add(new ConflictPaneLine(sideType, text, sideLineNumber++, source));
                    }

                    for (int row = sideCount + 1; row < rows - 1; row++)
                    {
                        result.Add(new ConflictPaneLine(ConflictPaneLineType.Padding, string.Empty, null, lineIdx + row));
                    }
                }

                result.Add(new ConflictPaneLine(ConflictPaneLineType.Marker, string.Empty, null, region.EndLine));
                lineIdx += rows;
                continue;
            }

            string lineText = lineIdx < documentLines.Count ? documentLines[lineIdx] : string.Empty;
            result.Add(new ConflictPaneLine(ConflictPaneLineType.Common, lineText, sideLineNumber++, lineIdx));
            lineIdx++;
        }

        return result;
    }
}
