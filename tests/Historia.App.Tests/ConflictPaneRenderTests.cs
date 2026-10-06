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

    private static (Window Window, TextEditor Editor, ConflictToggleMargin Margin) ShowToggleMargin(Func<int, ConflictToggleGlyph> glyph)
    {
        var editor = new TextEditor
        {
            Document = new TextDocument(string.Join('\n', Enumerable.Range(1, 200).Select(i => $"line {i}"))),
        };
        var margin = new ConflictToggleMargin(editor, glyph);
        editor.TextArea.LeftMargins.Insert(0, margin);
        var window = new Window { Width = 600, Height = 500, Content = editor };
        window.Show();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        editor.TextArea.TextView.EnsureVisualLines();
        return (window, editor, margin);
    }

    private static ConflictToggleGlyph CycleGlyphs(int line) => (line % 4) switch
    {
        0 => ConflictToggleGlyph.None,
        1 => ConflictToggleGlyph.Unchecked,
        2 => ConflictToggleGlyph.Partial,
        _ => ConflictToggleGlyph.Checked,
    };

    [AvaloniaFact]
    public void Toggle_margin_repeat_frame_shapes_nothing()
    {
        var (window, _, margin) = ShowToggleMargin(CycleGlyphs);
        try
        {
            RenderMargin(margin);
            Assert.True(margin.PoolMissesForTesting > 0, "the first frame must build the glyphs");

            margin.ResetPoolCountersForTesting();
            RenderMargin(margin);
            Assert.Equal(0, margin.PoolMissesForTesting);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Toggle_margin_click_raises_line_index()
    {
        var (window, editor, margin) = ShowToggleMargin(line => line == 2 ? ConflictToggleGlyph.Unchecked : ConflictToggleGlyph.None);
        try
        {
            var raised = new List<int>();
            margin.ToggleClicked += (_, line) => raised.Add(line);
            var view = editor.TextArea.TextView;
            double LineMiddle(int lineNumber) => view.GetVisualTopByDocumentLine(lineNumber) - view.VerticalOffset + view.DefaultLineHeight / 2;

            var onLine3 = margin.TranslatePoint(new Point(margin.Bounds.Width / 2, LineMiddle(3)), window)!.Value;
            window.MouseDown(onLine3, MouseButton.Left);
            window.MouseUp(onLine3, MouseButton.Left);
            Assert.Equal(new[] { 2 }, raised);

            var onLine1 = margin.TranslatePoint(new Point(margin.Bounds.Width / 2, LineMiddle(1)), window)!.Value;
            window.MouseDown(onLine1, MouseButton.Left);
            window.MouseUp(onLine1, MouseButton.Left);
            Assert.Equal(new[] { 2 }, raised);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Toggle_margin_palette_change_invalidates()
    {
        var theme = new StubThemeService { Effective = EffectiveTheme.Dark };
        using var applier = new AvaloniaThemeApplier(theme);
        var (window, editor, margin) = ShowToggleMargin(CycleGlyphs);
        try
        {
            RenderMargin(margin);
            margin.ResetPoolCountersForTesting();

            theme.Effective = EffectiveTheme.Light;
            applier.Apply(EffectiveTheme.Light);
            editor.TextArea.TextView.EnsureVisualLines();
            RenderMargin(margin);

            Assert.True(margin.PoolMissesForTesting > 0, "a palette change must re-resolve the pens and brushes");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Excluded_colorizer_marks_only_excluded_lines()
    {
        var editor = new TextEditor { Document = new TextDocument("kept\nstruck\nkept too\n") };
        editor.TextArea.TextView.LineTransformers.Add(new ConflictExcludedLineColorizer(line => line == 1));
        var window = new Window { Width = 400, Height = 300, Content = editor };
        window.Show();
        try
        {
            window.UpdateLayout();
            var view = editor.TextArea.TextView;
            view.EnsureVisualLines();

            TextDecorationCollection? Decorations(int lineNumber)
            {
                var visual = view.GetOrConstructVisualLine(editor.Document.GetLineByNumber(lineNumber));
                return visual.Elements[0].TextRunProperties.TextDecorations;
            }

            Assert.True(Decorations(2) is { Count: > 0 }, "the excluded line must be struck through");
            Assert.True(Decorations(1) is null or { Count: 0 });
            Assert.True(Decorations(3) is null or { Count: 0 });
        }
        finally
        {
            window.Close();
        }
    }

    private static (Window Window, TextEditor Editor, ConflictBackgroundRenderer Renderer) ShowResultRenderer(
        string text, Func<int, ConflictRowVisual>? rowVisual)
    {
        var editor = new TextEditor { Document = new TextDocument(text) };
        var parse = ConflictMarkerParser.Parse(text);
        var renderer = new ConflictBackgroundRenderer(editor, () => null, () => parse, getRowVisual: rowVisual);
        editor.TextArea.TextView.BackgroundRenderers.Add(renderer);
        var window = new Window { Width = 400, Height = 300, Content = editor };
        window.Show();
        window.UpdateLayout();
        editor.TextArea.TextView.EnsureVisualLines();
        return (window, editor, renderer);
    }

    private static void RenderBackground(TextEditor editor, ConflictBackgroundRenderer renderer)
    {
        using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize(400, 300));
        using var dc = bitmap.CreateDrawingContext();
        renderer.Draw(editor.TextArea.TextView, dc);
    }

    [AvaloniaFact]
    public void Result_renderer_without_row_visuals_matches_today()
    {
        var (window, editor, renderer) = ShowResultRenderer("top\n<<<<<<< a\nours\n=======\ntheirs\n>>>>>>> b\nbottom", null);
        try
        {
            RenderBackground(editor, renderer);
            Assert.Equal(5, renderer.RegionLinesDrawnForTesting);
            Assert.Equal(0, renderer.RowLinesDrawnForTesting);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Result_renderer_draws_rows_with_no_marker_regions()
    {
        var visuals = new[]
        {
            ConflictRowVisual.None,
            ConflictRowVisual.Ours | ConflictRowVisual.RegionTop,
            ConflictRowVisual.Theirs | ConflictRowVisual.Excluded | ConflictRowVisual.RegionBottom,
            ConflictRowVisual.None,
        };
        var (window, editor, renderer) = ShowResultRenderer("top\nours\ntheirs\nbottom", line => line < visuals.Length ? visuals[line] : ConflictRowVisual.None);
        try
        {
            RenderBackground(editor, renderer);
            Assert.Equal(2, renderer.RowLinesDrawnForTesting);
            Assert.Equal(0, renderer.RegionLinesDrawnForTesting);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Minimap_resolved_ranges_unchanged_value_invalidates_nothing()
    {
        var (window, minimap) = ShowMinimap(ManyConflicts(20));
        try
        {
            minimap.ResolvedRanges = new[] { (0, 3), (10, 12) };
            Pump();
            var bars = minimap.BarsRenderCountForTesting;

            minimap.ResolvedRanges = new[] { (0, 3), (10, 12) };
            Pump();
            Assert.Equal(bars, minimap.BarsRenderCountForTesting);

            minimap.ResolvedRanges = new[] { (0, 3) };
            Pump();
            Assert.Equal(bars + 1, minimap.BarsRenderCountForTesting);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Minimap_draws_resolved_ranges()
    {
        var (window, minimap) = ShowMinimap(ConflictMarkerParser.Parse(string.Join('\n', Enumerable.Range(0, 100).Select(i => $"line {i}"))));
        try
        {
            Assert.Equal(0, minimap.ResolvedRectCountForTesting);
            minimap.ResolvedRanges = new[] { (10, 14), (60, 65) };
            Pump();
            Assert.Equal(2, minimap.ResolvedRectCountForTesting);
            Assert.Equal(0, minimap.BarRectCountForTesting);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Row_tints_classify_pane_lines()
    {
        Assert.Equal(ConflictRowTint.Add, ConflictRowTints.ForPane(ConflictPaneLineType.Ours));
        Assert.Equal(ConflictRowTint.Add, ConflictRowTints.ForPane(ConflictPaneLineType.Theirs));
        Assert.Equal(ConflictRowTint.Marker, ConflictRowTints.ForPane(ConflictPaneLineType.Marker));
        Assert.Equal(ConflictRowTint.None, ConflictRowTints.ForPane(ConflictPaneLineType.Common));
        Assert.Equal(ConflictRowTint.None, ConflictRowTints.ForPane(ConflictPaneLineType.Padding));
    }

    [AvaloniaFact]
    public void Row_tints_classify_result_lines()
    {
        var parse = ConflictMarkerParser.Parse("top\n<<<<<<< a\nours\n||||||| base\nbase line\n=======\ntheirs\n>>>>>>> b\nbottom");
        Assert.Single(parse.Regions);

        var expected = new[]
        {
            ConflictRowTint.None,
            ConflictRowTint.Marker,
            ConflictRowTint.Add,
            ConflictRowTint.Marker,
            ConflictRowTint.Base,
            ConflictRowTint.Marker,
            ConflictRowTint.Add,
            ConflictRowTint.Marker,
            ConflictRowTint.None,
        };
        for (var line = 0; line < expected.Length; line++)
        {
            Assert.Equal(expected[line], ConflictRowTints.ForResult(line, ConflictRowVisual.None, parse));
        }

        Assert.Equal(ConflictRowTint.Add, ConflictRowTints.ForResult(0, ConflictRowVisual.Ours, null));
        Assert.Equal(ConflictRowTint.Add, ConflictRowTints.ForResult(0, ConflictRowVisual.Theirs | ConflictRowVisual.RegionTop, null));
        Assert.Equal(ConflictRowTint.Del, ConflictRowTints.ForResult(0, ConflictRowVisual.Ours | ConflictRowVisual.Excluded, null));
        Assert.Equal(ConflictRowTint.Del, ConflictRowTints.ForResult(0, ConflictRowVisual.Theirs | ConflictRowVisual.Excluded | ConflictRowVisual.RegionBottom, null));
        Assert.Equal(ConflictRowTint.None, ConflictRowTints.ForResult(0, ConflictRowVisual.Added, null));
        Assert.Equal(ConflictRowTint.Del, ConflictRowTints.ForResult(1, ConflictRowVisual.Excluded, parse));
    }

    [AvaloniaFact]
    public void Tint_brushes_map_to_diff_tokens()
    {
        using var applier = new AvaloniaThemeApplier(new StubThemeService());

        static void AssertColor(string token, IBrush? brush)
            => Assert.Equal(HistoriaPalette.Get(EffectiveTheme.Dark, token).ToAvalonia(), Assert.IsAssignableFrom<ISolidColorBrush>(brush).Color);

        AssertColor("diff-add-bg", ConflictRowTints.BackgroundBrush(ConflictRowTint.Add));
        AssertColor("diff-del-bg", ConflictRowTints.BackgroundBrush(ConflictRowTint.Del));
        AssertColor("conflict-base-bg", ConflictRowTints.BackgroundBrush(ConflictRowTint.Base));
        AssertColor("conflict-marker-bg", ConflictRowTints.BackgroundBrush(ConflictRowTint.Marker));
        Assert.Null(ConflictRowTints.BackgroundBrush(ConflictRowTint.None));

        AssertColor("diff-add-gutter", ConflictRowTints.GutterBrush(ConflictRowTint.Add));
        AssertColor("diff-del-gutter", ConflictRowTints.GutterBrush(ConflictRowTint.Del));
        Assert.Null(ConflictRowTints.GutterBrush(ConflictRowTint.Base));
        Assert.Null(ConflictRowTints.GutterBrush(ConflictRowTint.Marker));
        Assert.Null(ConflictRowTints.GutterBrush(ConflictRowTint.None));
    }

    [AvaloniaFact]
    public void Line_number_margin_tints_rows_from_getTint()
    {
        var editor = new TextEditor
        {
            Document = new TextDocument(string.Join('\n', Enumerable.Range(1, 200).Select(i => $"line {i}"))),
        };
        var margin = new ConflictLineNumberMargin(editor, getTint: row => row % 2 == 0 ? ConflictRowTint.Add : ConflictRowTint.Del);
        editor.TextArea.LeftMargins.Insert(0, margin);
        var window = new Window { Width = 600, Height = 400, Content = editor };
        window.Show();
        try
        {
            window.UpdateLayout();
            editor.TextArea.TextView.EnsureVisualLines();

            Assert.Equal(ConflictRowTint.Add, margin.TintForRowForTesting(0));
            Assert.Equal(ConflictRowTint.Del, margin.TintForRowForTesting(1));
            Assert.Equal(new Thickness(0), margin.Margin);
            RenderMargin(margin);

            Assert.Equal(ConflictRowTint.None, new ConflictLineNumberMargin(editor).TintForRowForTesting(0));
        }
        finally
        {
            window.Close();
        }
    }
}
