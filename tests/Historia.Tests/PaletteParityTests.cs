using Historia.Models;
using Historia.Services.Graph;
using Historia.Services.Theming;
using Xunit;
using Color = Historia.Rgb;

namespace Historia.Tests;

/// <summary>
/// §V2-3 / §V2-15.1: <see cref="HistoriaPalette"/> is the single source of truth for every colour
/// the app paints. V4 §split removed <c>wwwroot/app.css</c> (the theme is now generated from
/// this table by <c>AvaloniaThemeApplier</c>), so the CSS-parity check is replaced by a
/// self-consistency check: every theme defines exactly the declared token set, and each hex
/// round-trips.
/// </summary>
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

    [Fact]
    public void RefLabelToken_exists_in_every_native_theme()
    {
        foreach (var theme in new[] { EffectiveTheme.Dark, EffectiveTheme.Light, EffectiveTheme.HighContrast })
            Assert.True(HistoriaPalette.For(theme).ContainsKey("ref-label"));

        Assert.Equal(Color.FromRgb(0xFF, 0xFF, 0xFF), HistoriaPalette.Get(EffectiveTheme.Dark, "ref-label"));
        Assert.Equal(Color.FromRgb(0x1F, 0x1F, 0x1F), HistoriaPalette.Get(EffectiveTheme.Light, "ref-label"));
    }

    // §V3-B correction: the dark/HC white-lift is withdrawn — the palette hues are the SOLID
    // line and pill-border colour, so EdgeColorFor is the identity in every theme.
    [Theory]
    [InlineData(EffectiveTheme.Dark)]
    [InlineData(EffectiveTheme.Light)]
    [InlineData(EffectiveTheme.HighContrast)]
    public void EdgeColorFor_is_identity_in_every_theme(EffectiveTheme theme)
    {
        for (var i = 0; i < LanePalette.Size; i++)
            Assert.Equal(LanePalette.ColorFor(i, theme), LanePalette.EdgeColorFor(i, theme));
    }

    // §V6-G6: the dark theme is the 9-colour rainbow (docs/9-color-rainbow-palette.png) verbatim.
    [Fact]
    public void RainbowDark_matches_the_palette_image()
    {
        Assert.Equal(9, LanePalette.Size);
        Assert.Equal(Color.FromRgb(0xFF, 0x00, 0x00), LanePalette.ColorFor(0, EffectiveTheme.Dark)); // Red
        Assert.Equal(Color.FromRgb(0xFF, 0x7F, 0x00), LanePalette.ColorFor(1, EffectiveTheme.Dark)); // Orange
        Assert.Equal(Color.FromRgb(0xFF, 0xFF, 0x00), LanePalette.ColorFor(2, EffectiveTheme.Dark)); // Yellow
        Assert.Equal(Color.FromRgb(0x00, 0xFF, 0x00), LanePalette.ColorFor(3, EffectiveTheme.Dark)); // Green
        Assert.Equal(Color.FromRgb(0x00, 0xCE, 0xD1), LanePalette.ColorFor(4, EffectiveTheme.Dark)); // Turquoise
        Assert.Equal(Color.FromRgb(0x00, 0x00, 0xFF), LanePalette.ColorFor(5, EffectiveTheme.Dark)); // Blue
        Assert.Equal(Color.FromRgb(0x4B, 0x00, 0x82), LanePalette.ColorFor(6, EffectiveTheme.Dark)); // Indigo
        Assert.Equal(Color.FromRgb(0x94, 0x00, 0xD3), LanePalette.ColorFor(7, EffectiveTheme.Dark)); // Violet
        Assert.Equal(Color.FromRgb(0xFF, 0x14, 0x93), LanePalette.ColorFor(8, EffectiveTheme.Dark)); // Pink
    }

    // §V6-G6: no two lanes may share a colour within a theme; keep them perceptually apart.
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

    // §V6-G6 / M1.2: a 2 DIP line of each light-theme lane must clear ~3:1 on white; each
    // high-contrast lane must clear WCAG AA (4.5:1) on black.
    [Fact]
    public void Light_and_hc_lane_lines_meet_contrast()
    {
        var white = Color.FromRgb(0xFF, 0xFF, 0xFF);
        var black = Color.FromRgb(0x00, 0x00, 0x00);
        for (var i = 0; i < LanePalette.Size; i++)
        {
            var light = LanePalette.ColorFor(i, EffectiveTheme.Light);
            Assert.True(ContrastRatio(light, white) >= 3.0,
                $"light lane {i} ({light.ToHex()}) contrast {ContrastRatio(light, white):F2} < 3.0 on white");

            var hc = LanePalette.ColorFor(i, EffectiveTheme.HighContrast);
            Assert.True(ContrastRatio(hc, black) >= 4.5,
                $"HC lane {i} ({hc.ToHex()}) contrast {ContrastRatio(hc, black):F2} < 4.5 on black");
        }
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
