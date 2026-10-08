namespace HistoriaApp.Tests;

[Trait("tier", "ui")]
public sealed class ScrollBarThemeTests
{
    private sealed class StubThemeService : IThemeService
    {
        public EffectiveTheme Effective { get; set; } = EffectiveTheme.Dark;
        public ThemeMode CurrentMode => ThemeMode.Dark;
        public bool HighContrastEnabled => false;
        public string EffectiveCssThemeName => Effective == EffectiveTheme.Dark ? "dark" : "light";
        public IReadOnlyDictionary<string, Rgb> Palette => HistoriaPalette.For(Effective);
        public IReadOnlyList<Rgb> LaneColors => [];

#pragma warning disable CS0067
        public event EventHandler<ThemeChangedEventArgs>? ThemeChanged;
#pragma warning restore CS0067

        public Task ApplyModeAsync(ThemeMode mode, CancellationToken ct = default) => Task.CompletedTask;
        public bool CustomThemeActive => false;
        public Task SetCustomThemeEnabledAsync(bool enabled, CancellationToken ct = default) => Task.CompletedTask;
        public Task SelectCustomThemeAsync(string themeId, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetHighContrastAsync(bool enabled, CancellationToken ct = default) => Task.CompletedTask;
    }

    private static void EnsureAppStylesLoaded()
    {
        if (Application.Current is { } app && !app.Resources.ContainsKey(typeof(ScrollBar)))
        {
            var appType = typeof(Historia.Theming.ScrollBarMetrics).Assembly.GetType("Historia.App")!;
            var historiaApp = (Application)Activator.CreateInstance(appType)!;
            historiaApp.Initialize();
            foreach (var key in historiaApp.Resources.Keys)
            {
                app.Resources[key] = historiaApp.Resources[key];
            }
            var styles = historiaApp.Styles.ToList();
            historiaApp.Styles.Clear();
            foreach (var style in styles)
            {
                app.Styles.Add(style);
            }
        }
    }

    [AvaloniaFact]
    public void Vertical_bar_is_a_lane_with_a_pill_thumb()
    {
        EnsureAppStylesLoaded();
        var theme = new StubThemeService { Effective = EffectiveTheme.Dark };
        using var applier = new AvaloniaThemeApplier(theme);
        applier.Apply(EffectiveTheme.Dark);

        var sv = new ScrollViewer
        {
            Width = 200,
            Height = 200,
            Content = new Border { Width = 150, Height = 1000 }
        };
        var window = new Window { Width = 400, Height = 400, Content = sv };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            var bar = sv.GetVisualDescendants().OfType<ScrollBar>().Single(b => b.Orientation == Orientation.Vertical);
            Assert.True(bar.IsVisible);
            Assert.Equal(ScrollBarMetrics.Lane, bar.Bounds.Width);

            var thumb = bar.GetVisualDescendants().OfType<Thumb>().Single();
            Assert.Equal(ScrollBarMetrics.Thumb, thumb.Bounds.Width);
            Assert.Equal(new CornerRadius(ScrollBarMetrics.Thumb / 2), thumb.CornerRadius);

            var lineButtons = bar.GetVisualDescendants().OfType<RepeatButton>()
                .Where(b => b.Name is "PART_LineUpButton" or "PART_LineDownButton")
                .ToList();
            Assert.Empty(lineButtons);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Horizontal_bar_matches()
    {
        EnsureAppStylesLoaded();
        var theme = new StubThemeService { Effective = EffectiveTheme.Dark };
        using var applier = new AvaloniaThemeApplier(theme);
        applier.Apply(EffectiveTheme.Dark);

        var sv = new ScrollViewer
        {
            Width = 200,
            Height = 200,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Visible,
            Content = new Border { Width = 1000, Height = 150 }
        };
        var window = new Window { Width = 400, Height = 400, Content = sv };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            var bar = sv.GetVisualDescendants().OfType<ScrollBar>().Single(b => b.Orientation == Orientation.Horizontal);
            Assert.True(bar.IsVisible);
            Assert.Equal(ScrollBarMetrics.Lane, bar.Bounds.Height);

            var thumb = bar.GetVisualDescendants().OfType<Thumb>().Single();
            Assert.Equal(ScrollBarMetrics.Thumb, thumb.Bounds.Height);
            Assert.Equal(new CornerRadius(ScrollBarMetrics.Thumb / 2), thumb.CornerRadius);

            var lineButtons = bar.GetVisualDescendants().OfType<RepeatButton>()
                .Where(b => b.Name is "PART_LineLeftButton" or "PART_LineRightButton")
                .ToList();
            Assert.Empty(lineButtons);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void ScrollViewers_reserve_their_lane()
    {
        EnsureAppStylesLoaded();
        var theme = new StubThemeService { Effective = EffectiveTheme.Dark };
        using var applier = new AvaloniaThemeApplier(theme);
        applier.Apply(EffectiveTheme.Dark);

        var sv = new ScrollViewer
        {
            Width = 200,
            Height = 200,
            Content = new Border { Width = 180, Height = 1000 }
        };
        var window = new Window { Width = 400, Height = 400, Content = sv };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            var bar = sv.GetVisualDescendants().OfType<ScrollBar>().Single(b => b.Orientation == Orientation.Vertical);
            var presenter = sv.GetVisualDescendants().OfType<Control>().Single(c => c.Name == "PART_ContentPresenter");
            Assert.True(bar.IsVisible);

            Assert.True(
                presenter.Bounds.Right <= bar.Bounds.Left,
                $"Presenter right {presenter.Bounds.Right} overlaps scrollbar left {bar.Bounds.Left}");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Thumb_recolours_on_theme_switch()
    {
        EnsureAppStylesLoaded();
        var theme = new StubThemeService { Effective = EffectiveTheme.Dark };
        using var applier = new AvaloniaThemeApplier(theme);
        applier.Apply(EffectiveTheme.Dark);

        var sv = new ScrollViewer
        {
            Width = 200,
            Height = 200,
            Content = new Border { Width = 150, Height = 1000 }
        };
        var window = new Window { Width = 400, Height = 400, Content = sv };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            var bar = sv.GetVisualDescendants().OfType<ScrollBar>().Single(b => b.Orientation == Orientation.Vertical);
            var thumb = bar.GetVisualDescendants().OfType<Thumb>().Single();

            var darkBrush = Assert.IsAssignableFrom<ISolidColorBrush>(thumb.Background);
            var expectedDark = HistoriaPalette.Get(EffectiveTheme.Dark, "scrollbar").ToAvalonia();
            Assert.Equal(expectedDark, darkBrush.Color);

            theme.Effective = EffectiveTheme.Light;
            applier.Apply(EffectiveTheme.Light);
            Dispatcher.UIThread.RunJobs();

            var lightBrush = Assert.IsAssignableFrom<ISolidColorBrush>(thumb.Background);
            var expectedLight = HistoriaPalette.Get(EffectiveTheme.Light, "scrollbar").ToAvalonia();
            Assert.Equal(expectedLight, lightBrush.Color);
            Assert.NotEqual(expectedDark, expectedLight);
        }
        finally
        {
            window.Close();
        }
    }
}
