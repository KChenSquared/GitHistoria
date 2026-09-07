namespace Historia.Services.Diff;

/// <summary>A changed span within a line: <c>[Start, Start+Length)</c>.</summary>
public readonly record struct WordSpan(int Start, int Length);

/// <summary>
/// Word-level intra-line diff (§V2-17.4). Computed only for the visible deletion/addition line
/// pairs. A word-token LCS marks the runs that differ on each side; unchanged whitespace is not
/// highlighted.
/// </summary>
public static class WordDiff
{
    public readonly record struct Result(IReadOnlyList<WordSpan> OldChanged, IReadOnlyList<WordSpan> NewChanged);

    public static Result Compute(string oldLine, string newLine)
    {
        var a = Tokenize(oldLine);
        var b = Tokenize(newLine);

        // LCS over the token strings.
        int n = a.Count, m = b.Count;
        var dp = new int[n + 1, m + 1];
        for (var i = n - 1; i >= 0; i--)
            for (var j = m - 1; j >= 0; j--)
                dp[i, j] = a[i].Text == b[j].Text
                    ? dp[i + 1, j + 1] + 1
                    : Math.Max(dp[i + 1, j], dp[i, j + 1]);

        var oldChanged = new List<WordSpan>();
        var newChanged = new List<WordSpan>();
        int x = 0, y = 0;
        while (x < n && y < m)
        {
            if (a[x].Text == b[y].Text)
            {
                x++;
                y++;
            }
            else if (dp[x + 1, y] >= dp[x, y + 1])
            {
                AddSpan(oldChanged, a[x]);
                x++;
            }
            else
            {
                AddSpan(newChanged, b[y]);
                y++;
            }
        }
        for (; x < n; x++) AddSpan(oldChanged, a[x]);
        for (; y < m; y++) AddSpan(newChanged, b[y]);

        return new Result(Merge(oldChanged), Merge(newChanged));
    }

    private static void AddSpan(List<WordSpan> into, Tok t)
    {
        if (!string.IsNullOrWhiteSpace(t.Text))
            into.Add(new WordSpan(t.Start, t.Text.Length));
    }

    private static List<WordSpan> Merge(List<WordSpan> spans)
    {
        if (spans.Count <= 1)
            return spans;
        spans.Sort((p, q) => p.Start.CompareTo(q.Start));
        var merged = new List<WordSpan> { spans[0] };
        foreach (var s in spans.Skip(1))
        {
            var last = merged[^1];
            if (s.Start <= last.Start + last.Length + 1)
                merged[^1] = new WordSpan(last.Start, Math.Max(last.Start + last.Length, s.Start + s.Length) - last.Start);
            else
                merged.Add(s);
        }
        return merged;
    }

    private readonly record struct Tok(int Start, string Text);

    private static List<Tok> Tokenize(string line)
    {
        var tokens = new List<Tok>();
        var i = 0;
        while (i < line.Length)
        {
            var start = i;
            if (char.IsLetterOrDigit(line[i]) || line[i] == '_')
                while (i < line.Length && (char.IsLetterOrDigit(line[i]) || line[i] == '_')) i++;
            else if (char.IsWhiteSpace(line[i]))
                while (i < line.Length && char.IsWhiteSpace(line[i])) i++;
            else
                i++;
            tokens.Add(new Tok(start, line[start..i]));
        }
        return tokens;
    }
}
