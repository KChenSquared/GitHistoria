namespace Historia.Controls;

/// <summary>The translucent tab copy that follows the cursor during a reorder drag.</summary>
internal sealed class AppBandDragGhost : Control, IDisposable
{
    private readonly Bitmap _image;
    private readonly Size _size;
    private Point _topLeft;

    public AppBandDragGhost(Bitmap image, Size size)
    {
        _image = image;
        _size = size;
        Opacity = 0.75;
        IsHitTestVisible = false;
        Effect = new DropShadowEffect { BlurRadius = 10, OffsetX = 0, OffsetY = 3, Opacity = 0.35 };
    }

    public void Dispose() => _image.Dispose();

    public void MoveTo(Point topLeft)
    {
        _topLeft = topLeft;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
        => context.DrawImage(_image, new Rect(_topLeft, _size));
}
