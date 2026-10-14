namespace Historia.Controls;

/// <summary>
/// Overview minimap rendering conflict regions and unresolved counts.
/// Adapted from SourceGit (MIT) — see docs/third-party-licenses.md.
/// <para>
/// Drawn as two child visuals, like the diff minimap: a bars layer with the regions bucketed by
/// <see cref="MinimapBars"/> (at most one bar per bucket row), re-rendered only when the parse,
/// the height or the palette changes, and a range layer that is the only thing a
/// <see cref="DisplayRange"/> change invalidates. Setting an unchanged value invalidates nothing.
/// Managed (resolved-but-toggleable) regions from <see cref="ResolvedRanges"/> are drawn muted
/// beneath the unresolved marker regions.
/// </para>
/// </summary>
public sealed class ConflictResolverMinimap : Control
{
    private readonly BarsLayer _bars;
    private readonly RangeLayer _range;
    private ConflictParse? _parse;
    private (int Start, int End)? _displayRange;
    private IReadOnlyList<(int Start, int End)>? _resolvedRanges;

    public ConflictParse? Parse
    {
        get => _parse;
        set
        {
            if (!ReferenceEquals(_parse, value))
            {
                _parse = value;
                _bars.Invalidate();
                _range.InvalidateVisual();
            }
        }
    }

    /// <summary>0-based line ranges of managed regions. An equal sequence invalidates nothing.</summary>
    public IReadOnlyList<(int Start, int End)>? ResolvedRanges
    {
        get => _resolvedRanges;
        set
        {
            if (ReferenceEquals(_resolvedRanges, value)
                || (_resolvedRanges != null && value != null && _resolvedRanges.SequenceEqual(value)))
            {
                return;
            }

            _resolvedRanges = value;
            _bars.Invalidate();
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

    public event EventHandler<int>? LineNavigationRequested;

    /// <summary>How many times the bars layer has rendered.</summary>
    internal int BarsRenderCountForTesting => _bars.RenderCount;

    /// <summary>Rectangles drawn by the last bars render.</summary>
    internal int BarRectCountForTesting => _bars.RectCount;

    /// <summary>Muted rectangles (managed regions) drawn by the last bars render.</summary>
    internal int ResolvedRectCountForTesting => _bars.ResolvedRectCount;

    /// <summary>How many times the range layer has rendered.</summary>
    internal int RangeRenderCountForTesting => _range.RenderCount;

    public ConflictResolverMinimap()
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
        PaletteBrushes.Changed += OnPaletteChanged;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        PaletteBrushes.Changed -= OnPaletteChanged;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnPaletteChanged(object? sender, EventArgs e)
    {
        _bars.Invalidate();
        _range.InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        // Transparent fill so the whole strip is hit-testable for navigation clicks.
        context.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, Bounds.Width, Bounds.Height));
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        if (_parse == null || _parse.LineCount == 0 || Bounds.Height <= 0)
        {
            return;
        }

        int total = _parse.LineCount;
        var pressedY = e.GetPosition(this).Y;
        int line = Math.Max(1, Math.Min(total, (int)Math.Ceiling(pressedY * total / Bounds.Height)));
        LineNavigationRequested?.Invoke(this, line);
        e.Handled = true;
    }

    /// <summary>Draws the cached region bars. The region walk happens only on a rebuild.</summary>
    private sealed class BarsLayer : Control
    {
        private readonly ConflictResolverMinimap _owner;
        private IReadOnlyList<MinimapSpan>? _spans;
        private IReadOnlyList<MinimapSpan>? _resolvedSpans;
        private int _bucketHeight;

        public int RenderCount { get; private set; }
        public int RectCount { get; private set; }
        public int ResolvedRectCount { get; private set; }

        public BarsLayer(ConflictResolverMinimap owner)
        {
            _owner = owner;
            IsHitTestVisible = false;
        }

        public void Invalidate()
        {
            _spans = null;
            _resolvedSpans = null;
            InvalidateVisual();
        }

        public override void Render(DrawingContext context)
        {
            RenderCount++;
            RectCount = 0;
            ResolvedRectCount = 0;

            var parse = _owner._parse;
            if (parse == null || parse.LineCount == 0 || Bounds.Height <= 0)
            {
                return;
            }

            int height = Math.Max(1, (int)Math.Ceiling(Bounds.Height));
            if (_spans == null || height != _bucketHeight)
            {
                _bucketHeight = height;
                _spans = MinimapBars.FromRegions(parse.Regions.Select(r => (r.StartLine, r.EndLine)), parse.LineCount, height);
                _resolvedSpans = _owner._resolvedRanges is { Count: > 0 } resolved
                    ? MinimapBars.FromRegions(resolved, parse.LineCount, height)
                    : null;
            }

            double scale = Bounds.Height / height;
            if (_resolvedSpans != null)
            {
                var resolvedBrush = PaletteBrushes.Get("ColorTextFaint");
                foreach (var span in _resolvedSpans)
                {
                    context.DrawRectangle(resolvedBrush, null, new Rect(0, span.Top * scale, Bounds.Width, Math.Max(2.0, span.Height * scale)));
                    ResolvedRectCount++;
                }
            }

            var conflictBrush = PaletteBrushes.Get("ColorWarning");
            foreach (var span in _spans)
            {
                context.DrawRectangle(conflictBrush, null, new Rect(0, span.Top * scale, Bounds.Width, Math.Max(2.0, span.Height * scale)));
                RectCount++;
            }
        }
    }

    /// <summary>Draws only the visible-range box.</summary>
    private sealed class RangeLayer : Control
    {
        private readonly ConflictResolverMinimap _owner;

        public int RenderCount { get; private set; }

        public RangeLayer(ConflictResolverMinimap owner)
        {
            _owner = owner;
            IsHitTestVisible = false;
        }

        public override void Render(DrawingContext context)
        {
            RenderCount++;

            var parse = _owner._parse;
            if (parse == null || parse.LineCount == 0 || _owner._displayRange is not { } range || range.End <= range.Start)
            {
                return;
            }

            int total = parse.LineCount;
            double startY = range.Start / (double)total * Bounds.Height;
            double endY = range.End / (double)total * Bounds.Height;
            var rect = new Rect(0, startY, Bounds.Width, Math.Max(2.0, endY - startY));

            var rangeBrush = PaletteBrushes.GetTranslucent("ColorAccent", 0x30);
            var rangePen = PaletteBrushes.Pen("ColorAccent", 1);
            context.DrawRectangle(rangeBrush, rangePen, rect);
        }
    }
}
