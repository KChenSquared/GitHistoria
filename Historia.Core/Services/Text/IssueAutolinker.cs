namespace Historia.Services.Text;

/// <summary>One run of a linkified message: plain text when <see cref="Url"/> is null.</summary>
public readonly record struct AutolinkRun(string Text, string? Url);

/// <summary>
/// Applies <see cref="IssueAutolinkRule"/>s to commit-message text (§V2-17.6). Pure and
/// account-free — regex match plus <c>$n</c> capture-group substitution into the URL template.
/// The caller renders the runs and opens <see cref="AutolinkRun.Url"/> in the system browser.
/// </summary>
public static class IssueAutolinker
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(50);

    public static IReadOnlyList<AutolinkRun> Linkify(string text, IReadOnlyList<IssueAutolinkRule> rules)
    {
        if (string.IsNullOrEmpty(text) || rules.Count == 0)
            return [new AutolinkRun(text ?? string.Empty, null)];

        // Collect non-overlapping matches across all rules, earliest first.
        var hits = new List<(int Start, int Length, string Url)>();
        foreach (var rule in rules)
        {
            Regex rx;
            try
            {
                rx = new Regex(rule.Pattern, RegexOptions.CultureInvariant, MatchTimeout);
            }
            catch (ArgumentException)
            {
                continue; // a bad user-entered pattern is ignored, not fatal
            }

            foreach (Match m in SafeMatches(rx, text))
            {
                if (m.Length == 0)
                    continue;
                if (hits.Any(h => m.Index < h.Start + h.Length && m.Index + m.Length > h.Start))
                    continue; // overlaps an earlier rule's match — first rule wins
                hits.Add((m.Index, m.Length, Expand(rule.UrlTemplate, m)));
            }
        }

        if (hits.Count == 0)
            return [new AutolinkRun(text, null)];

        hits.Sort((a, b) => a.Start.CompareTo(b.Start));

        var runs = new List<AutolinkRun>();
        var cursor = 0;
        foreach (var (start, length, url) in hits)
        {
            if (start > cursor)
                runs.Add(new AutolinkRun(text[cursor..start], null));
            runs.Add(new AutolinkRun(text.Substring(start, length), url));
            cursor = start + length;
        }
        if (cursor < text.Length)
            runs.Add(new AutolinkRun(text[cursor..], null));

        return runs;
    }

    private static IEnumerable<Match> SafeMatches(Regex rx, string text)
    {
        Match m;
        try { m = rx.Match(text); }
        catch (RegexMatchTimeoutException) { yield break; }

        while (m.Success)
        {
            yield return m;
            try { m = m.NextMatch(); }
            catch (RegexMatchTimeoutException) { yield break; }
        }
    }

    private static string Expand(string template, Match m) =>
        Regex.Replace(template, @"\$(\d+)", g =>
        {
            // A very long digit run overflows int — leave the token literal rather than throw
            // during message rendering.
            if (!int.TryParse(g.Groups[1].Value, out var n))
                return g.Value;
            return n < m.Groups.Count && m.Groups[n].Success ? Uri.EscapeDataString(m.Groups[n].Value) : g.Value;
        });
}
