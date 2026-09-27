
namespace Historia.Services.Graph;

/// <summary>
/// Deterministic lane color palette. The layout engine computes color <i>indices</i>;
/// this maps an index to a concrete <see cref="Rgb"/>.
/// </summary>
/// <remarks>
/// One table per theme: a nine-colour rainbow
/// (<c>docs/9-color-rainbow-palette.png</c>) for the dark/System-dark theme, plus a
/// hand-picked darker, still mutually-distinct nine-colour set for the light theme and a
/// lifted set for high contrast. No two lanes share a colour <i>within</i> a theme
/// (asserted by <c>PaletteParityTests</c> with a CIE76 ΔE floor). This is the graph layer's
/// palette only — <see cref="Historia.Services.Theming.HistoriaPalette"/> still owns the theme tokens.
/// These are "the available colors" for the <b>solid</b> lane lines and the branch/tag pill
/// borders, so <see cref="EdgeColorFor"/> is the identity. Backgrounds (the lane-square
/// fill, the pill fills) derive a translucent tint from <see cref="ColorFor"/>. Each renumber
/// (here 10 -> 9) breaks the colour-index stability guarantee — existing repos re-seed
/// their lane colours on next open.
/// </remarks>
public static class LanePalette
{
    public const int Size = 9;

    // Dark theme: the 9-colour rainbow, image verbatim (docs/9-color-rainbow-palette.png).
    private static readonly Rgb[] RainbowDark =
    [
        Rgb.FromRgb(0xFF, 0x00, 0x00), // Red
        Rgb.FromRgb(0xFF, 0x7F, 0x00), // Orange
        Rgb.FromRgb(0xFF, 0xFF, 0x00), // Yellow
        Rgb.FromRgb(0x00, 0xFF, 0x00), // Green
        Rgb.FromRgb(0x00, 0xCE, 0xD1), // Turquoise
        Rgb.FromRgb(0x00, 0x00, 0xFF), // Blue
        Rgb.FromRgb(0x4B, 0x00, 0x82), // Indigo
        Rgb.FromRgb(0x94, 0x00, 0xD3), // Violet
        Rgb.FromRgb(0xFF, 0x14, 0x93), // Pink
    ];

    // Light theme: each hue keeps its rainbow identity but is dark enough to read as a
    // 2 DIP line on #FFFFFF, and all nine are mutually distinct (ΔE >= 12).
    private static readonly Rgb[] RainbowLight =
    [
        Rgb.FromRgb(0xD9, 0x1E, 0x1E), // Red
        Rgb.FromRgb(0xC2, 0x56, 0x0A), // Orange — burnt, separated from gold
        Rgb.FromRgb(0x8A, 0x7A, 0x00), // Yellow — dark gold; pure yellow cannot render on white
        Rgb.FromRgb(0x1E, 0x8E, 0x2F), // Green
        Rgb.FromRgb(0x0A, 0x7C, 0x8C), // Turquoise — teal, separated from green and blue
        Rgb.FromRgb(0x1A, 0x38, 0xD8), // Blue
        Rgb.FromRgb(0x3B, 0x0A, 0x8C), // Indigo
        Rgb.FromRgb(0x93, 0x17, 0xC4), // Violet — bluer->redder, brighter than indigo
        Rgb.FromRgb(0xC7, 0x12, 0x77), // Pink — magenta-pink, separated from red
    ];

    // High contrast: the dark rainbow lifted until every entry clears >= 4.5:1 on #000000.
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
        EffectiveTheme.Light => RainbowLight,
        EffectiveTheme.HighContrast => RainbowHc,
        _ => RainbowDark,
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
    /// palette hues are drawn as the <b>solid</b> line and pill border colour. Kept as a named seam so the
    /// call sites (lane pens, emphasis pens, pill borders, the tick) do not have to change.
    /// </summary>
    public static Rgb EdgeColorFor(int colorIndex, EffectiveTheme theme) => ColorFor(colorIndex, theme);
}
