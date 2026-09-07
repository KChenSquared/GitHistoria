namespace Historia.Services.Markdown;

/// <summary>
/// A small, dependency-free GitHub-Flavored-Markdown renderer for commit bodies, notes, and
/// READMEs (§V2-17.5, D12: bundled, must sanitize).
/// <para>
/// Security model: the input is untrusted (whoever wrote the commit / README). Raw HTML never
/// passes through — every source character is HTML-encoded before markup is added. Link hrefs
/// are restricted to <c>http</c>/<c>https</c>/<c>mailto</c>; anything else (notably
/// <c>javascript:</c>) is neutralised to a non-link. Images are <b>never</b> emitted as
/// <c>&lt;img&gt;</c> — a remote <c>src</c> would both be an XSS vector and a Rule 0 violation
/// (the app would fetch it) — they render as a bracketed "[image: alt]" note instead.
/// </para>
/// </summary>
// ── V4: a UI-agnostic block model so the Avalonia MarkdownView can render without HTML. ──

public enum MdInlineKind { Text, Code, Bold, Italic, Strike, Link }

/// <summary>One run of inline text. <see cref="Href"/> is set only for <see cref="MdInlineKind.Link"/>.</summary>
public readonly record struct MdInline(MdInlineKind Kind, string Text, string? Href = null);

public abstract record MdBlock;
public sealed record MdHeading(int Level, IReadOnlyList<MdInline> Inlines) : MdBlock;
public sealed record MdParagraph(IReadOnlyList<MdInline> Inlines) : MdBlock;
public sealed record MdCode(string Text) : MdBlock;
public sealed record MdRule : MdBlock;
public sealed record MdQuote(IReadOnlyList<MdBlock> Children) : MdBlock;
public sealed record MdList(bool Ordered, IReadOnlyList<IReadOnlyList<MdInline>> Items) : MdBlock;
public sealed record MdTable(
    IReadOnlyList<IReadOnlyList<MdInline>> Header,
    IReadOnlyList<IReadOnlyList<IReadOnlyList<MdInline>>> Rows) : MdBlock;

public static class MarkdownRenderer
{
    /// <summary>
    /// Parses GFM into a block model (V4). Same block grammar as <see cref="ToHtml"/> — fenced
    /// code, headings, HR, blockquote (recursive), GFM tables, flat lists, paragraphs — with
    /// inline runs for bold / italic / strike / code / links / bare autolinks. Untrusted input:
    /// no run carries raw markup, and link hrefs are restricted to http / https / mailto.
    /// </summary>
    public static IReadOnlyList<MdBlock> ToBlocks(string? markdown)
    {
        var blocks = new List<MdBlock>();
        if (string.IsNullOrWhiteSpace(markdown))
            return blocks;

        var lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var i = 0;
        while (i < lines.Length)
        {
            var line = lines[i];

            if (Regex.IsMatch(line, @"^\s*(```|~~~)"))
            {
                var fence = line.TrimStart()[..3];
                i++;
                var code = new StringBuilder();
                while (i < lines.Length && !lines[i].TrimStart().StartsWith(fence))
                    code.AppendLine(lines[i++]);
                if (i < lines.Length) i++;
                blocks.Add(new MdCode(code.ToString().TrimEnd('\n')));
                continue;
            }

            if (line.Trim().Length == 0) { i++; continue; }

            var h = Regex.Match(line, @"^(#{1,6})\s+(.*?)\s*#*\s*$");
            if (h.Success)
            {
                blocks.Add(new MdHeading(h.Groups[1].Value.Length, ParseInline(h.Groups[2].Value)));
                i++;
                continue;
            }

            if (Regex.IsMatch(line, @"^\s*([-*_])(\s*\1){2,}\s*$"))
            {
                blocks.Add(new MdRule());
                i++;
                continue;
            }

            if (Regex.IsMatch(line, @"^\s*>"))
            {
                var quote = new StringBuilder();
                while (i < lines.Length && Regex.IsMatch(lines[i], @"^\s*>"))
                    quote.AppendLine(Regex.Replace(lines[i++], @"^\s*>\s?", ""));
                blocks.Add(new MdQuote(ToBlocks(quote.ToString())));
                continue;
            }

            if (i + 1 < lines.Length && line.Contains('|') && Regex.IsMatch(lines[i + 1], @"^\s*\|?[\s:|-]+\|?\s*$"))
            {
                var header = SplitRow(lines[i]).Select(ParseInline).ToList();
                i += 2;
                var rows = new List<IReadOnlyList<IReadOnlyList<MdInline>>>();
                while (i < lines.Length && lines[i].Contains('|') && lines[i].Trim().Length > 0)
                    rows.Add(SplitRow(lines[i++]).Select(ParseInline).ToList());
                blocks.Add(new MdTable(header, rows));
                continue;
            }

            if (Regex.IsMatch(line, @"^\s*([-*+]|\d+\.)\s+"))
            {
                var ordered = Regex.IsMatch(line, @"^\s*\d+\.\s+");
                var items = new List<IReadOnlyList<MdInline>>();
                while (i < lines.Length && Regex.IsMatch(lines[i], @"^\s*([-*+]|\d+\.)\s+"))
                    items.Add(ParseInline(Regex.Replace(lines[i++], @"^\s*([-*+]|\d+\.)\s+", "")));
                blocks.Add(new MdList(ordered, items));
                continue;
            }

            var para = new StringBuilder();
            while (i < lines.Length && lines[i].Trim().Length > 0
                   && !Regex.IsMatch(lines[i], @"^(#{1,6}\s|\s*>|\s*([-*+]|\d+\.)\s|\s*(```|~~~))"))
            {
                if (para.Length > 0) para.Append(' ');
                para.Append(lines[i++].Trim());
            }
            blocks.Add(new MdParagraph(ParseInline(para.ToString())));
        }

        return blocks;
    }

