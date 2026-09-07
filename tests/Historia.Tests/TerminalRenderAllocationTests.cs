using Historia.Rendering;
using Historia.Services.Terminal;
using Xunit;

namespace Historia.Tests;

/// <summary>
/// Constitution rule 24 — "the render path creates no Historia-owned object per frame" — measured
/// as pool/cache misses, and "a test says so" (V5 §6.4). The control lives in <c>Historia.App</c>
/// which the test project cannot reference, so the pooling lives in the Core
/// <see cref="RenderResourceCache{TKey,TValue}"/> and the test drives that directly: warm over a
/// frame, reset counters, render the same frame again, assert every miss counter is zero.
/// </summary>
public class TerminalRenderAllocationTests
{
    private sealed record PenKey(int Rgb, double Thickness, int Style);

    /// <summary>Mirrors what <c>TerminalControl</c> touches for one frame: a brush per distinct
    /// colour, a pen per distinct underline/strike spec, and a glyph per distinct codepoint.</summary>
    private static void RenderFrame(
        RenderResourceCache<int, object> brushes,
        RenderResourceCache<PenKey, object> pens,
        RenderResourceCache<int, object> glyphs)
    {
        int[] palette = [0, 1, 2, 7, 15, 231, 244, 0x50FA7B];
        foreach (var c in palette) { _ = brushes.Get(c); _ = brushes.Get(c); }

        _ = pens.Get(new PenKey(0xCCCCCC, 1.0, 0));
        _ = pens.Get(new PenKey(0xCCCCCC, 1.0, 1));

        foreach (var codepoint in "the quick brown fox jumps — 你好 42".EnumerateRunes())
            _ = glyphs.Get(codepoint.Value);
    }

    [Fact]
    public void SecondFrameOverUnchangedBuffer_RecordsZeroMisses()
    {
        var brushes = new RenderResourceCache<int, object>(512, _ => new object());
        var pens = new RenderResourceCache<PenKey, object>(256, _ => new object());
        var glyphs = new RenderResourceCache<int, object>(4096, _ => new object());

        // Frame 1 — warm.
        RenderFrame(brushes, pens, glyphs);
        Assert.True(brushes.Misses > 0 && pens.Misses > 0 && glyphs.Misses > 0);

        brushes.ResetCounters();
        pens.ResetCounters();
        glyphs.ResetCounters();

        // Frame 2 — identical buffer.
        RenderFrame(brushes, pens, glyphs);

        Assert.Equal(0, brushes.Misses);
        Assert.Equal(0, pens.Misses);
        Assert.Equal(0, glyphs.Misses);
        Assert.Equal(0, brushes.Evictions);
        Assert.Equal(0, pens.Evictions);
        Assert.Equal(0, glyphs.Evictions);
    }

    [Fact]
    public void GlyphCache_IsBoundedLru_NotAnUnboundedLeak()
    {
        var glyphs = new RenderResourceCache<int, object>(64, _ => new object());
        for (var i = 0; i < 5000; i++)
            _ = glyphs.Get(i);

        Assert.Equal(64, glyphs.Count);
        Assert.True(glyphs.Evictions >= 4936);
    }

    [Fact]
    public void Clear_DropsEntriesButKeepsCounters()
    {
        var brushes = new RenderResourceCache<int, object>(16, _ => new object());
        _ = brushes.Get(1);
        var missesBefore = brushes.Misses;
        brushes.Clear();
        Assert.Equal(0, brushes.Count);
        Assert.Equal(missesBefore, brushes.Misses);
    }

    [Fact]
    public void TerminalColorResolver_IsPureAcrossFrames()
    {
        // The resolver feeds the brush key; if it were not deterministic the cache would thrash.
        var a = TerminalColorResolver.Resolve(TerminalColor.Indexed(4), Historia.Models.EffectiveTheme.Dark);
        var b = TerminalColorResolver.Resolve(TerminalColor.Indexed(4), Historia.Models.EffectiveTheme.Dark);
        Assert.Equal(a, b);
    }
}
