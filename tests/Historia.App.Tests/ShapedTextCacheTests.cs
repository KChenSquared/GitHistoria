namespace HistoriaApp.Tests;

/// <summary>
/// <see cref="ShapedTextCache"/> is the shared shaped-text cache behind the diff, conflict and
/// graph render paths. A key that drops a glyph-affecting input draws stale text, and an
/// eviction that skips disposal leaks glyph buffers; neither raises an error, so both are
/// pinned here.
/// </summary>
public sealed class ShapedTextCacheTests
{
    private static readonly Typeface Face = new(FontFamily.Default);

    [AvaloniaFact]
    public void A_hit_returns_the_same_instance()
    {
        var cache = new ShapedTextCache();
        var brush = new SolidColorBrush(Colors.Red);

        var first = cache.Get("1234", Face, 12, brush);
        var second = cache.Get("1234", Face, 12, brush);

        Assert.Same(first, second);
        Assert.Equal(1, cache.Count);
    }

    [AvaloniaFact]
    public void The_key_distinguishes_brush_size_width_trimming_and_lines()
    {
        var cache = new ShapedTextCache();
        var red = new SolidColorBrush(Colors.Red);
        var sameColourOtherBrush = new SolidColorBrush(Colors.Red);

        var baseline = cache.Get("text", Face, 12, red);

        Assert.NotSame(baseline, cache.Get("text", Face, 12, sameColourOtherBrush));
        Assert.NotSame(baseline, cache.Get("text", Face, 13, red));
        Assert.NotSame(baseline, cache.Get("text", Face, 12, red, maxWidth: 40));
        Assert.NotSame(cache.Get("text", Face, 12, red, maxWidth: 40), cache.Get("text", Face, 12, red, maxWidth: 41));
        Assert.NotSame(baseline, cache.Get("text", Face, 12, red, trimming: TextTrimming.CharacterEllipsis));
        Assert.NotSame(baseline, cache.Get("text", Face, 12, red, maxLines: 1));
        Assert.NotSame(baseline, cache.Get("Text", Face, 12, red));
        Assert.NotSame(baseline, cache.Get("text", new Typeface(FontFamily.Default, FontStyle.Italic), 12, red));
        Assert.Same(baseline, cache.Get("text", Face, 12, red));
    }

    [AvaloniaFact]
    public void At_capacity_the_least_recently_used_entry_is_evicted_and_disposed()
    {
        var cache = new ShapedTextCache(capacity: 3);
        var disposed = new List<object>();
        cache.DisposedForTesting = l => disposed.Add(l);

        var a = cache.Get("a", Face, 12, Brushes.Black);
        var b = cache.Get("b", Face, 12, Brushes.Black);
        var c = cache.Get("c", Face, 12, Brushes.Black);
        Assert.Same(a, cache.Get("a", Face, 12, Brushes.Black)); // a is now most recent; b is least

        cache.Get("d", Face, 12, Brushes.Black);

        Assert.Equal(3, cache.Count);
        Assert.Equal([b], disposed);
        Assert.Same(a, cache.Get("a", Face, 12, Brushes.Black));
        Assert.Same(c, cache.Get("c", Face, 12, Brushes.Black));
        Assert.NotSame(b, cache.Get("b", Face, 12, Brushes.Black)); // re-shaped; evicts d
        Assert.Equal(2, disposed.Count);
    }

    [AvaloniaFact]
    public void Clear_disposes_every_cached_layout()
    {
        var cache = new ShapedTextCache();
        var disposed = new List<object>();
        cache.DisposedForTesting = l => disposed.Add(l);

        var layouts = Enumerable.Range(0, 5).Select(i => (object)cache.Get(i.ToString(), Face, 12, Brushes.Black)).ToList();
        cache.Clear();

        Assert.Equal(0, cache.Count);
        Assert.Equal(layouts.OrderBy(RuntimeHelpers.GetHashCode), disposed.OrderBy(RuntimeHelpers.GetHashCode));
    }

    [AvaloniaFact]
    public void Hits_and_misses_are_counted()
    {
        var cache = new ShapedTextCache();

        cache.Get("x", Face, 12, Brushes.Black);
        cache.Get("x", Face, 12, Brushes.Black);
        cache.Get("y", Face, 12, Brushes.Black);
        cache.Get("x", Face, 12, Brushes.Black);

        Assert.Equal(2, cache.Misses);
        Assert.Equal(2, cache.Hits);

        cache.ResetCounters();
        cache.Get("y", Face, 12, Brushes.Black);
        Assert.Equal(0, cache.Misses);
        Assert.Equal(1, cache.Hits);

        cache.Clear();
        cache.Get("y", Face, 12, Brushes.Black);
        Assert.Equal(1, cache.Misses);
    }

    [AvaloniaFact]
    public void Layout_metrics_match_formatted_text_for_single_line_text()
    {
        foreach (var text in new[] { "1", "42", "12345", "+", "-", "A longer commit message line" })
        {
            var cache = new ShapedTextCache();
            var layout = cache.Get(text, Face, 13, Brushes.Black);
            var formatted = new FormattedText(text, System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, Face, 13, Brushes.Black);

            Assert.Equal(formatted.Width, layout.Width, 3);
            Assert.Equal(formatted.Height, layout.Height, 3);
            Assert.Equal(formatted.Baseline, layout.Baseline, 3);
        }
    }
}
