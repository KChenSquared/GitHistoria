
namespace Historia.Theming;

/// <summary>Core hands the App <see cref="Rgb"/>; the framework wants
/// <see cref="Color"/>. One conversion so call sites don't spell out the channel order.</summary>
public static class RgbInterop
{
    public static Color ToAvalonia(this Rgb c) => Color.FromArgb(c.A, c.R, c.G, c.B);
}
