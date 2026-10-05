namespace Historia.Tests;

/// <summary>With no custom theme rendering, the resolution seam
/// (<see cref="IThemeService.Palette"/> / <see cref="IThemeService.LaneColors"/>) is
/// byte-identical to the built-in tables. A custom theme overrides the Light / Dark / System
/// choice only while the override is on, its id resolves and High Contrast is off; it overlays
/// the palette and refreshes even when the effective base is unchanged, and the Light / Dark /
/// System choice itself never changes underneath it.</summary>
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

    private static ThemeService Make(AppSettings settings, out CustomThemeService themes, out FakeSettingsService cfg)
    {
        themes = new CustomThemeService();
        cfg = new FakeSettingsService(settings);
        return new ThemeService(cfg, new FixedProbe(), themes);
    }

    private static Task SaveDarkThemeAsync(CustomThemeService themes, string id = "c", string name = "C") =>
        themes.SaveAsync(new CustomTheme
        {
            Id = id, Name = name, Base = "Dark",
            Tokens = new Dictionary<string, string> { ["bg"] = "#101820" },
            Lane = ["#ABCDEF"],
        });

    private static void AssertBuiltin(IThemeService svc, EffectiveTheme effective)
    {
        Assert.Equal(effective, svc.Effective);
        foreach (var token in HistoriaPalette.Tokens)
            Assert.Equal(HistoriaPalette.Get(effective, token), svc.Palette[token]);

        Assert.Equal(LanePalette.Size, svc.LaneColors.Count);
        for (var i = 0; i < LanePalette.Size; i++)
            Assert.Equal(LanePalette.ColorFor(i, effective), svc.LaneColors[i]);
    }

    [Theory]
    [InlineData(ThemeMode.Dark, false, EffectiveTheme.Dark)]
    [InlineData(ThemeMode.Light, false, EffectiveTheme.Light)]
    [InlineData(ThemeMode.Dark, true, EffectiveTheme.HighContrast)]
    public void Builtin_modes_resolve_to_the_builtin_tables(ThemeMode mode, bool hc, EffectiveTheme effective)
    {
        var svc = Make(new AppSettings { AppTheme = mode, HighContrastOverride = hc }, out _, out _);

        Assert.False(svc.CustomThemeActive);
        AssertBuiltin(svc, effective);
    }

    [Fact]
    public async Task Enabled_custom_theme_overlays_the_palette_and_lanes()
    {
        var svc = Make(new AppSettings { AppTheme = ThemeMode.Dark }, out var themes, out var cfg);
        await SaveDarkThemeAsync(themes);
        await svc.SelectCustomThemeAsync("c");

        var raised = 0;
        svc.ThemeChanged += (_, _) => raised++;

        await svc.SetCustomThemeEnabledAsync(true);

        Assert.True(cfg.Current.UseCustomTheme);
        Assert.Equal("c", cfg.Current.CustomThemeId);
        Assert.Equal(ThemeMode.Dark, svc.CurrentMode);              // the base choice is untouched
        Assert.True(svc.CustomThemeActive);
        Assert.Equal(EffectiveTheme.Dark, svc.Effective);           // base is Dark
        Assert.Equal(Rgb.Parse("#101820"), svc.Palette["bg"]);      // overlaid
        Assert.Equal(Rgb.Parse("#ABCDEF"), svc.LaneColors[0]);      // overlaid
        Assert.Equal(LanePalette.ColorFor(1, EffectiveTheme.Dark), svc.LaneColors[1]); // inherited
        Assert.True(raised >= 1, "turning a custom theme on must raise ThemeChanged even when the base is unchanged");
    }

    [Fact]
    public async Task Selected_but_disabled_custom_theme_does_not_render()
    {
        var svc = Make(new AppSettings { AppTheme = ThemeMode.Light }, out var themes, out var cfg);
        await SaveDarkThemeAsync(themes);

        var raised = 0;
        svc.ThemeChanged += (_, _) => raised++;

        await svc.SelectCustomThemeAsync("c");

        Assert.Equal("c", cfg.Current.CustomThemeId);
        Assert.False(cfg.Current.UseCustomTheme);
        Assert.Equal(ThemeMode.Light, svc.CurrentMode);
        Assert.False(svc.CustomThemeActive);
        AssertBuiltin(svc, EffectiveTheme.Light);
        Assert.Equal(0, raised);
    }

    [Fact]
    public async Task Disabling_reverts_to_the_base_mode()
    {
        var svc = Make(new AppSettings { AppTheme = ThemeMode.Light }, out var themes, out var cfg);
        await SaveDarkThemeAsync(themes);
        await svc.SelectCustomThemeAsync("c");
        await svc.SetCustomThemeEnabledAsync(true);
        Assert.Equal(EffectiveTheme.Dark, svc.Effective); // the custom theme's Dark base renders
        Assert.Equal(ThemeMode.Light, svc.CurrentMode);

        var changes = new List<ThemeChangedEventArgs>();
        svc.ThemeChanged += (_, e) => changes.Add(e);

        await svc.SetCustomThemeEnabledAsync(false);

        Assert.False(cfg.Current.UseCustomTheme);
        Assert.Equal("c", cfg.Current.CustomThemeId); // kept, so turning it back on restores it
        Assert.False(svc.CustomThemeActive);
        Assert.Equal(ThemeMode.Light, svc.CurrentMode);
        AssertBuiltin(svc, EffectiveTheme.Light);
        var change = Assert.Single(changes);
        Assert.Equal(EffectiveTheme.Dark, change.OldTheme);
        Assert.Equal(EffectiveTheme.Light, change.NewTheme);
    }

    [Fact]
    public async Task Enabling_with_an_unresolvable_id_selects_the_first_saved_theme()
    {
        var svc = Make(new AppSettings { AppTheme = ThemeMode.Dark, CustomThemeId = "gone" }, out var themes, out var cfg);
        await SaveDarkThemeAsync(themes, id: "b", name: "Beta");
        await SaveDarkThemeAsync(themes, id: "a", name: "Alpha"); // first by name

        await svc.SetCustomThemeEnabledAsync(true);

        Assert.True(cfg.Current.UseCustomTheme);
        Assert.Equal("a", cfg.Current.CustomThemeId);
        Assert.True(svc.CustomThemeActive);
        Assert.Equal(Rgb.Parse("#101820"), svc.Palette["bg"]);
    }

    [Fact]
    public async Task Enabling_with_no_saved_themes_renders_the_base_mode()
    {
        var svc = Make(new AppSettings { AppTheme = ThemeMode.Light }, out _, out var cfg);

        await svc.SetCustomThemeEnabledAsync(true);

        Assert.True(cfg.Current.UseCustomTheme);
        Assert.Null(cfg.Current.CustomThemeId);
        Assert.Equal(ThemeMode.Light, svc.CurrentMode);
        Assert.False(svc.CustomThemeActive);
        AssertBuiltin(svc, EffectiveTheme.Light);
    }

    [Fact]
    public async Task High_contrast_still_wins_over_an_enabled_custom_theme()
    {
        var svc = Make(new AppSettings { AppTheme = ThemeMode.Dark }, out var themes, out _);
        await SaveDarkThemeAsync(themes);
        await svc.SelectCustomThemeAsync("c");
        await svc.SetCustomThemeEnabledAsync(true);
        Assert.True(svc.CustomThemeActive);

        await svc.SetHighContrastAsync(true);

        Assert.False(svc.CustomThemeActive);
        Assert.Equal(ThemeMode.Dark, svc.CurrentMode);
        AssertBuiltin(svc, EffectiveTheme.HighContrast);
    }
}
