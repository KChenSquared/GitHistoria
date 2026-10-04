namespace Historia.Tests;

/// <summary>
/// <see cref="MinimapBars"/> caps a minimap at one bar per bucket row per kind. Drawing one bar
/// per row run again produces no error, only thousands of overlapping sub-pixel rectangles per
/// scroll frame, so the bound is asserted here alongside the mapping's edge cases.
/// </summary>
public sealed class MinimapBarsTests
{
    private static List<DiffDisplayRow> Rows(params DiffDisplayLineType[] types)
        => types.Select(t => new DiffDisplayRow { Type = t, Content = "" }).ToList();

    private static List<DiffDisplayRow> Rows(int count, Func<int, DiffDisplayLineType> type)
        => Enumerable.Range(0, count).Select(i => new DiffDisplayRow { Type = type(i), Content = "" }).ToList();

    private static void AssertDisjointAndInRange(IReadOnlyList<MinimapSpan> spans, int height)
    {
        for (var i = 0; i < spans.Count; i++)
        {
            Assert.True(spans[i].Height >= 1);
            Assert.True(spans[i].Top >= 0 && spans[i].Bottom <= height);
            if (i > 0)
            {
                Assert.True(spans[i].Top > spans[i - 1].Bottom, "touching spans of one kind must merge");
            }
        }
    }

    [Fact]
    public void Alternating_runs_over_many_rows_yield_at_most_one_span_per_bucket_row()
    {
        const int total = 47_000;
        const int height = 800;
        // 7,000 alternating add/del runs (3 rows each), separated by context.
        var rows = Rows(total, i =>
        {
            if (i >= 7_000 * 6) return DiffDisplayLineType.Context;
            return (i / 3 % 2) == 0 ? DiffDisplayLineType.Addition : DiffDisplayLineType.Deletion;
        });

        var bars = MinimapBars.FromRows(rows, total, height);

        Assert.InRange(bars.Additions.Count, 1, height);
        Assert.InRange(bars.Deletions.Count, 1, height);
        AssertDisjointAndInRange(bars.Additions, height);
        AssertDisjointAndInRange(bars.Deletions, height);
    }

    [Fact]
    public void Isolated_one_row_change_still_gets_one_bucket_row()
    {
        var rows = Rows(47_000, i => i == 23_456 ? DiffDisplayLineType.Addition : DiffDisplayLineType.Context);

        var bars = MinimapBars.FromRows(rows, rows.Count, 800);

        var span = Assert.Single(bars.Additions);
        Assert.Equal(1, span.Height);
        Assert.Equal((int)(23_456L * 800 / 47_000), span.Top);
        Assert.Empty(bars.Deletions);
    }

    [Fact]
    public void Adjacent_same_kind_bucket_rows_merge()
    {
        // 10 rows over 10 bucket rows: additions at rows 2,3 and 5 (separated by a deletion at 4).
        var rows = Rows(
            DiffDisplayLineType.Context, DiffDisplayLineType.Context,
            DiffDisplayLineType.Addition, DiffDisplayLineType.Addition,
            DiffDisplayLineType.Deletion,
            DiffDisplayLineType.Addition,
            DiffDisplayLineType.Context, DiffDisplayLineType.Context, DiffDisplayLineType.Context, DiffDisplayLineType.Context);

        var bars = MinimapBars.FromRows(rows, rows.Count, 10);

        // Rows 2-3 and row 5 are not touching; the deletion at 4 is its own kind.
        Assert.Equal([new MinimapSpan(2, 2), new MinimapSpan(5, 1)], bars.Additions);
        Assert.Equal([new MinimapSpan(4, 1)], bars.Deletions);

        // Over 5 bucket rows, rows 2-3 map to [1,2) and row 5 to [2,3): touching, so one span.
        var squeezed = MinimapBars.FromRows(rows, rows.Count, 5);
        Assert.Equal([new MinimapSpan(1, 2)], squeezed.Additions);
    }

    [Fact]
    public void Mixed_bucket_row_yields_both_kinds()
    {
        var rows = Rows(DiffDisplayLineType.Addition, DiffDisplayLineType.Deletion, DiffDisplayLineType.Context, DiffDisplayLineType.Context);

        var bars = MinimapBars.FromRows(rows, rows.Count, 1);

        Assert.Equal([new MinimapSpan(0, 1)], bars.Additions);
        Assert.Equal([new MinimapSpan(0, 1)], bars.Deletions);
    }

    [Fact]
    public void Empty_input_or_zero_height_yields_nothing()
    {
        Assert.Same(MinimapBars.Empty, MinimapBars.FromRows(Rows(), 0, 800));
        Assert.Same(MinimapBars.Empty, MinimapBars.FromRows(Rows(DiffDisplayLineType.Addition), 1, 0));
        Assert.Same(MinimapBars.Empty, MinimapBars.FromRows(Rows(DiffDisplayLineType.Addition), 0, 10));
        Assert.Empty(MinimapBars.FromRegions([(0, 3)], 10, 0));
        Assert.Empty(MinimapBars.FromRegions([], 10, 100));
    }

    [Fact]
    public void Last_row_rounds_into_the_last_bucket_row()
    {
        // 3 rows over 800 bucket rows: the last row covers [533, 800).
        var rows = Rows(DiffDisplayLineType.Context, DiffDisplayLineType.Context, DiffDisplayLineType.Deletion);
        var bars = MinimapBars.FromRows(rows, rows.Count, 800);
        Assert.Equal([new MinimapSpan(533, 267)], bars.Deletions);

        // 1,000 rows over 7 bucket rows: the last row stays inside the strip.
        var many = Rows(1_000, i => i == 999 ? DiffDisplayLineType.Addition : DiffDisplayLineType.Context);
        var last = Assert.Single(MinimapBars.FromRows(many, many.Count, 7).Additions);
        Assert.Equal(new MinimapSpan(6, 1), last);
    }

    [Fact]
    public void Shorter_half_maps_on_the_shared_total()
    {
        // A side-by-side half with 5 rows on a 10-row scale fills only the top half.
        var rows = Rows(5, _ => DiffDisplayLineType.Addition);
        var bars = MinimapBars.FromRows(rows, 10, 100);
        Assert.Equal([new MinimapSpan(0, 50)], bars.Additions);
    }

    [Fact]
    public void Regions_are_bucketed_merged_and_clamped()
    {
        // Inclusive zero-based regions in a 100-line document over 50 bucket rows.
        var spans = MinimapBars.FromRegions([(60, 61), (10, 19), (20, 21), (98, 500)], 100, 50);

        // (10,19) -> [5,10), (20,21) -> [10,11) touch and merge; (60,61) -> [30,31); (98,99) -> [49,50).
        Assert.Equal([new MinimapSpan(5, 6), new MinimapSpan(30, 1), new MinimapSpan(49, 1)], spans);
    }

    [Fact]
    public void Many_regions_yield_at_most_one_span_per_bucket_row()
    {
        var regions = Enumerable.Range(0, 500).Select(i => (i * 20, i * 20 + 4)).ToList();

        var spans = MinimapBars.FromRegions(regions, 10_000, 300);

        Assert.InRange(spans.Count, 1, 300);
        AssertDisjointAndInRange(spans, 300);
    }
}
