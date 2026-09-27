namespace Historia.Services.Graph;

/// <summary>
/// pure lane-colour resolution for the commit-graph render pass. The Avalonia
/// <c>Pen</c> / <c>SolidColorBrush</c> pools stay in <c>CommitGraphControl</c> (they cache
/// framework types); only the colour maths lives here so it is unit-testable.
/// Mirrors <see cref="LanePalette.ColorFor"/>'s past-one-cycle
/// luminance shift, applied over the theme service's resolved lane list.
/// </summary>
public static class GraphLanePalette
{
    /// <summary>
    /// The resolved lane colour for <paramref name="colorIndex"/>: an entry of
    /// <paramref name="laneColors"/> (the built-in rainbow or the active custom overlay) with
    /// the cycle-darkening shift for indices past one palette cycle. Falls back to
    /// <see cref="LanePalette.ColorFor"/> when <paramref name="laneColors"/> is empty.
    /// <c>EdgeColorFor</c> is the identity, so this covers both the fill and the stroke.
    /// </summary>
    public static Rgb LaneRgb(int colorIndex, IReadOnlyList<Rgb> laneColors, EffectiveTheme mode)
    {
        var n = laneColors.Count;
        if (n == 0)
            return LanePalette.ColorFor(colorIndex, mode);

        var i = ((colorIndex % n) + n) % n;
        var c = laneColors[i];
        var cycle = colorIndex / n;
        if (cycle <= 0)
            return c;

        var factor = 1.0 - Math.Min(0.5, cycle * 0.18);
        return Rgb.FromRgb((byte)(c.R * factor), (byte)(c.G * factor), (byte)(c.B * factor));
    }

    /// <summary>the badge is a solid opaque lane-colour fill, so its label/icon ink
    /// must contrast against that hue — near-black when the lane colour is light enough,
    /// near-white otherwise. Threshold at 0.55 relative luminance.</summary>
    public static bool PrefersDarkInk(Rgb laneColor) => laneColor.RelativeLuminance() > 0.55;
}
