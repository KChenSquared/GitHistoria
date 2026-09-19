namespace Historia.Tests;

/// <summary>§V6-G9 M7.3: with no custom theme selected the resolution seam
/// (<see cref="IThemeService.Palette"/> / <see cref="IThemeService.LaneColors"/>) is
/// byte-identical to the built-in tables; selecting one overlays it and refreshes even when
/// the effective base is unchanged.</summary>
[Trait("tier", "unit")]
public sealed class ThemeServiceSeamTests : IDisposable
{
    private sealed class FixedProbe : ISystemThemeProbe
    {
        public bool IsLightTheme { get; init; }
        public bool IsHighContrast { get; init; }
        public event EventHandler? Changed { add { } remove { } }
    }

    private readonly StateDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private static ThemeService Make(AppSettings settings, ISystemThemeProbe probe, out CustomThemeService themes)
    {
        themes = new CustomThemeService();
        return new ThemeService(new FakeSettingsService(settings), probe, themes);
    }

    [Theory]
    [InlineData(ThemeMode.Dark, false, EffectiveTheme.Dark)]
    [InlineData(ThemeMode.Light, false, EffectiveTheme.Light)]
    [InlineData(ThemeMode.Dark, true, EffectiveTheme.HighContrast)]
    public void Builtin_modes_resolve_to_the_builtin_tables(ThemeMode mode, bool hc, EffectiveTheme effective)
    {
        var svc = Make(new AppSettings { AppTheme = mode, HighContrastOverride = hc }, new FixedProbe(), out _);

        Assert.Equal(effective, svc.Effective);
        foreach (var token in HistoriaPalette.Tokens)
            Assert.Equal(HistoriaPalette.Get(effective, token), svc.Palette[token]);

        Assert.Equal(LanePalette.Size, svc.LaneColors.Count);
        for (var i = 0; i < LanePalette.Size; i++)
            Assert.Equal(LanePalette.ColorFor(i, effective), svc.LaneColors[i]);
    }

    [Fact]
    public async Task Selecting_a_custom_theme_overlays_the_palette_and_lanes()
    {
        var svc = Make(new AppSettings { AppTheme = ThemeMode.Dark }, new FixedProbe(), out var themes);
        await themes.SaveAsync(new CustomTheme
        {
            Id = "c", Name = "C", Base = "Dark",
            Tokens = new Dictionary<string, string> { ["bg"] = "#101820" },
            Lane = ["#ABCDEF"],
        });

        var raised = 0;
        svc.ThemeChanged += (_, _) => raised++;

        await svc.ApplyCustomThemeAsync("c");

        Assert.Equal(ThemeMode.Custom, svc.CurrentMode);
        Assert.Equal(EffectiveTheme.Dark, svc.Effective);           // base is Dark
        Assert.Equal(Rgb.Parse("#101820"), svc.Palette["bg"]);      // overlaid
        Assert.Equal(Rgb.Parse("#ABCDEF"), svc.LaneColors[0]);      // overlaid
        Assert.Equal(LanePalette.ColorFor(1, EffectiveTheme.Dark), svc.LaneColors[1]); // inherited
        Assert.True(raised >= 1, "switching into a custom theme must raise ThemeChanged");
    }

    [Fact]
    public async Task High_contrast_still_wins_over_a_selected_custom_theme()
    {
        var svc = Make(new AppSettings { AppTheme = ThemeMode.Dark }, new FixedProbe(), out var themes);
        await themes.SaveAsync(new CustomTheme
        {
            Id = "c", Name = "C", Base = "Dark",
            Tokens = new Dictionary<string, string> { ["bg"] = "#101820" },
        });
        await svc.ApplyCustomThemeAsync("c");
        await svc.SetHighContrastAsync(true);

        Assert.Equal(EffectiveTheme.HighContrast, svc.Effective);
        Assert.Equal(HistoriaPalette.Get(EffectiveTheme.HighContrast, "bg"), svc.Palette["bg"]);
    }
}
