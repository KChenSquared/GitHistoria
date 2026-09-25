namespace Historia.Controls;

/// <summary>
/// Base class for background renderers that guards against VisualLinesInvalidException.
/// Reproduces SourceGit's safety guard (commit ddf00a9).
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
