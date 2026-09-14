namespace Historia.ScenarioLab.Services;

public sealed record ScriptHeaderInfo(string Short, string Long, int Offset, int Length);

/// <summary>
/// Parses the structured comment header block in bash scripts.
/// Grammar:
///   Line 1: #!/usr/bin/env bash
///   Line 2: # @short: <text> (required, &lt;= 72 chars)
///   Optional: # @long:
///   Body lines: #  &lt;text&gt; (hash + two spaces) or lone # (paragraph break)
/// </summary>
public static class ScriptHeader
{
    public static ScriptHeaderInfo Parse(string text)
    {
        if (string.IsNullOrEmpty(text))
            return new ScriptHeaderInfo(string.Empty, string.Empty, 0, 0);

        // Determine where line 2 begins (after shebang)
        int startOffset;
        if (text.StartsWith("#!"))
        {
            var firstNewline = text.IndexOf('\n');
            startOffset = firstNewline >= 0 ? firstNewline + 1 : text.Length;
        }
        else
        {
            startOffset = 0;
        }

        if (startOffset >= text.Length)
            return new ScriptHeaderInfo(string.Empty, string.Empty, startOffset, 0);

        // Find lines starting at startOffset
        var lines = SplitLinesWithOffsets(text, startOffset);
        if (lines.Count == 0)
            return new ScriptHeaderInfo(string.Empty, string.Empty, startOffset, 0);

        var firstLine = lines[0].Text.TrimEnd('\r');
        if (!firstLine.StartsWith("# @short:", StringComparison.OrdinalIgnoreCase) &&
            !firstLine.StartsWith("#@short:", StringComparison.OrdinalIgnoreCase))
        {
            return new ScriptHeaderInfo(string.Empty, string.Empty, startOffset, 0);
        }

        var prefixLen = firstLine.StartsWith("# @short:", StringComparison.OrdinalIgnoreCase)
            ? "# @short:".Length
            : "#@short:".Length;
        var shortText = firstLine[prefixLen..].Trim();

        var blockEnd = lines[0].EndOffset;
        var longLines = new List<string>();
        var inLong = false;

        for (int i = 1; i < lines.Count; i++)
        {
            var lineText = lines[i].Text.TrimEnd('\r');

            if (!inLong)
            {
                if (lineText.Equals("# @long:", StringComparison.OrdinalIgnoreCase) ||
                    lineText.Equals("#@long:", StringComparison.OrdinalIgnoreCase))
                {
                    inLong = true;
                    blockEnd = lines[i].EndOffset;
                    continue;
                }
                break;
            }

            // In long body: must start with '#  ' or be a lone '#'
            if (lineText.StartsWith("#  "))
            {
                longLines.Add(lineText[3..]);
                blockEnd = lines[i].EndOffset;
            }
            else if (lineText == "#" || lineText.Trim() == "#")
            {
                longLines.Add(string.Empty);
                blockEnd = lines[i].EndOffset;
            }
            else
            {
                // Reached end of tagged header block
                break;
            }
        }

        var longText = inLong ? string.Join("\n", longLines) : string.Empty;
        var length = blockEnd - startOffset;
        return new ScriptHeaderInfo(shortText, longText, startOffset, length);
    }

    private sealed record LineOffset(string Text, int StartOffset, int EndOffset);

    private static List<LineOffset> SplitLinesWithOffsets(string text, int startAt)
    {
        var result = new List<LineOffset>();
        int current = startAt;

        while (current < text.Length)
        {
            int nextNewline = text.IndexOf('\n', current);
            int lineEnd;
            int nextStart;

            if (nextNewline >= 0)
            {
                lineEnd = nextNewline + 1; // include \n in block end
                nextStart = nextNewline + 1;
            }
            else
            {
                lineEnd = text.Length;
                nextStart = text.Length;
            }

            var lineText = text[current..(nextNewline >= 0 ? nextNewline : text.Length)];
            result.Add(new LineOffset(lineText, current, lineEnd));
            current = nextStart;
        }

        return result;
    }
}
