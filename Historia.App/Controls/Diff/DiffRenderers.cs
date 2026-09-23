namespace Historia.Controls;

/// <summary>
/// Vertical separator margin between diff editor margins.
/// Adapted from SourceGit (MIT) — see docs/third-party-licenses.md.
/// </summary>
public sealed class DiffVerticalSeparatorMargin : AbstractMargin
{
    private Pen? _pen;

    public DiffVerticalSeparatorMargin()
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
        var brush = this.FindResource("ColorBorder") as IBrush ?? Brushes.Gray;
        _pen = new Pen(brush, 1);
    }

    public override void Render(DrawingContext context)
    {
        if (_pen != null)
        {
            context.DrawLine(_pen, new Point(0.5, 0), new Point(0.5, Bounds.Height));
        }
    }

    protected override Size MeasureOverride(Size availableSize) => new(1, 0);
}

/// <summary>
/// Line number margin displaying old or new line numbers for diff rows.
/// Adapted from SourceGit (MIT) — see docs/third-party-licenses.md.
/// </summary>
public sealed class DiffLineNumberMargin : AbstractMargin
{
    private readonly bool _isOld;
    private readonly DiffPresenterBase _presenter;
    private IBrush? _foregroundBrush;

    private Typeface _typeface;

    public DiffLineNumberMargin(DiffPresenterBase presenter, bool isOld)
    {
        _presenter = presenter;
        _isOld = isOld;
        _typeface = new Typeface(_presenter.FontFamily, _presenter.FontStyle, _presenter.FontWeight);
        Margin = new Thickness(8, 0);
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
        _foregroundBrush = this.FindResource("ColorTextFaint") as IBrush ?? Brushes.Gray;
        _typeface = new Typeface(_presenter.FontFamily, _presenter.FontStyle, _presenter.FontWeight);
    }

    public override void Render(DrawingContext context)
    {
        var view = TextView;
        if (view is not { VisualLinesValid: true } || _foregroundBrush == null)
        {
            return;
        }

        var rows = _presenter.Rows;

        foreach (var line in view.VisualLines)
        {
            if (line.IsDisposed || line.FirstDocumentLine == null || line.FirstDocumentLine.IsDeleted)
            {
                continue;
            }

            var index = line.FirstDocumentLine.LineNumber;
            if (index > rows.Count)
            {
                break;
            }

            var row = rows[index - 1];
            var lineNumber = _isOld ? row.OldLineText : row.NewLineText;
            if (string.IsNullOrEmpty(lineNumber))
            {
                continue;
            }

            var y = line.GetTextLineVisualYPosition(line.TextLines[0], VisualYPosition.LineMiddle) - view.VerticalOffset;
            var txt = new FormattedText(
                lineNumber,
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                _typeface,
                _presenter.FontSize,
                _foregroundBrush);

            context.DrawText(txt, new Point(Bounds.Width - txt.Width, y - (txt.Height * 0.5)));
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var maxLine = _presenter.Model?.MaxLineNumber ?? 0;
        if (maxLine <= 0)
        {
            return new Size(24, 0);
        }

        var typeface = new Typeface(_presenter.FontFamily);
        var test = new FormattedText(
            maxLine.ToString(CultureInfo.InvariantCulture),
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            typeface,
            _presenter.FontSize,
            Brushes.White);

        return new Size(test.Width, 0);
    }
}

/// <summary>
/// Gutter margin showing '+' and '-' modification indicators.
/// Adapted from SourceGit (MIT) — see docs/third-party-licenses.md.
/// </summary>
public sealed class DiffLineModifyTypeMargin : AbstractMargin
{
    private readonly DiffPresenterBase _presenter;
    private IBrush? _addBrush;
    private IBrush? _delBrush;

    private Typeface _typeface;

    public DiffLineModifyTypeMargin(DiffPresenterBase presenter)
    {
        _presenter = presenter;
        _typeface = new Typeface(_presenter.FontFamily, _presenter.FontStyle, _presenter.FontWeight);
        Margin = new Thickness(4, 0);
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
        _addBrush = this.FindResource("ColorGitAdded") as IBrush ?? Brushes.Green;
        _delBrush = this.FindResource("ColorGitDeleted") as IBrush ?? Brushes.Red;
        _typeface = new Typeface(_presenter.FontFamily, _presenter.FontStyle, _presenter.FontWeight);
    }

