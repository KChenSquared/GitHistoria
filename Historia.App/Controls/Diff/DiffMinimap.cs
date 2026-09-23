namespace Historia.Controls;

/// <summary>
/// Overview minimap rendering added/deleted row bars and visible scroll range.
/// Adapted from SourceGit (MIT) — see docs/third-party-licenses.md.
/// </summary>
public sealed class DiffMinimap : Control
{
    private DiffDisplayModel? _model;
    private (int Start, int End)? _displayRange;
    private bool _isSideBySide;

    private IBrush? _addBrush;
    private IBrush? _delBrush;
    private IBrush? _rangeBrush;
    private IPen? _rangePen;

    public DiffDisplayModel? Model
    {
        get => _model;
        set
        {
            if (!ReferenceEquals(_model, value))
            {
                _model = value;
                InvalidateVisual();
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
                InvalidateVisual();
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
                InvalidateVisual();
            }
        }
    }

    public event EventHandler<int>? LineNavigationRequested;

    public DiffMinimap()
    {
        ClipToBounds = true;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        RefreshResources();

        if (VisualRoot is TopLevel top)
        {
            top.ActualThemeVariantChanged += OnThemeVariantChanged;
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (VisualRoot is TopLevel top)
        {
            top.ActualThemeVariantChanged -= OnThemeVariantChanged;
        }
        base.OnDetachedFromVisualTree(e);
    }

    private void OnThemeVariantChanged(object? sender, EventArgs e)
    {
        RefreshResources();
        InvalidateVisual();
    }

    public void RefreshResources()
    {
        _addBrush = this.FindResource("ColorDiffAddBg") as IBrush ?? Brushes.Green;
        _delBrush = this.FindResource("ColorDiffDelBg") as IBrush ?? Brushes.Red;

        var accent = this.FindResource("ColorAccent") as IBrush ?? Brushes.RoyalBlue;
        _rangeBrush = accent;
        _rangePen = new Pen(accent, 1);
    }

    public override void Render(DrawingContext context)
    {
        context.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, Bounds.Width, Bounds.Height));

        if (_model == null)
        {
            return;
        }

        int total;
        if (_isSideBySide)
        {
            total = Math.Max(_model.SideBySideOldRows.Count, _model.SideBySideNewRows.Count);
            if (total == 0)
            {
                return;
            }

            double halfWidth = Bounds.Width * 0.5;
            RenderRows(context, _model.SideBySideOldRows, 0, halfWidth, total);
            RenderRows(context, _model.SideBySideNewRows, halfWidth, halfWidth, total);
        }
        else
        {
            total = _model.CombinedRows.Count;
            if (total == 0)
            {
                return;
            }

            RenderRows(context, _model.CombinedRows, 0, Bounds.Width, total);
        }

        if (_displayRange is { } range && range.End > range.Start && total > 0)
        {
            double startY = range.Start / (double)total * Bounds.Height;
            double endY = range.End / (double)total * Bounds.Height;
            var rect = new Rect(0, startY, Bounds.Width, Math.Max(2.0, endY - startY));

            if (_rangeBrush != null)
            {
                context.DrawRectangle(_rangeBrush, null, rect);
            }

            if (_rangePen != null)
            {
                context.DrawRectangle(null, _rangePen, rect);
            }
        }
    }

    private void RenderRows(DrawingContext context, IReadOnlyList<DiffDisplayRow> rows, double x, double width, int total)
    {
        if (rows.Count == 0 || total == 0)
        {
            return;
        }

        var lastType = DiffDisplayLineType.None;
        int blockStart = 0;

        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (row.Type != lastType)
            {
                RenderBlock(context, lastType, blockStart, i - blockStart, total, x, width);
                lastType = row.Type;
                blockStart = i;
            }
        }

        RenderBlock(context, lastType, blockStart, rows.Count - blockStart, total, x, width);
    }

    private void RenderBlock(DrawingContext context, DiffDisplayLineType type, int start, int count, int total, double x, double width)
    {
        if (count <= 0 || total <= 0)
        {
            return;
        }

        IBrush? brush = type switch
        {
            DiffDisplayLineType.Addition => _addBrush,
            DiffDisplayLineType.Deletion => _delBrush,
            _ => null,
        };

        if (brush != null)
        {
            double y = start / (double)total * Bounds.Height;
            double h = Math.Max(1.0, count / (double)total * Bounds.Height);
            context.DrawRectangle(brush, null, new Rect(x, y, width, h));
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        if (_model == null)
        {
            return;
        }

        int total = _isSideBySide
            ? Math.Max(_model.SideBySideOldRows.Count, _model.SideBySideNewRows.Count)
            : _model.CombinedRows.Count;

        if (total == 0 || Bounds.Height <= 0)
        {
            return;
        }

        var pressedY = e.GetPosition(this).Y;
        int line = Math.Max(1, Math.Min(total, (int)Math.Ceiling(pressedY * total / Bounds.Height)));
        LineNavigationRequested?.Invoke(this, line);
        e.Handled = true;
    }
}
