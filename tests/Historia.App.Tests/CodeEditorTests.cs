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
}