    public override void Render(DrawingContext context)
    {
        var view = TextView;
        if (view is not { VisualLinesValid: true })
        {
            return;
        }

        var rows = _presenter.Rows;

        foreach (var line in view.VisualLines)
        {
            if (line.IsDisposed || line.FirstDocumentLine == null || line.FirstDocumentLine.IsDeleted)
            {
                continue;
            }

            var index = line.FirstDocumentLine.LineNumber;
            if (index > rows.Count)
            {
                break;
            }

            var row = rows[index - 1];
            FormattedText? indicator = null;
            var y = line.GetTextLineVisualYPosition(line.TextLines[0], VisualYPosition.LineMiddle) - view.VerticalOffset;

            if (row.Type == DiffDisplayLineType.Addition && _addBrush != null)
            {
                indicator = new FormattedText(
                    "+",
                    CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    _typeface,
                    _presenter.FontSize,
                    _addBrush);
            }
            else if (row.Type == DiffDisplayLineType.Deletion && _delBrush != null)
            {
                indicator = new FormattedText(
                    "-",
                    CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    _typeface,
                    _presenter.FontSize,
                    _delBrush);
            }

            if (indicator != null)
            {
                context.DrawText(indicator, new Point(0, y - (indicator.Height * 0.5)));
            }
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var typeface = new Typeface(_presenter.FontFamily);
        var test = new FormattedText(
            "-",
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            typeface,
            _presenter.FontSize,
            Brushes.White);
        return new Size(test.Width, 0);
    }
}

/// <summary>
/// Safe background renderer for diff line washes, intra-line spans, and block borders.
/// Adapted from SourceGit (MIT) — see docs/third-party-licenses.md.
/// </summary>
public sealed class DiffLineBackgroundRenderer : SafeBackgroundRenderer
{
    private readonly DiffPresenterBase _presenter;
    private IBrush? _addBg;
    private IBrush? _delBg;
    private IBrush? _emptyBg;
    private IBrush? _addHl;
    private IBrush? _delHl;
    private IPen? _blockBorderPen;

    public override KnownLayer Layer => KnownLayer.Background;

    public DiffLineBackgroundRenderer(DiffPresenterBase presenter)
    {
        _presenter = presenter;
        RefreshResources();
    }

    public void RefreshResources()
    {
        _addBg = _presenter.FindResource("ColorDiffAddBg") as IBrush ?? Brushes.Transparent;
        _delBg = _presenter.FindResource("ColorDiffDelBg") as IBrush ?? Brushes.Transparent;
        _emptyBg = _presenter.FindResource("ColorDiffEmptyBg") as IBrush ?? Brushes.Transparent;
        _addHl = _presenter.FindResource("ColorDiffAddHl") as IBrush ?? Brushes.Transparent;
        _delHl = _presenter.FindResource("ColorDiffDelHl") as IBrush ?? Brushes.Transparent;

        var borderBrush = _presenter.FindResource("ColorAccent") as IBrush ?? Brushes.DodgerBlue;
        _blockBorderPen = new Pen(borderBrush, 1);
    }

