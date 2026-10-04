namespace Historia.Controls;

/// <summary>
/// Overview minimap rendering added/deleted row bars and visible scroll range.
/// Adapted from SourceGit (MIT) — see docs/third-party-licenses.md.
/// <para>
/// Drawn as two child visuals so a scroll step costs one rectangle. The bars layer draws the
/// bucketed <see cref="MinimapBars"/> (at most one bar per bucket row per kind) and re-renders only
/// when the model, the layout mode, the height or the palette changes; the range layer draws the
/// visible-range box and is the only thing a <see cref="DisplayRange"/> change invalidates. Each
/// visual keeps its own recorded draw list, so invalidating one never re-records the other.
/// </para>
/// </summary>
public sealed class DiffMinimap : Control
{
    private readonly BarsLayer _bars;
    private readonly RangeLayer _range;
    private DiffDisplayModel? _model;
    private (int Start, int End)? _displayRange;
    private bool _isSideBySide;

    public DiffDisplayModel? Model
    {
        get => _model;
        set
        {
            if (!ReferenceEquals(_model, value))
            {
                _model = value;
                _bars.Invalidate();
                _range.InvalidateVisual();
            }
        }
    }

    public (int Start, int End)? DisplayRange
    {
        get => _displayRange;
        set
        {
            if (_displayRange != value)
            {
                _displayRange = value;
                _range.InvalidateVisual();
            }
        }
    }

    public bool IsSideBySide
    {
        get => _isSideBySide;
        set
        {
            if (_isSideBySide != value)
            {
                _isSideBySide = value;
                _bars.Invalidate();
                _range.InvalidateVisual();
            }
        }
    }

    public event EventHandler<int>? LineNavigationRequested;

    /// <summary>How many times the bars layer has rendered.</summary>
    internal int BarsRenderCountForTesting => _bars.RenderCount;

    /// <summary>Rectangles drawn by the last bars render.</summary>
    internal int BarRectCountForTesting => _bars.RectCount;

    /// <summary>Bucket rows the current bars were computed for; 0 before the first render.</summary>
    internal int BarsBucketHeightForTesting => _bars.BucketHeight;

    public DiffMinimap()
    {
        ClipToBounds = true;
        _bars = new BarsLayer(this);
        _range = new RangeLayer(this);
        VisualChildren.Add(_bars);
        VisualChildren.Add(_range);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        PaletteBrushes.Changed += OnPaletteBrushesChanged;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        PaletteBrushes.Changed -= OnPaletteBrushesChanged;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnPaletteBrushesChanged(object? sender, EventArgs e)
    {
        _bars.Invalidate();
        _range.InvalidateVisual();
    }

    private int TotalRows => _model == null
        ? 0
        : _isSideBySide
            ? Math.Max(_model.SideBySideOldRows.Count, _model.SideBySideNewRows.Count)
            : _model.CombinedRows.Count;

    public override void Render(DrawingContext context)
    {
        // Transparent fill so the whole strip is hit-testable for navigation clicks.
        context.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, Bounds.Width, Bounds.Height));
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        int total = TotalRows;
        if (total == 0 || Bounds.Height <= 0)
        {
            return;
        }

        var pressedY = e.GetPosition(this).Y;
        int line = Math.Max(1, Math.Min(total, (int)Math.Ceiling(pressedY * total / Bounds.Height)));
        LineNavigationRequested?.Invoke(this, line);
        e.Handled = true;
    }

    /// <summary>Draws the cached add/delete bars. The row walk happens only on a rebuild.</summary>
    private sealed class BarsLayer : Control
    {
        private readonly DiffMinimap _owner;
        private MinimapBars? _first;
        private MinimapBars? _second;
        private int _bucketHeight;

        public int RenderCount { get; private set; }
        public int RectCount { get; private set; }
        public int BucketHeight => _bucketHeight;

        public BarsLayer(DiffMinimap owner)
        {
            _owner = owner;
            IsHitTestVisible = false;
        }

        public void Invalidate()
        {
            _first = null;
            _second = null;
            InvalidateVisual();
        }

        public override void Render(DrawingContext context)
        {
            RenderCount++;
            RectCount = 0;

            var model = _owner._model;
            int total = _owner.TotalRows;
            if (model == null || total == 0 || Bounds.Height <= 0)
            {
                return;
            }

            int height = Math.Max(1, (int)Math.Ceiling(Bounds.Height));
            if (_first == null || height != _bucketHeight)
            {
                Rebuild(model, total, height);
            }

            double scale = Bounds.Height / height;
            var addBrush = PaletteBrushes.Get("ColorDiffAddBg");
            var delBrush = PaletteBrushes.Get("ColorDiffDelBg");

            if (_owner._isSideBySide)
            {
                double half = Bounds.Width * 0.5;
                Draw(context, _first!, 0, half, scale, addBrush, delBrush);
                Draw(context, _second!, half, half, scale, addBrush, delBrush);
            }
            else
            {
                Draw(context, _first!, 0, Bounds.Width, scale, addBrush, delBrush);
            }
        }

        private void Rebuild(DiffDisplayModel model, int total, int height)
        {
            _bucketHeight = height;
            if (_owner._isSideBySide)
            {
                _first = MinimapBars.FromRows(model.SideBySideOldRows, total, height);
                _second = MinimapBars.FromRows(model.SideBySideNewRows, total, height);
            }
            else
            {
                _first = MinimapBars.FromRows(model.CombinedRows, total, height);
                _second = null;
            }
        }

        private void Draw(DrawingContext context, MinimapBars bars, double x, double width, double scale, IBrush addBrush, IBrush delBrush)
        {
            foreach (var span in bars.Additions)
            {
                context.DrawRectangle(addBrush, null, new Rect(x, span.Top * scale, width, span.Height * scale));
                RectCount++;
            }

            foreach (var span in bars.Deletions)
            {
                context.DrawRectangle(delBrush, null, new Rect(x, span.Top * scale, width, span.Height * scale));
                RectCount++;
            }
        }
    }

    /// <summary>Draws only the visible-range box.</summary>
    private sealed class RangeLayer : Control
    {
        private readonly DiffMinimap _owner;

        public RangeLayer(DiffMinimap owner)
        {
            _owner = owner;
            IsHitTestVisible = false;
        }

        public override void Render(DrawingContext context)
        {
            int total = _owner.TotalRows;
            if (_owner._displayRange is not { } range || range.End <= range.Start || total <= 0)
            {
                return;
            }

            double startY = range.Start / (double)total * Bounds.Height;
            double endY = range.End / (double)total * Bounds.Height;
            var rect = new Rect(0, startY, Bounds.Width, Math.Max(2.0, endY - startY));

            var rangeBrush = PaletteBrushes.GetTranslucent("ColorAccent", 0x30);
            var rangePen = PaletteBrushes.Pen("ColorAccent", 1);
            context.DrawRectangle(rangeBrush, rangePen, rect);
        }
    }
}
