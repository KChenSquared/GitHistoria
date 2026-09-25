namespace HistoriaApp.Tests;

public class PaletteBrushesTests
{
    private sealed class StubThemeService : IThemeService
    {
        public EffectiveTheme Effective { get; set; } = EffectiveTheme.Dark;
        public ThemeMode CurrentMode => ThemeMode.Dark;
        public bool HighContrastEnabled => false;
        public string EffectiveCssThemeName => "dark";
        public IReadOnlyDictionary<string, Rgb> Palette => HistoriaPalette.For(Effective);
        public IReadOnlyList<Rgb> LaneColors => [];

#pragma warning disable CS0067
        public event EventHandler<ThemeChangedEventArgs>? ThemeChanged;
#pragma warning restore CS0067

        public Task ApplyModeAsync(ThemeMode mode, CancellationToken ct = default) => Task.CompletedTask;
        public Task ApplyCustomThemeAsync(string themeId, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetHighContrastAsync(bool enabled, CancellationToken ct = default) => Task.CompletedTask;
    }

    [AvaloniaFact]
    public void Get_returns_applied_palette_brush()
    {
        var theme = new StubThemeService { Effective = EffectiveTheme.Dark };
        using var applier = new AvaloniaThemeApplier(theme);

        var brush = PaletteBrushes.Get("ColorDiffAddBg");
        Assert.NotNull(brush);
        Assert.NotEqual(Brushes.Transparent, brush);

        var expectedColor = HistoriaPalette.Get(EffectiveTheme.Dark, "diff-add-bg").ToAvalonia();
        Assert.IsAssignableFrom<ISolidColorBrush>(brush);
        Assert.Equal(expectedColor, ((ISolidColorBrush)brush).Color);
    }

    [AvaloniaFact]
    public void Invalidate_after_hc_switch_returns_new_brush_and_raises_changed()
    {
        var theme = new StubThemeService { Effective = EffectiveTheme.Dark };
        using var applier = new AvaloniaThemeApplier(theme);

        var darkBrush = PaletteBrushes.Get("ColorBg");
        var darkColor = ((ISolidColorBrush)darkBrush).Color;

        bool changedRaised = false;
        EventHandler handler = (_, _) => changedRaised = true;
        PaletteBrushes.Changed += handler;

        try
        {
            theme.Effective = EffectiveTheme.HighContrast;
            applier.Apply(EffectiveTheme.HighContrast);

            Assert.True(changedRaised);

            var hcBrush = PaletteBrushes.Get("ColorBg");
            var hcColor = ((ISolidColorBrush)hcBrush).Color;

            var expectedHc = HistoriaPalette.Get(EffectiveTheme.HighContrast, "bg").ToAvalonia();
            Assert.Equal(expectedHc, hcColor);
            Assert.NotEqual(darkColor, hcColor);
        }
        finally
        {
            PaletteBrushes.Changed -= handler;
            // Restore dark
            theme.Effective = EffectiveTheme.Dark;
            applier.Apply(EffectiveTheme.Dark);
        }
    }
}