    protected override void DrawCore(TextView textView, DrawingContext drawingContext)
    {
        var rows = _presenter.Rows;
        if (rows.Count == 0)
        {
            return;
        }

        var width = textView.Bounds.Width;
        var changeBlock = _presenter.CurrentBlock;

        foreach (var line in textView.VisualLines)
        {
            if (line.IsDisposed || line.FirstDocumentLine == null || line.FirstDocumentLine.IsDeleted)
            {
                continue;
            }

            var index = line.FirstDocumentLine.LineNumber;
            if (index > rows.Count)
            {
                break;
            }

            var row = rows[index - 1];
            var lastTextLine = line.TextLines[^1];
            var startY = line.GetTextLineVisualYPosition(line.TextLines[0], VisualYPosition.LineTop) - textView.VerticalOffset;
            var endY = line.GetTextLineVisualYPosition(lastTextLine, VisualYPosition.LineBottom) - textView.VerticalOffset;

            IBrush? bg = row.Type switch
            {
                DiffDisplayLineType.Addition => _addBg,
                DiffDisplayLineType.Deletion => _delBg,
                DiffDisplayLineType.None => _emptyBg,
                _ => null,
            };

            if (bg != null)
            {
                drawingContext.DrawRectangle(bg, null, new Rect(0, startY, width, endY - startY));

                if (row.Highlights.Count > 0)
                {
                    var highlightBg = row.Type == DiffDisplayLineType.Addition ? _addHl : _delHl;
                    if (highlightBg != null)
                    {
                        int processingIdxStart = 0;
                        int processingIdxEnd = 0;
                        int nextHighlight = 0;

                        foreach (var tl in line.TextLines)
                        {
                            processingIdxEnd += tl.Length;
                            var y = line.GetTextLineVisualYPosition(tl, VisualYPosition.LineTop) - textView.VerticalOffset;
                            var h = line.GetTextLineVisualYPosition(tl, VisualYPosition.LineBottom) - textView.VerticalOffset - y;

                            while (nextHighlight < row.Highlights.Count)
                            {
                                var highlight = row.Highlights[nextHighlight];
                                int hlStart = highlight.Start;
                                int hlEnd = highlight.Start + highlight.Length - 1;

                                if (hlStart >= processingIdxEnd)
                                {
                                    break;
                                }

                                var startCol = line.GetVisualColumn(hlStart < processingIdxStart ? processingIdxStart : hlStart);
                                var endCol = line.GetVisualColumn(hlEnd >= processingIdxEnd ? processingIdxEnd : hlEnd + 1);

                                var x = line.GetTextLineVisualXPosition(tl, startCol) - textView.HorizontalOffset;
                                var w = line.GetTextLineVisualXPosition(tl, endCol) - textView.HorizontalOffset - x;

                                if (w > 0 && h > 0)
                                {
                                    drawingContext.DrawRectangle(highlightBg, null, new Rect(x, y, w, h));
                                }

                                if (hlEnd >= processingIdxEnd)
                                {
                                    break;
                                }

                                nextHighlight++;
                            }

                            processingIdxStart = processingIdxEnd;
                        }
                    }
                }
            }

            if (changeBlock != null && _blockBorderPen != null)
            {
                if (index == changeBlock.Value.StartLine)
                {
                    drawingContext.DrawLine(_blockBorderPen, new Point(0, startY), new Point(width, startY));
                }

                if (index == changeBlock.Value.EndLine)
                {
                    drawingContext.DrawLine(_blockBorderPen, new Point(0, endY), new Point(width, endY));
                }
            }
        }
    }
}

/// <summary>
/// Transformer coloring @@ hunk indicator @@ header lines.
/// Adapted from SourceGit (MIT) — see docs/third-party-licenses.md.
/// </summary>
public sealed class DiffIndicatorTransformer : DocumentColorizingTransformer
{
    private readonly DiffPresenterBase _presenter;
    private IBrush? _indicatorBrush;
    private Typeface _indicatorTypeface;

    public DiffIndicatorTransformer(DiffPresenterBase presenter)
    {
        _presenter = presenter;
        _indicatorTypeface = new Typeface(_presenter.FontFamily, FontStyle.Italic);
        RefreshResources();
    }

    public void RefreshResources()
    {
        _indicatorBrush = _presenter.FindResource("ColorAccent") as IBrush ?? Brushes.DodgerBlue;
        _indicatorTypeface = new Typeface(_presenter.FontFamily, FontStyle.Italic);
    }

    protected override void ColorizeLine(DocumentLine line)
    {
        var rows = _presenter.Rows;
        var idx = line.LineNumber;
        if (idx > rows.Count)
        {
            return;
        }

        var row = rows[idx - 1];
        if (row.Type == DiffDisplayLineType.Indicator && _indicatorBrush != null)
        {
            ChangeLinePart(line.Offset, line.EndOffset, v =>
            {
                v.TextRunProperties.SetForegroundBrush(_indicatorBrush);
                v.TextRunProperties.SetTypeface(_indicatorTypeface);
            });
        }
    }
}
