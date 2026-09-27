namespace Historia;

/// <summary>
/// An 8-bit-per-channel ARGB colour. <see cref="Historia.Core"/> owns no UI framework,
/// so <see cref="Services.Theming.HistoriaPalette"/> and <see cref="Services.Graph.LanePalette"/>
/// produce these instead of <c>System.Windows.Media.Color</c> / <c>Avalonia.Media.Color</c>.
/// The App layer converts to its framework's colour type at the boundary. Channel order and
/// the <c>FromArgb</c> / <c>FromRgb</c> factory names mirror the WPF/Avalonia API so call sites
/// were a one-word change.
/// </summary>
public readonly record struct Rgb(byte A, byte R, byte G, byte B)
{
    public static Rgb FromArgb(byte a, byte r, byte g, byte b) => new(a, r, g, b);

    public static Rgb FromRgb(byte r, byte g, byte b) => new(0xFF, r, g, b);

    /// <summary>Parses <c>#RRGGBB</c> or <c>#RRGGBBAA</c> (CSS channel order).</summary>
    public static Rgb Parse(string hex)
    {
        var s = hex.AsSpan().TrimStart('#');
        byte r = byte.Parse(s[..2], NumberStyles.HexNumber);
        byte g = byte.Parse(s[2..4], NumberStyles.HexNumber);
        byte b = byte.Parse(s[4..6], NumberStyles.HexNumber);
        byte a = s.Length >= 8 ? byte.Parse(s[6..8], NumberStyles.HexNumber) : (byte)0xFF;
        return new Rgb(a, r, g, b);
    }

    /// <summary><c>#rrggbb</c>, or <c>#rrggbbaa</c> when not fully opaque.</summary>
    public string ToHex() => A == 0xFF ? $"#{R:x2}{G:x2}{B:x2}" : $"#{R:x2}{G:x2}{B:x2}{A:x2}";

    /// <summary>WCAG relative luminance (0 = black, 1 = white). Alpha is ignored.</summary>
    public double RelativeLuminance()
        => 0.2126 * Linearize(R) + 0.7152 * Linearize(G) + 0.0722 * Linearize(B);

    /// <summary>WCAG contrast ratio between two colours, from 1:1 to 21:1.</summary>
    public static double ContrastRatio(Rgb a, Rgb b)
    {
        double la = a.RelativeLuminance(), lb = b.RelativeLuminance();
        var (hi, lo) = la >= lb ? (la, lb) : (lb, la);
        return (hi + 0.05) / (lo + 0.05);
    }

    private static double Linearize(byte channel)
    {
        var s = channel / 255.0;
        return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
    }
}
