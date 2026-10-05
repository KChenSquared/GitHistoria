namespace Historia.Controls;

/// <summary>
/// Base class for background renderers that guards against VisualLinesInvalidException:
/// drawing is skipped while the text view has no document or its visual lines are invalid.
/// The same guard SourceGit uses.
/// </summary>
public abstract class SafeBackgroundRenderer : IBackgroundRenderer
{
    public abstract KnownLayer Layer { get; }

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (textView == null || !textView.VisualLinesValid || textView.Document == null)
        {
            return;
        }

        DrawCore(textView, drawingContext);
    }

    protected abstract void DrawCore(TextView textView, DrawingContext drawingContext);
}
