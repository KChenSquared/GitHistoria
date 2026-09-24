namespace HistoriaApp.Tests;

public class CodeEditorTests
{
    private sealed class TestSafeRenderer : SafeBackgroundRenderer
    {
        public bool DrawCoreCalled { get; private set; }

        public override KnownLayer Layer => KnownLayer.Background;

        protected override void DrawCore(TextView textView, DrawingContext drawingContext)
        {
            DrawCoreCalled = true;
        }
    }

    [AvaloniaFact]
    public void CodeEditor_constructs_headless_with_expected_defaults()
    {
        var editor = new CodeEditor();

        Assert.True(editor.IsReadOnly);
        Assert.False(editor.ShowLineNumbers);
        Assert.False(editor.WordWrap);
        Assert.Equal(12, editor.FontSize);

        Assert.False(editor.Options.EnableHyperlinks);
        Assert.False(editor.Options.EnableEmailHyperlinks);
        Assert.False(editor.Options.EnableTextDragDrop);
        Assert.False(editor.Options.AllowScrollBelowDocument);
    }

    [Fact]
    public void SafeBackgroundRenderer_skips_draw_when_visual_lines_invalid()
    {
        var renderer = new TestSafeRenderer();
        var textView = new TextView();

        Assert.False(textView.VisualLinesValid);

        // Should return early and not call DrawCore or throw VisualLinesInvalidException
        renderer.Draw(textView, null!);

        Assert.False(renderer.DrawCoreCalled);
    }

    [Fact]
    public void SafeBackgroundRenderer_skips_draw_when_document_null()
    {
        var renderer = new TestSafeRenderer();
        renderer.Draw(null!, null!);
        Assert.False(renderer.DrawCoreCalled);
    }

    [AvaloniaFact]
    public void Conflict_pane_scrollviewer_is_hooked_after_show()
    {
        var editor = new ConflictPaneEditor();
        editor.Document = new TextDocument(string.Join("\n", Enumerable.Range(1, 100).Select(i => $"Line {i}")));

        var window = new Window { Width = 400, Height = 300, Content = editor };
        window.Show();

        try
        {
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            var sv = editor.FindDescendantOfType<ScrollViewer>();
            Assert.NotNull(sv);

            bool scrollChangedFired = false;
            editor.ScrollOffsetChanged += (_, _) => scrollChangedFired = true;

            sv.Offset = new Vector(0, 40);
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            Assert.True(scrollChangedFired);
            Assert.Equal(new Vector(0, 40), editor.GetScrollOffset());
        }
        finally
        {
            window.Close();
        }
    }
}
