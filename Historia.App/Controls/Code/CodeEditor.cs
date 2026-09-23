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
}
