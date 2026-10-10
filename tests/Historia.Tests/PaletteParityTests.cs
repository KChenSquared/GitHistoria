namespace Historia.Tests;

/// <summary>
/// <see cref="HistoriaPalette"/> is the single source of truth for every colour
/// the app paints (the theme is generated from this table by <c>AvaloniaThemeApplier</c>).
/// Asserts self-consistency: every theme defines exactly the declared token set, and each hex
/// round-trips.
/// </summary>
[Trait("tier", "unit")]
public class PaletteParityTests
{
    public static IEnumerable<object[]> Themes()
    {
        yield return new object[] { EffectiveTheme.Dark };
        yield return new object[] { EffectiveTheme.Light };
        yield return new object[] { EffectiveTheme.HighContrast };
    }

    [Theory]
    [MemberData(nameof(Themes))]
    public void EveryThemeDefinesExactlyTheDeclaredTokens(EffectiveTheme theme)
    {
        var palette = HistoriaPalette.For(theme);
        Assert.Equal(
            HistoriaPalette.Tokens.OrderBy(k => k, StringComparer.Ordinal),
            palette.Keys.OrderBy(k => k, StringComparer.Ordinal));

        foreach (var token in palette.Keys)
        {
            var hex = palette[token].ToHex();
            Assert.Equal(palette[token], Rgb.Parse(hex));
        }
    }

    // badge labels and icons are white in every theme, whatever the lane colour.
    [Fact]
    public void RefLabelToken_is_white_in_every_native_theme()
    {
        foreach (var theme in new[] { EffectiveTheme.Dark, EffectiveTheme.Light, EffectiveTheme.HighContrast })
            Assert.Equal(Color.FromRgb(0xFF, 0xFF, 0xFF), HistoriaPalette.Get(theme, "ref-label"));
    }

    // dark is Synthwave Sunset and light is Retro Sunset, each verbatim in index order.
    [Theory]
    [InlineData(EffectiveTheme.Dark, 0, "#E09E1D")] // Cyber Yellow
    [InlineData(EffectiveTheme.Dark, 1, "#FF5E00")] // Neon Orange
    [InlineData(EffectiveTheme.Dark, 2, "#FF003C")] // Laser Red
    [InlineData(EffectiveTheme.Dark, 3, "#FF00A0")] // Synthwave Pink
    [InlineData(EffectiveTheme.Dark, 4, "#B500FF")] // Hyper Magenta
    [InlineData(EffectiveTheme.Dark, 5, "#5D00FF")] // Electric Indigo
    [InlineData(EffectiveTheme.Dark, 6, "#0088FF")] // Azure Glow
    [InlineData(EffectiveTheme.Dark, 7, "#00B5B5")] // Grid Cyan
    [InlineData(EffectiveTheme.Dark, 8, "#008D64")] // Matrix Mint
    [InlineData(EffectiveTheme.Light, 0, "#D90429")] // Vintage Crimson
    [InlineData(EffectiveTheme.Light, 1, "#F4A261")] // Tangerine
    [InlineData(EffectiveTheme.Light, 2, "#E9C46A")] // Goldenrod
    [InlineData(EffectiveTheme.Light, 3, "#2A9D8F")] // Muted Teal
    [InlineData(EffectiveTheme.Light, 4, "#457B9D")] // Steel Blue
    [InlineData(EffectiveTheme.Light, 5, "#023E8A")] // Ocean Depth
    [InlineData(EffectiveTheme.Light, 6, "#7209B7")] // Plum Purple
    [InlineData(EffectiveTheme.Light, 7, "#B5179E")] // Vibrant Fuchsia
    [InlineData(EffectiveTheme.Light, 8, "#F72585")] // Retro Pink
    public void Dark_and_light_lanes_match_the_specified_palettes(EffectiveTheme theme, int index, string hex)
    {
        Assert.Equal(9, LanePalette.Size);
        Assert.Equal(Rgb.Parse(hex), LanePalette.ColorFor(index, theme));
    }

