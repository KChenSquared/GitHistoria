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
    Base,
    Padding
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
    /// Builds aligned pane lines for the specified side.
    /// </summary>
    public static IReadOnlyList<ConflictPaneLine> Build(ConflictParse parse, string? text, ConflictSide side)
    {
        text ??= string.Empty;
        var documentLines = ExtractLineStrings(text);
        int lineCount = parse.LineCount;

        // Guarantee documentLines has at least lineCount entries
        while (documentLines.Count < lineCount)
        {
            documentLines.Add(string.Empty);
        }

        var result = new List<ConflictPaneLine>(lineCount);
        int regionIndex = 0;
        int sideLineNumber = 1;

        for (int lineIdx = 0; lineIdx < lineCount; lineIdx++)
        {
            string lineText = lineIdx < documentLines.Count ? documentLines[lineIdx] : string.Empty;

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

    private static List<string> ExtractLineStrings(string text)
    {
        var lines = new List<string>();
        if (string.IsNullOrEmpty(text))
        {
            lines.Add(string.Empty);
            return lines;
        }

        int offset = 0;
        int length = text.Length;

        while (offset < length)
        {
            int lineStart = offset;
            int nextBreak = -1;
            int delimLen = 0;

            for (int i = offset; i < length; i++)
            {
                char c = text[i];
                if (c == '\r')
                {
                    nextBreak = i;
                    delimLen = (i + 1 < length && text[i + 1] == '\n') ? 2 : 1;
                    break;
                }
                if (c == '\n')
                {
                    nextBreak = i;
                    delimLen = 1;
                    break;
                }
            }

            if (nextBreak >= 0)
            {
                lines.Add(text.Substring(lineStart, nextBreak - lineStart));
                offset = nextBreak + delimLen;
                if (offset == length)
                {
                    lines.Add(string.Empty);
                    break;
                }
            }
            else
            {
                lines.Add(text.Substring(lineStart, length - lineStart));
                break;
            }
        }

        return lines;
    }
}
