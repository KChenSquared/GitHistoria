
namespace Historia.Services.Theming;

/// <summary>
/// Pure resolution of <see cref="EffectiveTheme"/> from the user's choice and OS state.
/// Order: High Contrast &gt; System-resolved OS theme &gt; explicit Dark/Light.
/// </summary>
public static class ThemeResolver
{
    public static EffectiveTheme Resolve(ThemeMode mode, bool highContrastOverride, bool osHighContrast, bool osLight)
    {
        if (highContrastOverride || osHighContrast)
            return EffectiveTheme.HighContrast;

        return mode switch
        {
            ThemeMode.Light => EffectiveTheme.Light,
            ThemeMode.Dark => EffectiveTheme.Dark,
            ThemeMode.System => osLight ? EffectiveTheme.Light : EffectiveTheme.Dark,
            _ => EffectiveTheme.Dark,
        };
    }
}
