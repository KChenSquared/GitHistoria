namespace HistoriaApp.Tests;

[Trait("tier", "ui")]
public sealed class EditorReattachPaletteTests
{
    private static void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private static void RenderMargin(Control margin)
    {
        var size = new PixelSize(Math.Max(1, (int)margin.Bounds.Width), Math.Max(1, (int)margin.Bounds.Height));
        using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(size);
        using var dc = bitmap.CreateDrawingContext();
        margin.Render(dc);
    }

    [AvaloniaFact]
    public void Toggle_margin_uses_the_new_palette_after_a_detached_theme_change()
    {
        var app = Application.Current!;
        app.TryFindResource("ColorBg", out var originalBgResource);
        var oldBrush = originalBgResource as IBrush ?? Brushes.Black;
        var newBrush = new SolidColorBrush(Colors.Magenta);

        var editor = new TextEditor
        {
            Document = new TextDocument(string.Join('\n', Enumerable.Range(1, 200).Select(i => $"line {i}"))),
        };
        var margin = new ConflictToggleMargin(line => line % 2 == 0 ? ConflictToggleGlyph.Checked : ConflictToggleGlyph.Unchecked);
        editor.TextArea.LeftMargins.Insert(0, margin);

        var window = new Window { Width = 600, Height = 500, Content = editor };
        var placeholder = new Border();
        window.Show();

        try
        {
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            editor.TextArea.TextView.EnsureVisualLines();
            RenderMargin(margin);

            Assert.NotNull(margin.BoxBrushForTesting);
            Assert.NotSame(newBrush, margin.BoxBrushForTesting);

            window.Content = placeholder;
            Dispatcher.UIThread.RunJobs();

            app.Resources["ColorBg"] = newBrush;
            PaletteBrushes.Invalidate();

            window.Content = editor;
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            editor.TextArea.TextView.EnsureVisualLines();
            RenderMargin(margin);

            Assert.Same(newBrush, margin.BoxBrushForTesting);

            margin.ResetPoolCountersForTesting();
            RenderMargin(margin);
            Assert.Equal(0, margin.PoolMissesForTesting);
        }
        finally
        {
            app.Resources["ColorBg"] = oldBrush;
            PaletteBrushes.Invalidate();
            window.Close();
        }
    }

    private sealed class AccentColorizer : DocumentColorizingTransformer
    {
        protected override void ColorizeLine(DocumentLine line)
        {
            var brush = PaletteBrushes.Get("ColorAccent");
            ChangeLinePart(line.Offset, line.EndOffset, element => element.TextRunProperties.SetForegroundBrush(brush));
        }
    }

    private static Color? FirstRunColor(CodeEditor editor)
    {
        editor.TextArea.TextView.EnsureVisualLines();
        var line = editor.TextArea.TextView.GetVisualLine(1);
        return (line?.Elements[0].TextRunProperties.ForegroundBrush as ISolidColorBrush)?.Color;
    }

    [AvaloniaFact]
    public void Code_editor_recolors_lines_after_a_detached_theme_change()
    {
        var app = Application.Current!;
        app.TryFindResource("ColorAccent", out var originalAccentResource);
        var oldBrush = originalAccentResource as IBrush ?? Brushes.Orange;
        var newBrush = new SolidColorBrush(Colors.Magenta);

        var editor = new CodeEditor
        {
            Document = new TextDocument("line 1\nline 2\nline 3\n"),
        };
        editor.TextArea.TextView.LineTransformers.Add(new AccentColorizer());
        var window = new Window { Width = 600, Height = 400, Content = editor };
        var placeholder = new Border();
        window.Show();

        try
        {
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            Assert.NotEqual(Colors.Magenta, FirstRunColor(editor));

            window.Content = placeholder;
            Dispatcher.UIThread.RunJobs();

            app.Resources["ColorAccent"] = newBrush;
            PaletteBrushes.Invalidate();

            window.Content = editor;
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(Colors.Magenta, FirstRunColor(editor));
        }
        finally
        {
            app.Resources["ColorAccent"] = oldBrush;
            PaletteBrushes.Invalidate();
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Resolver_minimap_rerenders_on_reattach()
    {
        var parse = ConflictMarkerParser.Parse("top\n<<<<<<< a\nours\n=======\ntheirs\n>>>>>>> b\nbottom\n");
        var minimap = new ConflictResolverMinimap { Parse = parse, Width = 12, Height = 300 };
        var window = new Window { Width = 200, Height = 400, Content = minimap };
        var placeholder = new Border();
        window.Show();

        try
        {
            Pump();
            var initialRenderCount = minimap.BarsRenderCountForTesting;
            Assert.True(initialRenderCount > 0, "bars layer must have rendered initially");

            window.Content = placeholder;
            Pump();

            PaletteBrushes.Invalidate();

            window.Content = minimap;
            Pump();

            Assert.True(minimap.BarsRenderCountForTesting > initialRenderCount, "bars layer must re-render after re-attach");
        }
        finally
        {
            PaletteBrushes.Invalidate();
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Line_number_margin_clears_cache_and_repaints_after_a_detached_theme_change()
    {
        var editor = new TextEditor
        {
            Document = new TextDocument(string.Join('\n', Enumerable.Range(1, 100).Select(i => $"line {i}"))),
        };
        var margin = new ConflictLineNumberMargin(editor);
        editor.TextArea.LeftMargins.Insert(0, margin);
        var window = new Window { Width = 600, Height = 500, Content = editor };
        var placeholder = new Border();
        window.Show();

        try
        {
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            editor.TextArea.TextView.EnsureVisualLines();
            RenderMargin(margin);

            Assert.True(margin.TextCacheForTesting.Count > 0);

            window.Content = placeholder;
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(0, margin.TextCacheForTesting.Count);

            PaletteBrushes.Invalidate();

            window.Content = editor;
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            editor.TextArea.TextView.EnsureVisualLines();
            RenderMargin(margin);

            Assert.True(margin.TextCacheForTesting.Count > 0);
        }
        finally
        {
            PaletteBrushes.Invalidate();
            window.Close();
        }
    }
}