    private static readonly Regex InlineToken = new(
        @"(?<code>`[^`]+`)" +
        @"|(?<img>!\[[^\]]*\]\([^)]+\))" +
        @"|(?<link>\[[^\]]+\]\([^)\s]+(?:\s+""[^""]*"")?\))" +
        @"|(?<url>(?<![""=>])\bhttps?://[^\s<]+)" +
        @"|(?<bold>\*\*[^*]+\*\*|__[^_]+__)" +
        @"|(?<italic>(?<!\*)\*[^*]+\*(?!\*)|(?<!_)_[^_]+_(?!_))" +
        @"|(?<strike>~~[^~]+~~)",
        RegexOptions.Compiled);

    private static List<MdInline> ParseInline(string text)
    {
        var runs = new List<MdInline>();
        var pos = 0;
        foreach (Match m in InlineToken.Matches(text))
        {
            if (m.Index > pos)
                runs.Add(new MdInline(MdInlineKind.Text, text[pos..m.Index]));

            if (m.Groups["code"].Success)
                runs.Add(new MdInline(MdInlineKind.Code, m.Value[1..^1]));
            else if (m.Groups["img"].Success)
            {
                var alt = Regex.Match(m.Value, @"!\[([^\]]*)\]").Groups[1].Value;
                runs.Add(new MdInline(MdInlineKind.Text, $"[image: {alt}]"));
            }
            else if (m.Groups["link"].Success)
            {
                var lm = Regex.Match(m.Value, @"\[([^\]]+)\]\(([^)\s]+)");
                var href = SafeHref(lm.Groups[2].Value);
                runs.Add(href is null
                    ? new MdInline(MdInlineKind.Text, lm.Groups[1].Value)
                    : new MdInline(MdInlineKind.Link, lm.Groups[1].Value, href));
            }
            else if (m.Groups["url"].Success)
                runs.Add(new MdInline(MdInlineKind.Link, m.Value, m.Value));
            else if (m.Groups["bold"].Success)
                runs.Add(new MdInline(MdInlineKind.Bold, m.Value[2..^2]));
            else if (m.Groups["italic"].Success)
                runs.Add(new MdInline(MdInlineKind.Italic, m.Value[1..^1]));
            else if (m.Groups["strike"].Success)
                runs.Add(new MdInline(MdInlineKind.Strike, m.Value[2..^2]));

            pos = m.Index + m.Length;
        }
        if (pos < text.Length)
            runs.Add(new MdInline(MdInlineKind.Text, text[pos..]));
        return runs;
    }

    public static string ToHtml(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return "";

        var lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var sb = new StringBuilder();
        var i = 0;

        while (i < lines.Length)
        {
            var line = lines[i];

            // Fenced code block
            if (Regex.IsMatch(line, @"^\s*(```|~~~)"))
            {
                var fence = line.TrimStart()[..3];
                i++;
                var code = new StringBuilder();
                while (i < lines.Length && !lines[i].TrimStart().StartsWith(fence))
                    code.AppendLine(lines[i++]);
                if (i < lines.Length) i++; // closing fence
                sb.Append("<pre class=\"md-code\"><code>")
                  .Append(WebUtility.HtmlEncode(code.ToString().TrimEnd('\n')))
                  .Append("</code></pre>\n");
                continue;
            }

            // Blank line
            if (line.Trim().Length == 0) { i++; continue; }

            // ATX heading
            var h = Regex.Match(line, @"^(#{1,6})\s+(.*?)\s*#*\s*$");
            if (h.Success)
            {
                var level = h.Groups[1].Value.Length;
                sb.Append("<h").Append(level).Append('>')
                  .Append(Inline(h.Groups[2].Value))
                  .Append("</h").Append(level).Append(">\n");
                i++;
                continue;
            }

            // Horizontal rule
            if (Regex.IsMatch(line, @"^\s*([-*_])(\s*\1){2,}\s*$"))
            {
                sb.Append("<hr />\n");
                i++;
                continue;
            }

            // Blockquote
            if (Regex.IsMatch(line, @"^\s*>"))
            {
                var quote = new StringBuilder();
                while (i < lines.Length && Regex.IsMatch(lines[i], @"^\s*>"))
                    quote.AppendLine(Regex.Replace(lines[i++], @"^\s*>\s?", ""));
                sb.Append("<blockquote>\n").Append(ToHtml(quote.ToString())).Append("</blockquote>\n");
                continue;
            }

            // GFM table
            if (i + 1 < lines.Length && line.Contains('|') && Regex.IsMatch(lines[i + 1], @"^\s*\|?[\s:|-]+\|?\s*$"))
            {
                i = Table(lines, i, sb);
                continue;
            }

            // Lists
            if (Regex.IsMatch(line, @"^\s*([-*+]|\d+\.)\s+"))
            {
                var ordered = Regex.IsMatch(line, @"^\s*\d+\.\s+");
                sb.Append(ordered ? "<ol>\n" : "<ul>\n");
                while (i < lines.Length && Regex.IsMatch(lines[i], @"^\s*([-*+]|\d+\.)\s+"))
                {
                    var item = Regex.Replace(lines[i++], @"^\s*([-*+]|\d+\.)\s+", "");
                    sb.Append("<li>").Append(Inline(item)).Append("</li>\n");
                }
                sb.Append(ordered ? "</ol>\n" : "</ul>\n");
                continue;
            }

            // Paragraph (until blank line)
            var para = new StringBuilder();
            while (i < lines.Length && lines[i].Trim().Length > 0
                   && !Regex.IsMatch(lines[i], @"^(#{1,6}\s|\s*>|\s*([-*+]|\d+\.)\s|\s*(```|~~~))"))
            {
                if (para.Length > 0) para.Append(" ");
                para.Append(lines[i++].Trim());
            }
            sb.Append("<p>").Append(Inline(para.ToString())).Append("</p>\n");
        }

        return sb.ToString();
    }

