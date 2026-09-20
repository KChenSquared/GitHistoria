namespace Historia.Tests;
[Trait("tier", "unit")]

public class ThemeResolverTests
{
    [Theory]
    [InlineData(ThemeMode.Dark, false, false, false, EffectiveTheme.Dark)]
    [InlineData(ThemeMode.Light, false, false, false, EffectiveTheme.Light)]
    [InlineData(ThemeMode.System, false, false, true, EffectiveTheme.Light)]
    [InlineData(ThemeMode.System, false, false, false, EffectiveTheme.Dark)]
    public void Resolves_mode_against_os(ThemeMode mode, bool hcOverride, bool osHc, bool osLight, EffectiveTheme expected)
        => Assert.Equal(expected, ThemeResolver.Resolve(mode, hcOverride, osHc, osLight));

    [Fact]
    public void High_contrast_override_wins_over_everything()
        => Assert.Equal(EffectiveTheme.HighContrast, ThemeResolver.Resolve(ThemeMode.Light, highContrastOverride: true, osHighContrast: false, osLight: true));

    [Fact]
    public void Os_high_contrast_wins_even_without_the_override()
        => Assert.Equal(EffectiveTheme.HighContrast, ThemeResolver.Resolve(ThemeMode.Dark, highContrastOverride: false, osHighContrast: true, osLight: false));
}
