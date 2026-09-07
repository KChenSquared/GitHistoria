
namespace Historia.Services.Theming;

/// <summary>
/// Pure resolution of <see cref="EffectiveTheme"/> from the user's choice and OS state (§11.6).
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
            // §V6-G9: ThemeService translates Custom to its Dark/Light base before calling this;
            // a bare Custom here (e.g. from a test) falls back to Dark.
            ThemeMode.Custom => EffectiveTheme.Dark,
            _ => EffectiveTheme.Dark,
        };
    }
}
