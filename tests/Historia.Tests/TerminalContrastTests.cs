namespace Historia.Tests;

/// <summary>
/// in High Contrast every ANSI colour must clear 4.5:1 against <c>term-bg</c>. A
/// contrast guarantee checked by eye regresses silently (constitution rule 36) — so it is a
/// test, not a spreadsheet.
/// </summary>
[Trait("tier", "unit")]
public class TerminalContrastTests
{
    private static readonly string[] AnsiTokens =
    [
        "ansi-black", "ansi-red", "ansi-green", "ansi-yellow",
        "ansi-blue", "ansi-magenta", "ansi-cyan", "ansi-white",
        "ansi-br-black", "ansi-br-red", "ansi-br-green", "ansi-br-yellow",
        "ansi-br-blue", "ansi-br-magenta", "ansi-br-cyan", "ansi-br-white",
    ];

    [Fact]
    public void HighContrastAnsiMeetsMinimumRatio()
    {
        var palette = HistoriaPalette.For(EffectiveTheme.HighContrast);
        var bg = palette["term-bg"];

        var failures = AnsiTokens
            .Select(t => (t, ratio: TerminalColorResolver.ContrastRatio(palette[t], bg)))
            .Where(x => x.ratio < 4.5)
            .Select(x => $"{x.t}={x.ratio:F2}")
            .ToList();

        Assert.True(failures.Count == 0, "HC ANSI tokens below 4.5:1 on term-bg: " + string.Join(", ", failures));
    }

    [Fact]
    public void HighContrastTermCursorAndSelectionAreVisible()
    {
        var palette = HistoriaPalette.For(EffectiveTheme.HighContrast);
        var bg = palette["term-bg"];
        Assert.True(TerminalColorResolver.ContrastRatio(palette["term-cursor"], bg) >= 3.0);
        Assert.True(TerminalColorResolver.ContrastRatio(palette["term-fg"], bg) >= 7.0);
    }

    [Theory]
    [InlineData(EffectiveTheme.Dark)]
    [InlineData(EffectiveTheme.Light)]
    public void NonHcThemes_DefaultTextClearsBackground(EffectiveTheme theme)
    {
        var palette = HistoriaPalette.For(theme);
        Assert.True(TerminalColorResolver.ContrastRatio(palette["term-fg"], palette["term-bg"]) >= 4.5);
    }
}