    private static int Table(string[] lines, int start, StringBuilder sb)
    {
        var header = SplitRow(lines[start]);
        var i = start + 2;
        sb.Append("<table class=\"md-table\"><thead><tr>");
        foreach (var cell in header)
            sb.Append("<th>").Append(Inline(cell)).Append("</th>");
        sb.Append("</tr></thead><tbody>\n");
        while (i < lines.Length && lines[i].Contains('|') && lines[i].Trim().Length > 0)
        {
            sb.Append("<tr>");
            foreach (var cell in SplitRow(lines[i]))
                sb.Append("<td>").Append(Inline(cell)).Append("</td>");
            sb.Append("</tr>\n");
            i++;
        }
        sb.Append("</tbody></table>\n");
        return i;
    }

    private static IEnumerable<string> SplitRow(string row) =>
        row.Trim().Trim('|').Split('|').Select(c => c.Trim());

    /// <summary>Inline formatting. Everything is HTML-encoded first — no raw HTML ever survives.</summary>
    private static string Inline(string text)
    {
        var s = WebUtility.HtmlEncode(text);

        // Images → a safe bracketed note (never an <img> with a remote src).
        s = Regex.Replace(s, @"!\[([^\]]*)\]\(([^)]+)\)", m => $"<span class=\"md-img\">[image: {m.Groups[1].Value}]</span>");

        // Inline code
        s = Regex.Replace(s, @"`([^`]+)`", "<code>$1</code>");

        // Links [text](href). Emitted with data-href (not href) so the WebView never navigates
        // itself — Historia.wireMarkdown hands the URL to the system browser (§V2-17.5).
        s = Regex.Replace(s, @"\[([^\]]+)\]\(([^)\s]+)(?:\s+&quot;[^)]*&quot;)?\)", m =>
        {
            var href = SafeHref(WebUtility.HtmlDecode(m.Groups[2].Value));
            return href is null
                ? m.Groups[1].Value
                : $"<a class=\"md-link\" data-href=\"{WebUtility.HtmlEncode(href)}\">{m.Groups[1].Value}</a>";
        });

        // Bare autolinks
        s = Regex.Replace(s, @"(?<![""=>])\bhttps?://[^\s<]+", m =>
            $"<a class=\"md-link\" data-href=\"{m.Value}\">{m.Value}</a>");

        // Bold / italic / strikethrough
        s = Regex.Replace(s, @"\*\*([^*]+)\*\*", "<strong>$1</strong>");
        s = Regex.Replace(s, @"__([^_]+)__", "<strong>$1</strong>");
        s = Regex.Replace(s, @"(?<!\*)\*([^*]+)\*(?!\*)", "<em>$1</em>");
        s = Regex.Replace(s, @"(?<!_)_([^_]+)_(?!_)", "<em>$1</em>");
        s = Regex.Replace(s, @"~~([^~]+)~~", "<del>$1</del>");

        return s;
    }

    /// <summary>Returns the href if its scheme is safe to follow, else null (neutralise the link).</summary>
    private static string? SafeHref(string raw)
    {
        var href = raw.Trim();
        if (href.StartsWith('#') || href.StartsWith('/') || href.StartsWith("./") || href.StartsWith("../"))
            return null; // relative links have no meaning outside a repo browser — drop them
        if (Uri.TryCreate(href, UriKind.Absolute, out var uri)
            && uri.Scheme is "http" or "https" or "mailto")
            return href;
        return null;
    }
}
