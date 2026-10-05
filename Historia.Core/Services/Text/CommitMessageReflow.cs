namespace Historia.Services.Text;

/// <summary>
/// Reflows a commit message for display in a box narrower than the column it was hard-wrapped
/// at. Each prose paragraph's lines are joined with single spaces, so the box's own soft wrap
/// is the only wrap and lines no longer alternate long/short. Kept line by line: blank lines,
/// list items (their indented continuation lines join the item), lines indented four or more
/// columns, <c>&gt;</c> quotes, <c>|</c> table rows, fenced blocks, and a final trailer block
/// (<c>Signed-off-by:</c>, <c>Co-Authored-By:</c> …). The first paragraph joins exactly as
/// git's <c>%s</c> does. Pure, and idempotent: reflowing the output again changes nothing.
/// </summary>
public static class CommitMessageReflow
{
    private enum LineKind { Blank, Prose, ListItem, Verbatim }

    private static readonly Regex ListItemRx =
        new(@"^[ ]{0,3}([-*+]|\d{1,3}[.)])\s+\S", RegexOptions.CultureInvariant);

    // A list marker at any indent: a nested item directly under another item starts its own line.
    private static readonly Regex NestedListItemRx =
        new(@"^\s+([-*+]|\d{1,3}[.)])\s+\S", RegexOptions.CultureInvariant);

    private static readonly Regex TrailerRx =
        new(@"^[A-Za-z][A-Za-z0-9-]*:\s+\S", RegexOptions.CultureInvariant);

    public static string Reflow(string? message)
    {
        if (string.IsNullOrEmpty(message))
            return "";

        var text = message.Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = text.Split('\n');

        // Trailing whitespace-only lines would otherwise hide a final trailer block.
        var count = lines.Length;
        while (count > 0 && string.IsNullOrWhiteSpace(lines[count - 1]))
            count--;
        if (count == 0)
            return "";
        if (count < lines.Length)
            lines = lines[..count];

        var trailerStart = FindTrailerStart(lines);
        var sb = new StringBuilder(text.Length);
        var prev = LineKind.Blank;
        var fenced = false;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var indent = Indent(line);

            if (IsFence(line, indent))
            {
                fenced = !fenced;
                NewLine(sb, i, line);
                prev = LineKind.Verbatim;
                continue;
            }

            if (fenced || i >= trailerStart)
            {
                NewLine(sb, i, line);
                prev = LineKind.Verbatim;
                continue;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                NewLine(sb, i, "");
                prev = LineKind.Blank;
                continue;
            }

            if (ListItemRx.IsMatch(line) || (prev == LineKind.ListItem && NestedListItemRx.IsMatch(line)))
            {
                NewLine(sb, i, line);
                prev = LineKind.ListItem;
                continue;
            }

            if (prev == LineKind.ListItem && indent >= 2)
            {
                Join(sb, line);
                continue;
            }

            var lead = line.TrimStart();
            if (indent >= 4 || lead[0] == '>' || lead[0] == '|')
            {
                NewLine(sb, i, line);
                prev = LineKind.Verbatim;
                continue;
            }

            if (prev == LineKind.Prose)
                Join(sb, line);
            else
                NewLine(sb, i, line);
            prev = LineKind.Prose;
        }

        return sb.ToString().TrimEnd('\n');
    }

    private static void NewLine(StringBuilder sb, int index, string line)
    {
        if (index > 0)
            sb.Append('\n');
        sb.Append(line);
    }

    private static void Join(StringBuilder sb, string line)
    {
        while (sb.Length > 0 && sb[^1] is ' ' or '\t')
            sb.Length--;
        sb.Append(' ').Append(line.Trim());
    }

    // Leading whitespace in columns, a tab counting as four.
    private static int Indent(string line)
    {
        var cols = 0;
        foreach (var c in line)
        {
            if (c == ' ') cols++;
            else if (c == '\t') cols += 4;
            else break;
        }
        return cols;
    }

    private static bool IsFence(string line, int indent)
    {
        if (indent > 3)
            return false;
        var lead = line.TrimStart();
        return lead.StartsWith("```", StringComparison.Ordinal) || lead.StartsWith("~~~", StringComparison.Ordinal);
    }

    // The index of the last paragraph when every one of its lines is a "Key: value" trailer,
    // else past the end. The first paragraph is never a trailer block: a "feat: x" subject
    // matches the pattern but is the subject.
    private static int FindTrailerStart(string[] lines)
    {
        var lastBlank = -1;
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            if (string.IsNullOrWhiteSpace(lines[i]))
            {
                lastBlank = i;
                break;
            }
        }

        if (lastBlank < 0 || lastBlank == lines.Length - 1)
            return lines.Length;

        var hasContentBefore = false;
        for (var i = 0; i < lastBlank; i++)
        {
            if (!string.IsNullOrWhiteSpace(lines[i]))
            {
                hasContentBefore = true;
                break;
            }
        }
        if (!hasContentBefore)
            return lines.Length;

        for (var i = lastBlank + 1; i < lines.Length; i++)
        {
            if (!TrailerRx.IsMatch(lines[i]))
                return lines.Length;
        }
        return lastBlank + 1;
    }
}
