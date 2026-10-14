namespace Historia.Controls;

/// <summary>
/// Base code editor control built on AvaloniaEdit TextEditor.
/// Configured with monospace typography, custom margins, and palette resource bindings.
/// </summary>
public class CodeEditor : TextEditor
{
    protected override Type StyleKeyOverride => typeof(TextEditor);

    public CodeEditor()
    {
        IsReadOnly = true;
        ShowLineNumbers = false;
        FontFamily = new FontFamily("Consolas, Menlo, monospace");
        FontSize = 12;
        WordWrap = false;

        Options.EnableHyperlinks = false;
        Options.EnableEmailHyperlinks = false;
        Options.EnableTextDragDrop = false;
        Options.AllowScrollBelowDocument = false;

        this[!BackgroundProperty] = new DynamicResourceExtension("ColorBg");
        this[!ForegroundProperty] = new DynamicResourceExtension("ColorText");
        TextArea[!TextArea.SelectionBrushProperty] = new DynamicResourceExtension("ColorCodeSelection");
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        PaletteBrushes.Changed += OnPaletteBrushesChanged;
        OnPaletteBrushesChanged(null, EventArgs.Empty);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        PaletteBrushes.Changed -= OnPaletteBrushesChanged;
    }

    private void OnPaletteBrushesChanged(object? sender, EventArgs e)
    {
        TextArea.TextView.Redraw();
        foreach (var margin in TextArea.LeftMargins)
        {
            margin.InvalidateVisual();
        }
    }
}