    // no two lanes may share a colour within a theme; keep them perceptually apart.
    [Theory]
    [MemberData(nameof(Themes))]
    public void Every_theme_has_nine_distinct_lane_colours(EffectiveTheme theme)
    {
        var colours = Enumerable.Range(0, LanePalette.Size)
            .Select(i => LanePalette.ColorFor(i, theme)).ToArray();

        for (var a = 0; a < colours.Length; a++)
            for (var b = a + 1; b < colours.Length; b++)
            {
                Assert.NotEqual(colours[a], colours[b]);
                Assert.True(DeltaE76(colours[a], colours[b]) >= 12,
                    $"{theme}: lanes {a} ({colours[a].ToHex()}) and {b} ({colours[b].ToHex()}) " +
                    $"are only ΔE {DeltaE76(colours[a], colours[b]):F1} apart");
            }
    }

    // a light-theme lane must stay visible on the light field, but pale lanes are
    // intended (the lane wash carries them), so the floor is 1.5:1; each high-contrast lane
    // must clear WCAG AA (4.5:1) on black.
    [Fact]
    public void Light_and_hc_lane_lines_meet_contrast()
    {
        var lightBg = HistoriaPalette.Get(EffectiveTheme.Light, "bg");
        var black = Color.FromRgb(0x00, 0x00, 0x00);
        for (var i = 0; i < LanePalette.Size; i++)
        {
            var light = LanePalette.ColorFor(i, EffectiveTheme.Light);
            Assert.True(ContrastRatio(light, lightBg) >= 1.5,
                $"light lane {i} ({light.ToHex()}) contrast {ContrastRatio(light, lightBg):F2} < 1.5 on the light bg");

            var hc = LanePalette.ColorFor(i, EffectiveTheme.HighContrast);
            Assert.True(ContrastRatio(hc, black) >= 4.5,
                $"HC lane {i} ({hc.ToHex()}) contrast {ContrastRatio(hc, black):F2} < 4.5 on black");
        }
    }

    // the light theme is one pure-white field: graph, panels, popups, inputs and the terminal.
    [Fact]
    public void Light_background_tokens_are_white()
    {
        var white = Rgb.Parse("#FFFFFF");
        foreach (var token in new[] { "bg", "chrome", "surface", "surface-2", "term-bg" })
            Assert.Equal(white, HistoriaPalette.Get(EffectiveTheme.Light, token));
    }

    [Fact]
    public void Light_text_stays_readable_on_white()
    {
        var bg = HistoriaPalette.Get(EffectiveTheme.Light, "bg");
        Assert.True(ContrastRatio(HistoriaPalette.Get(EffectiveTheme.Light, "text"), bg) >= 7.0);
        Assert.True(ContrastRatio(HistoriaPalette.Get(EffectiveTheme.Light, "text-faint"), bg) >= 4.0);
    }

    private static double SrgbToLinear(double c)
    {
        c /= 255.0;
        return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }

    private static double RelativeLuminance(Color c) =>
        0.2126 * SrgbToLinear(c.R) + 0.7152 * SrgbToLinear(c.G) + 0.0722 * SrgbToLinear(c.B);

    private static double ContrastRatio(Color a, Color b)
    {
        var la = RelativeLuminance(a);
        var lb = RelativeLuminance(b);
        var (hi, lo) = la >= lb ? (la, lb) : (lb, la);
        return (hi + 0.05) / (lo + 0.05);
    }

    // Cheap CIE76 ΔE: convert sRGB -> XYZ (D65) -> L*a*b*, Euclidean distance.
    private static double DeltaE76(Color a, Color b)
    {
        var (l1, a1, b1) = ToLab(a);
        var (l2, a2, b2) = ToLab(b);
        return Math.Sqrt((l1 - l2) * (l1 - l2) + (a1 - a2) * (a1 - a2) + (b1 - b2) * (b1 - b2));
    }

    private static (double L, double A, double B) ToLab(Color c)
    {
        var r = SrgbToLinear(c.R);
        var g = SrgbToLinear(c.G);
        var bl = SrgbToLinear(c.B);

        var x = (r * 0.4124 + g * 0.3576 + bl * 0.1805) / 0.95047;
        var y = r * 0.2126 + g * 0.7152 + bl * 0.0722;
        var z = (r * 0.0193 + g * 0.1192 + bl * 0.9505) / 1.08883;

        double F(double t) => t > 0.008856 ? Math.Cbrt(t) : (7.787 * t) + (16.0 / 116.0);
        var fx = F(x);
        var fy = F(y);
        var fz = F(z);

        return ((116.0 * fy) - 16.0, 500.0 * (fx - fy), 200.0 * (fy - fz));
    }
}
