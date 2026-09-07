namespace Historia.Tests;

public class TerminalColorResolverTests
{
    [Theory]
    [InlineData(16, 0x00, 0x00, 0x00)]
    [InlineData(21, 0x00, 0x00, 0xFF)]
    [InlineData(46, 0x00, 0xFF, 0x00)]
    [InlineData(196, 0xFF, 0x00, 0x00)]
    [InlineData(231, 0xFF, 0xFF, 0xFF)]
    public void Cube_MatchesPublishedXtermValues(int index, int r, int g, int b)
    {
        var c = TerminalColorResolver.FromIndex256(index);
        Assert.Equal(Rgb.FromRgb((byte)r, (byte)g, (byte)b), c);
    }

    [Theory]
    [InlineData(232, 0x08)]
    [InlineData(243, 8 + 11 * 10)]
    [InlineData(255, 8 + 23 * 10)]
    public void Greyscale_MatchesRamp(int index, int level)
    {
        var c = TerminalColorResolver.FromIndex256(index);
        Assert.Equal(Rgb.FromRgb((byte)level, (byte)level, (byte)level), c);
    }

    [Fact]
    public void FromIndex256_RejectsThemedRange()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TerminalColorResolver.FromIndex256(15));
        Assert.Throws<ArgumentOutOfRangeException>(() => TerminalColorResolver.FromIndex256(256));
    }

    [Fact]
    public void TruecolorPassesThrough()
    {
        var resolved = TerminalColorResolver.Resolve(TerminalColor.FromRgb(80, 250, 123), EffectiveTheme.Dark);
        Assert.Equal(Rgb.FromRgb(80, 250, 123), resolved);
    }

    [Fact]
    public void Indexed_0To15_ComeFromPalette()
    {
        var red = TerminalColorResolver.Resolve(TerminalColor.Indexed(1), EffectiveTheme.Dark);
        Assert.Equal(Historia.Services.Theming.HistoriaPalette.Get(EffectiveTheme.Dark, "ansi-red"), red);
    }

    [Fact]
    public void Default_ForegroundAndBackground_FromPalette()
    {
        Assert.Equal(Historia.Services.Theming.HistoriaPalette.Get(EffectiveTheme.Light, "term-fg"),
            TerminalColorResolver.Resolve(TerminalColor.DefaultForeground, EffectiveTheme.Light));
        Assert.Equal(Historia.Services.Theming.HistoriaPalette.Get(EffectiveTheme.Light, "term-bg"),
            TerminalColorResolver.Resolve(TerminalColor.DefaultBackground, EffectiveTheme.Light));
    }

    [Fact]
    public void FromEmulator_MapsModes()
    {
        Assert.Equal(TerminalColorKind.Rgb, TerminalColor.FromEmulator(0x50FA7B, 1, false).Kind);
        Assert.Equal(TerminalColor.Indexed(196), TerminalColor.FromEmulator(196, 0, false));
        Assert.Equal(TerminalColor.DefaultForeground, TerminalColor.FromEmulator(256, 0, false));
        Assert.Equal(TerminalColor.DefaultBackground, TerminalColor.FromEmulator(257, 0, true));
    }

    [Fact]
    public void EnsureContrast_LiftsBelowThreshold()
    {
        var black = Rgb.FromRgb(0, 0, 0);
        var lifted = TerminalColorResolver.EnsureContrast(black, black, Rgb.FromRgb(255, 255, 255));
        Assert.True(TerminalColorResolver.ContrastRatio(lifted, black) >= 4.5);
    }

    [Fact]
    public void EnsureContrast_LeavesCompliantAlone()
    {
        var white = Rgb.FromRgb(255, 255, 255);
        var black = Rgb.FromRgb(0, 0, 0);
        Assert.Equal(white, TerminalColorResolver.EnsureContrast(white, black, white));
    }
}
