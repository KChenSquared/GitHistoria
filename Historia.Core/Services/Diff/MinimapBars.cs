namespace Historia.Services.Diff;

/// <summary>A vertical run of whole bucket rows in a minimap, top-inclusive.</summary>
public readonly record struct MinimapSpan(int Top, int Height)
{
    public int Bottom => Top + Height;
}

/// <summary>
/// Buckets a diff's added and deleted rows into minimap bars, so a minimap draws at most one
/// bar per bucket row per kind however many rows the diff has.
/// <para>
/// The mapping: with <c>total</c> rows over <c>height</c> bucket rows, row <c>i</c> covers the
/// half-open band <c>[i·height/total, (i+1)·height/total)</c>. A run of rows <c>[s, e)</c>
/// therefore covers bucket rows <c>floor(s·height/total)</c> up to, but not including,
/// <c>ceil(e·height/total)</c>, and never less than one bucket row, so an isolated one-row
/// change stays visible however long the diff is. Spans are clamped to <c>[0, height)</c>.
/// Overlapping or touching spans of the same kind merge into one. Each kind is bucketed on its
/// own, so a bucket row holding both added and deleted rows yields a span of each kind.
/// </para>
/// <para>Pure and allocation-bounded: the output holds at most <c>height</c> spans per kind.</para>
/// </summary>
public sealed class MinimapBars
{
    public static readonly MinimapBars Empty = new([], []);

    public IReadOnlyList<MinimapSpan> Additions { get; }
    public IReadOnlyList<MinimapSpan> Deletions { get; }

    private MinimapBars(IReadOnlyList<MinimapSpan> additions, IReadOnlyList<MinimapSpan> deletions)
    {
        Additions = additions;
        Deletions = deletions;
    }

    /// <summary>
    /// Buckets <paramref name="rows"/> (a diff's display rows, in order) as the first rows of a
    /// <paramref name="totalRows"/>-row scale over <paramref name="height"/> bucket rows. The
    /// scale may be longer than the list: side-by-side halves share the longer half's total.
    /// </summary>
    public static MinimapBars FromRows(IReadOnlyList<DiffDisplayRow> rows, int totalRows, int height)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Count == 0 || totalRows <= 0 || height <= 0)
        {
            return Empty;
        }

        var additions = new SpanBuilder();
        var deletions = new SpanBuilder();
        var count = Math.Min(rows.Count, totalRows);
        var runType = rows[0].Type;
        var runStart = 0;

        for (var i = 1; i <= count; i++)
        {
            var type = i < count ? rows[i].Type : DiffDisplayLineType.None;
            if (i < count && type == runType)
            {
                continue;
            }

            if (runType == DiffDisplayLineType.Addition)
            {
                additions.Add(Map(runStart, i, totalRows, height));
            }
            else if (runType == DiffDisplayLineType.Deletion)
            {
                deletions.Add(Map(runStart, i, totalRows, height));
            }

            runType = type;
            runStart = i;
        }

        return new MinimapBars(additions.ToList(), deletions.ToList());
    }

    /// <summary>
    /// Buckets inclusive, zero-based line regions (<c>StartLine</c> through <c>EndLine</c>) of a
    /// <paramref name="totalLines"/>-line document over <paramref name="height"/> bucket rows.
    /// Regions may arrive in any order; out-of-range lines are clamped.
    /// </summary>
    public static IReadOnlyList<MinimapSpan> FromRegions(IEnumerable<(int StartLine, int EndLine)> regions, int totalLines, int height)
    {
        ArgumentNullException.ThrowIfNull(regions);
        if (totalLines <= 0 || height <= 0)
        {
            return [];
        }

        var mapped = new List<MinimapSpan>();
        foreach (var (startLine, endLine) in regions)
        {
            var start = Math.Clamp(startLine, 0, totalLines - 1);
            var end = Math.Clamp(endLine, start, totalLines - 1);
            mapped.Add(Map(start, end + 1, totalLines, height));
        }

        mapped.Sort(static (a, b) => a.Top.CompareTo(b.Top));
        var builder = new SpanBuilder();
        foreach (var span in mapped)
        {
            builder.Add(span);
        }

        return builder.ToList();
    }

    private static MinimapSpan Map(int start, int end, int total, int height)
    {
        var top = (int)((long)start * height / total);
        var bottom = (int)(((long)end * height + total - 1) / total);
        top = Math.Min(top, height - 1);
        bottom = Math.Clamp(bottom, top + 1, height);
        return new MinimapSpan(top, bottom - top);
    }

    /// <summary>Merges spans that arrive in non-decreasing <see cref="MinimapSpan.Top"/> order.</summary>
    private struct SpanBuilder
    {
        private List<MinimapSpan>? _spans;
        private int _top;
        private int _bottom;
        private bool _open;

        public void Add(MinimapSpan span)
        {
            if (_open && span.Top <= _bottom)
            {
                _bottom = Math.Max(_bottom, span.Bottom);
                return;
            }

            Flush();
            _top = span.Top;
            _bottom = span.Bottom;
            _open = true;
        }

        public List<MinimapSpan> ToList()
        {
            Flush();
            return _spans ?? [];
        }

        private void Flush()
        {
            if (_open)
            {
                (_spans ??= []).Add(new MinimapSpan(_top, _bottom - _top));
                _open = false;
            }
        }
    }
}
