namespace HistoriaApp.Tests;

/// <summary>
/// The conflict resolver's minimap and line-number margin keep their drawing across frames:
/// bucketed region bars redrawn only when the parse changes, and shaped line numbers. Redrawing
/// every region per scroll step or re-shaping every number per frame produces no error, only
/// slower scrolling, so the render counts and cache misses are asserted here.
/// </summary>
[Trait("tier", "ui")]
public sealed class ConflictPaneRenderTests
{
    private const double MinimapHeight = 300;

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
        public bool CustomThemeActive => false;
        public Task SetCustomThemeEnabledAsync(bool enabled, CancellationToken ct = default) => Task.CompletedTask;
        public Task SelectCustomThemeAsync(string themeId, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetHighContrastAsync(bool enabled, CancellationToken ct = default) => Task.CompletedTask;
    }

    /// <summary>A file with <paramref name="conflicts"/> conflict blocks separated by context.</summary>
    private static ConflictParse ManyConflicts(int conflicts)
    {
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < conflicts; i++)
        {
            sb.Append("context ").Append(i).Append('\n');
            sb.Append("<<<<<<< ours\n");
            sb.Append("ours ").Append(i).Append('\n');
            sb.Append("=======\n");
            sb.Append("theirs ").Append(i).Append('\n');
            sb.Append(">>>>>>> theirs\n");
        }
        return ConflictMarkerParser.Parse(sb.ToString());
    }

    private static void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private static (Window Window, ConflictResolverMinimap Minimap) ShowMinimap(ConflictParse parse)
    {
        var minimap = new ConflictResolverMinimap { Parse = parse, Width = 12, Height = MinimapHeight };
        var window = new Window { Width = 200, Height = MinimapHeight + 100, Content = minimap };
        window.Show();
        Pump();
        return (window, minimap);
    }

    [AvaloniaFact]
    public void Hundreds_of_regions_draw_at_most_one_bar_per_bucket_row()
    {
        var parse = ManyConflicts(500);
        Assert.Equal(500, parse.Regions.Count);
        var (window, minimap) = ShowMinimap(parse);
        try
        {
            Assert.True(minimap.BarsRenderCountForTesting >= 1, "the bars layer must have rendered");
            Assert.InRange(minimap.BarRectCountForTesting, 1, (int)MinimapHeight);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Display_range_changes_do_not_redraw_the_bars()
    {
        var (window, minimap) = ShowMinimap(ManyConflicts(500));
        try
        {
            var bars = minimap.BarsRenderCountForTesting;
            var ranges = minimap.RangeRenderCountForTesting;

            for (var i = 0; i < 50; i++)
            {
                minimap.DisplayRange = (i * 20, i * 20 + 40);
                Pump();
            }

            Assert.Equal(bars, minimap.BarsRenderCountForTesting);
            Assert.True(minimap.RangeRenderCountForTesting >= ranges + 50, "each range change redraws the range box");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Setting_an_unchanged_value_invalidates_nothing()
    {
        var parse = ManyConflicts(20);
        var (window, minimap) = ShowMinimap(parse);
        try
        {
            minimap.DisplayRange = (10, 40);
            Pump();
            var bars = minimap.BarsRenderCountForTesting;
            var ranges = minimap.RangeRenderCountForTesting;

            minimap.DisplayRange = (10, 40);
            minimap.Parse = parse;
            Pump();

            Assert.Equal(bars, minimap.BarsRenderCountForTesting);
            Assert.Equal(ranges, minimap.RangeRenderCountForTesting);

            minimap.Parse = ManyConflicts(21);
            Pump();
            Assert.Equal(bars + 1, minimap.BarsRenderCountForTesting);
        }
        finally
        {
            window.Close();
        }
    }

    private static (Window Window, TextEditor Editor, ConflictLineNumberMargin Margin) ShowMargin()
    {
        var editor = new TextEditor
        {
            Document = new TextDocument(string.Join('\n', Enumerable.Range(1, 5_000).Select(i => $"line {i}"))),
        };
        var margin = new ConflictLineNumberMargin(editor);
        editor.TextArea.LeftMargins.Insert(0, margin);
        var window = new Window { Width = 600, Height = 500, Content = editor };
        window.Show();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        editor.TextArea.TextView.EnsureVisualLines();
        return (window, editor, margin);
    }

    private static void RenderMargin(Control margin)
    {
        var size = new PixelSize(Math.Max(1, (int)margin.Bounds.Width), Math.Max(1, (int)margin.Bounds.Height));
        using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(size);
        using var dc = bitmap.CreateDrawingContext();
        margin.Render(dc);
    }

    [AvaloniaFact]
    public void The_margins_repeat_frame_shapes_nothing()
    {
        var (window, editor, margin) = ShowMargin();
        try
        {
            Assert.True(editor.TextArea.TextView.VisualLines.Count > 10);
            RenderMargin(margin);
            Assert.True(margin.TextCacheForTesting.Misses > 0, "the first frame must shape the visible numbers");

            margin.TextCacheForTesting.ResetCounters();
            RenderMargin(margin);

            Assert.Equal(0, margin.TextCacheForTesting.Misses);
            Assert.True(margin.TextCacheForTesting.Hits > 10);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void A_palette_change_clears_the_margins_cache()
    {
        var theme = new StubThemeService { Effective = EffectiveTheme.Dark };
        using var applier = new AvaloniaThemeApplier(theme);
        var (window, editor, margin) = ShowMargin();
        try
        {
            RenderMargin(margin);
            Assert.True(margin.TextCacheForTesting.Count > 0);

            theme.Effective = EffectiveTheme.Light;
            applier.Apply(EffectiveTheme.Light);
            Assert.Equal(0, margin.TextCacheForTesting.Count);

            editor.TextArea.TextView.EnsureVisualLines();
            RenderMargin(margin);
            margin.TextCacheForTesting.ResetCounters();
            var face = new Typeface(editor.FontFamily, editor.FontStyle, editor.FontWeight);
            margin.TextCacheForTesting.Get("1", face, editor.FontSize, PaletteBrushes.Get("ColorTextFaint"));
            Assert.Equal(1, margin.TextCacheForTesting.Hits);
        }
        finally
        {
            window.Close();
        }
    }
}
