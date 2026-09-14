namespace Historia.ScenarioLab.Services;

public sealed record ScriptHeaderInfo(string Short, string Long);

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
            return new ScriptHeaderInfo(string.Empty, string.Empty);

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
            return new ScriptHeaderInfo(string.Empty, string.Empty);

        var lines = SplitLines(text, startOffset);
        if (lines.Count == 0)
            return new ScriptHeaderInfo(string.Empty, string.Empty);

        var firstLine = lines[0].TrimEnd('\r');
        if (!firstLine.StartsWith("# @short:", StringComparison.OrdinalIgnoreCase) &&
            !firstLine.StartsWith("#@short:", StringComparison.OrdinalIgnoreCase))
        {
            return new ScriptHeaderInfo(string.Empty, string.Empty);
        }

        var prefixLen = firstLine.StartsWith("# @short:", StringComparison.OrdinalIgnoreCase)
            ? "# @short:".Length
            : "#@short:".Length;
        var shortText = firstLine[prefixLen..].Trim();

        var longLines = new List<string>();
        var inLong = false;

        for (int i = 1; i < lines.Count; i++)
        {
            var lineText = lines[i].TrimEnd('\r');

            if (!inLong)
            {
                if (lineText.Equals("# @long:", StringComparison.OrdinalIgnoreCase) ||
                    lineText.Equals("#@long:", StringComparison.OrdinalIgnoreCase))
                {
                    inLong = true;
                    continue;
                }
                break;
            }

            // In long body: must start with '#  ' or be a lone '#'
            if (lineText.StartsWith("#  "))
            {
                longLines.Add(lineText[3..]);
            }
            else if (lineText == "#" || lineText.Trim() == "#")
            {
                longLines.Add(string.Empty);
            }
            else
            {
                // Reached end of tagged header block
                break;
            }
        }

        var longText = inLong ? string.Join("\n", longLines) : string.Empty;
        return new ScriptHeaderInfo(shortText, longText);
    }

    private static List<string> SplitLines(string text, int startAt)
    {
        var result = new List<string>();
        int current = startAt;

        while (current < text.Length)
        {
            int nextNewline = text.IndexOf('\n', current);
            int nextStart = nextNewline >= 0 ? nextNewline + 1 : text.Length;

            var lineText = text[current..(nextNewline >= 0 ? nextNewline : text.Length)];
            result.Add(lineText);
            current = nextStart;
        }

        return result;
    }
}
