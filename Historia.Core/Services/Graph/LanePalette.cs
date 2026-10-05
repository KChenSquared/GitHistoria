
namespace Historia.Services.Graph;

/// <summary>
/// Deterministic lane color palette. The layout engine computes color <i>indices</i>;
/// this maps an index to a concrete <see cref="Rgb"/>.
/// </summary>
/// <remarks>
/// One table per theme. Dark is a neon set drawn on the dark navy field. Light is a retro set
/// on a white field that deliberately includes two pale hues (Tangerine and Goldenrod): their
/// 2 DIP lines are faint, so the translucent lane wash carries their lane identity. High
/// contrast is a rainbow lifted until every hue clears AA on black. No two lanes share a
/// colour <i>within</i> a theme (asserted by <c>PaletteParityTests</c> with a CIE76 ΔE floor).
/// This is the graph layer's palette only — <see cref="Historia.Services.Theming.HistoriaPalette"/>
/// still owns the theme tokens. These are "the available colors" for the <b>solid</b> lane
/// lines and the branch/tag pill borders, so <see cref="EdgeColorFor"/> is the identity.
/// Backgrounds (the lane-square fill, the pill fills) derive a translucent tint from
/// <see cref="ColorFor"/>. Changing <see cref="Size"/> breaks the colour-index stability
/// guarantee — existing repos re-seed their lane colours on next open.
/// </remarks>
public static class LanePalette
{
    public const int Size = 9;

    // Dark theme: Synthwave Sunset — saturated neon hues that glow on the navy field.
    private static readonly Rgb[] SynthwaveDark =
    [
        Rgb.FromRgb(0xE0, 0x9E, 0x1D), // Cyber Yellow
        Rgb.FromRgb(0xFF, 0x5E, 0x00), // Neon Orange
        Rgb.FromRgb(0xFF, 0x00, 0x3C), // Laser Red
        Rgb.FromRgb(0xFF, 0x00, 0xA0), // Synthwave Pink
        Rgb.FromRgb(0xB5, 0x00, 0xFF), // Hyper Magenta
        Rgb.FromRgb(0x5D, 0x00, 0xFF), // Electric Indigo
        Rgb.FromRgb(0x00, 0x88, 0xFF), // Azure Glow
        Rgb.FromRgb(0x00, 0xB5, 0xB5), // Grid Cyan
        Rgb.FromRgb(0x00, 0x8D, 0x64), // Matrix Mint
    ];

    // Light theme: Retro Sunset — muted retro hues on the white field. Tangerine and
    // Goldenrod are intentionally pale; the lane wash keeps them traceable.
    private static readonly Rgb[] RetroLight =
    [
        Rgb.FromRgb(0xD9, 0x04, 0x29), // Vintage Crimson
        Rgb.FromRgb(0xF4, 0xA2, 0x61), // Tangerine
        Rgb.FromRgb(0xE9, 0xC4, 0x6A), // Goldenrod
        Rgb.FromRgb(0x2A, 0x9D, 0x8F), // Muted Teal
        Rgb.FromRgb(0x45, 0x7B, 0x9D), // Steel Blue
        Rgb.FromRgb(0x02, 0x3E, 0x8A), // Ocean Depth
        Rgb.FromRgb(0x72, 0x09, 0xB7), // Plum Purple
        Rgb.FromRgb(0xB5, 0x17, 0x9E), // Vibrant Fuchsia
        Rgb.FromRgb(0xF7, 0x25, 0x85), // Retro Pink
    ];

    // High contrast: a rainbow lifted until every entry clears >= 4.5:1 on #000000.
    private static readonly Rgb[] RainbowHc =
    [
        Rgb.FromRgb(0xFF, 0x55, 0x55), // Red
        Rgb.FromRgb(0xFF, 0x9E, 0x33), // Orange
        Rgb.FromRgb(0xFF, 0xFF, 0x33), // Yellow
        Rgb.FromRgb(0x33, 0xFF, 0x57), // Green
        Rgb.FromRgb(0x2C, 0xD6, 0xD9), // Turquoise
        Rgb.FromRgb(0x6E, 0x9E, 0xFF), // Blue — lifted for AA on black
        Rgb.FromRgb(0x8A, 0x6C, 0xFF), // Indigo — bluer, kept apart from violet
        Rgb.FromRgb(0xE7, 0x7B, 0xFF), // Violet — pinker, kept apart from indigo
        Rgb.FromRgb(0xFF, 0x5C, 0xAE), // Pink
    ];

    private static Rgb[] TableFor(EffectiveTheme theme) => theme switch
    {
        EffectiveTheme.Light => RetroLight,
        EffectiveTheme.HighContrast => RainbowHc,
        _ => SynthwaveDark,
    };

    public static Rgb ColorFor(int colorIndex, EffectiveTheme theme)
    {
        var table = TableFor(theme);
        var i = ((colorIndex % Size) + Size) % Size;
        var baseColor = table[i];

        // Beyond one palette cycle, apply a luminance shift so colors stay distinguishable.
        var cycle = colorIndex / Size;
        if (cycle <= 0)
            return baseColor;

        var factor = 1.0 - Math.Min(0.5, cycle * 0.18);
        return Rgb.FromRgb(
            (byte)(baseColor.R * factor),
            (byte)(baseColor.G * factor),
            (byte)(baseColor.B * factor));
    }

    /// <summary>
    /// The stroke/border variant of a lane colour: the identity in every theme, since the
    /// palette hues are drawn as the <b>solid</b> line and pill border colour. A named seam so the
    /// call sites (lane pens, emphasis pens, pill borders, the tick) ask for the stroke colour
    /// explicitly, separate from the fill.
    /// </summary>
    public static Rgb EdgeColorFor(int colorIndex, EffectiveTheme theme) => ColorFor(colorIndex, theme);
}
