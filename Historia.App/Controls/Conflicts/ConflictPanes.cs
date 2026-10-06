namespace Historia.Controls;

/// <summary>
/// Specialized CodeEditor for conflict resolution panes (Ours, Theirs, Result).
/// Adapted from SourceGit (MIT) — see docs/third-party-licenses.md.
/// </summary>
public class ConflictPaneEditor : CodeEditor
{
    private IDisposable? _syntaxSubscription;
    private string? _filePath;
    private ScrollViewer? _scrollViewer;
    private Vector _scrollOffset = Vector.Zero;

    public IServiceProvider? ServicesProvider { get; set; }

    public string? FilePath
    {
        get => _filePath;
        set
        {
            _filePath = value;
            if (_syntaxSubscription != null)
            {
                DetachSyntaxHighlighting();
                AttachSyntaxHighlighting();
            }
        }
    }

    public event EventHandler<Vector>? ScrollOffsetChanged;

    public ConflictPaneEditor()
    {
        WordWrap = false;
        Options.EnableHyperlinks = false;
        Options.EnableEmailHyperlinks = false;
        Options.EnableTextDragDrop = false;
        Options.AllowScrollBelowDocument = false;
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        UnhookScrollViewer();
        _scrollViewer = e.NameScope.Find<ScrollViewer>("PART_ScrollViewer") ?? this.FindDescendantOfType<ScrollViewer>();
        if (_scrollViewer != null)
        {
            _scrollViewer.ScrollChanged += OnScrollViewerScrollChanged;
            if (_scrollOffset != Vector.Zero)
            {
                _scrollViewer.Offset = _scrollOffset;
            }
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        AttachSyntaxHighlighting();
        if (_scrollViewer == null)
        {
            HookScrollViewer();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        UnhookScrollViewer();
        DetachSyntaxHighlighting();
        base.OnDetachedFromVisualTree(e);
    }

    private void HookScrollViewer()
    {
        _scrollViewer = this.FindDescendantOfType<ScrollViewer>();
        if (_scrollViewer != null)
        {
            _scrollViewer.ScrollChanged += OnScrollViewerScrollChanged;
            if (_scrollOffset != Vector.Zero)
            {
                _scrollViewer.Offset = _scrollOffset;
            }
        }
    }

    private void UnhookScrollViewer()
    {
        if (_scrollViewer != null)
        {
            _scrollViewer.ScrollChanged -= OnScrollViewerScrollChanged;
            _scrollViewer = null;
        }
    }

    private void OnScrollViewerScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_scrollViewer != null)
        {
            _scrollOffset = _scrollViewer.Offset;
            ScrollOffsetChanged?.Invoke(this, _scrollOffset);
        }
    }

    public Vector GetScrollOffset() => _scrollViewer?.Offset ?? _scrollOffset;

    public void SetScrollOffset(Vector offset)
    {
        _scrollOffset = offset;
        if (_scrollViewer != null)
        {
            _scrollViewer.Offset = offset;
        }
        else
        {
            ScrollToHorizontalOffset(offset.X);
            ScrollToVerticalOffset(offset.Y);
        }
        ScrollOffsetChanged?.Invoke(this, offset);
    }

    private void AttachSyntaxHighlighting()
    {
        if (_syntaxSubscription != null)
        {
            return;
        }

        var host = ServicesProvider?.GetService(typeof(SyntaxThemeHost)) as SyntaxThemeHost
                   ?? (Application.Current as App)?.Services?.GetService(typeof(SyntaxThemeHost)) as SyntaxThemeHost;

        if (host != null)
        {
            try
            {
                _syntaxSubscription = host.Attach(this, FilePath);
            }
            catch
            {
                // Fall back to plain text
            }
        }
    }

    private void DetachSyntaxHighlighting()
    {
        _syntaxSubscription?.Dispose();
        _syntaxSubscription = null;
    }
}

/// <summary>How a conflict pane row is washed: green for what is kept, red for what is dropped.</summary>
public enum ConflictRowTint { None, Add, Del, Base, Marker }

/// <summary>
/// Maps conflict pane and Result rows to their wash and gutter tints. Kept rows (and both sides'
/// lines of an unresolved block) take the diff addition colours, dropped rows the deletion colours,
/// and marker and base lines stay neutral.
/// </summary>
public static class ConflictRowTints
{
    /// <summary>The tint of a side-pane row.</summary>
    public static ConflictRowTint ForPane(ConflictPaneLineType type) => type switch
    {
        ConflictPaneLineType.Ours or ConflictPaneLineType.Theirs => ConflictRowTint.Add,
        ConflictPaneLineType.Marker => ConflictRowTint.Marker,
        _ => ConflictRowTint.None,
    };

    /// <summary>
    /// The tint of a Result line: a managed row by its visual, otherwise the line's role inside an
    /// unresolved marker block.
    /// </summary>
    public static ConflictRowTint ForResult(int lineIdx, ConflictRowVisual visual, ConflictParse? parse)
    {
        if (visual != ConflictRowVisual.None)
        {
            return ForManagedRow(visual);
        }

        var region = FindRegion(parse, lineIdx);
        return region == null ? ConflictRowTint.None : ForRegionLine(lineIdx, region);
    }

    /// <summary>The tint of a managed row.</summary>
    internal static ConflictRowTint ForManagedRow(ConflictRowVisual visual)
    {
        if ((visual & ConflictRowVisual.Excluded) != 0)
        {
            return ConflictRowTint.Del;
        }

        return (visual & (ConflictRowVisual.Ours | ConflictRowVisual.Theirs)) != 0 ? ConflictRowTint.Add : ConflictRowTint.None;
    }

    /// <summary>The tint of <paramref name="lineIdx"/>, a line of the unresolved marker block <paramref name="region"/>.</summary>
    internal static ConflictRowTint ForRegionLine(int lineIdx, ConflictRegion region)
    {
        if (lineIdx == region.StartLine || lineIdx == region.SeparatorLine || lineIdx == region.EndLine
            || (region.HasBase && lineIdx == region.BaseMarkerLine))
        {
            return ConflictRowTint.Marker;
        }

        if (lineIdx >= region.OursStartLine && lineIdx <= region.OursEndLine)
        {
            return ConflictRowTint.Add;
        }

        if (region.HasBase && lineIdx >= region.BaseStartLine && lineIdx <= region.BaseEndLine)
        {
            return ConflictRowTint.Base;
        }

        return lineIdx >= region.TheirsStartLine && lineIdx <= region.TheirsEndLine ? ConflictRowTint.Add : ConflictRowTint.None;
    }

    /// <summary>The marker block containing <paramref name="lineIdx"/>, or null.</summary>
    internal static ConflictRegion? FindRegion(ConflictParse? parse, int lineIdx)
    {
        if (parse == null)
        {
            return null;
        }

        var regions = parse.Regions;
        for (int i = 0; i < regions.Count; i++)
        {
            if (lineIdx >= regions[i].StartLine && lineIdx <= regions[i].EndLine)
            {
                return regions[i];
            }
        }

        return null;
    }

    /// <summary>The row wash for <paramref name="tint"/>, or null when the row is unwashed.</summary>
    public static IBrush? BackgroundBrush(ConflictRowTint tint) => tint switch
    {
        ConflictRowTint.Add => PaletteBrushes.Get("ColorDiffAddBg"),
        ConflictRowTint.Del => PaletteBrushes.Get("ColorDiffDelBg"),
        ConflictRowTint.Base => PaletteBrushes.Get("ColorConflictBaseBg"),
        ConflictRowTint.Marker => PaletteBrushes.Get("ColorConflictMarkerBg"),
        _ => null,
    };

    /// <summary>The line-number gutter tint for <paramref name="tint"/>, or null when the gutter is untinted.</summary>
    public static IBrush? GutterBrush(ConflictRowTint tint) => tint switch
    {
        ConflictRowTint.Add => PaletteBrushes.Get("ColorDiffAddGutter"),
        ConflictRowTint.Del => PaletteBrushes.Get("ColorDiffDelGutter"),
        _ => null,
    };
}

/// <summary>
/// Line number margin for conflict panes, numbered by the pane's own lines. A row that is kept or
/// dropped tints its whole gutter row.
/// Adapted from SourceGit (MIT) — see docs/third-party-licenses.md.
/// </summary>
public sealed class ConflictLineNumberMargin : AbstractMargin
{
    private const double Pad = 8;

    private readonly TextEditor _editor;
    private readonly Func<IReadOnlyList<ConflictPaneLine>?>? _getLines;
    private readonly Func<int, ConflictRowTint>? _getTint;
    private readonly ShapedTextCache _textCache = new(DiffLineNumberMargin.TextCacheCapacity);
    private Typeface _typeface;
    private double _lastFontSize;

    internal ShapedTextCache TextCacheForTesting => _textCache;

    /// <summary>The tint the margin paints behind 0-based <paramref name="row"/>.</summary>
    internal ConflictRowTint TintForRowForTesting(int row) => _getTint?.Invoke(row) ?? ConflictRowTint.None;

    /// <summary>Gutter rows the last <see cref="Render"/> tinted.</summary>
    internal int TintedRowsDrawnForTesting { get; private set; }

    /// <param name="getTint">How a row (0-based document line) tints its gutter; null leaves the
    /// gutter untinted.</param>
    public ConflictLineNumberMargin(
        TextEditor editor,
        Func<IReadOnlyList<ConflictPaneLine>?>? getLines = null,
        Func<int, ConflictRowTint>? getTint = null)
    {
        _editor = editor;
        _getLines = getLines;
        _getTint = getTint;
        _typeface = new Typeface(_editor.FontFamily, _editor.FontStyle, _editor.FontWeight);
        _lastFontSize = _editor.FontSize;
        ClipToBounds = true;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        PaletteBrushes.Changed += OnPaletteChanged;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        PaletteBrushes.Changed -= OnPaletteChanged;
        _textCache.Clear();
        base.OnDetachedFromVisualTree(e);
    }

    private void OnPaletteChanged(object? sender, EventArgs e)
    {
        _textCache.Clear();
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var view = TextView;
        if (view is not { VisualLinesValid: true })
        {
            return;
        }

        var typeface = new Typeface(_editor.FontFamily, _editor.FontStyle, _editor.FontWeight);
        if (_editor.FontSize != _lastFontSize || !typeface.Equals(_typeface))
        {
            _lastFontSize = _editor.FontSize;
            _typeface = typeface;
            _textCache.Clear();
        }

        TintedRowsDrawnForTesting = 0;
        var lines = _getLines?.Invoke();
        var foregroundBrush = PaletteBrushes.Get("ColorTextFaint");

        foreach (var line in view.VisualLines)
        {
            if (line.IsDisposed || line.FirstDocumentLine == null || line.FirstDocumentLine.IsDeleted)
            {
                continue;
            }

            int docLineNumber = line.FirstDocumentLine.LineNumber;
            string numText;

            if (lines != null)
            {
                if (docLineNumber > lines.Count)
                {
                    break;
                }
                var paneLine = lines[docLineNumber - 1];
                numText = paneLine.LineNumber is { } n ? n.ToString(CultureInfo.InvariantCulture) : string.Empty;
            }
            else
            {
                numText = docLineNumber.ToString(CultureInfo.InvariantCulture);
            }

            if (_getTint != null && ConflictRowTints.GutterBrush(_getTint(docLineNumber - 1)) is { } tint)
            {
                DiffGutterTint.FillRow(context, view, line, Bounds.Width, tint);
                TintedRowsDrawnForTesting++;
            }

            if (string.IsNullOrEmpty(numText))
            {
                continue;
            }

            var y = line.GetTextLineVisualYPosition(line.TextLines[0], VisualYPosition.LineMiddle) - view.VerticalOffset;
            var txt = _textCache.Get(numText, _typeface, _lastFontSize, foregroundBrush);
            txt.Draw(context, new Point(Bounds.Width - Pad - txt.Width, y - (txt.Height * 0.5)));
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var max = _editor.Document?.LineCount ?? 100;
        var test = new FormattedText(
            Math.Max(100, max).ToString(CultureInfo.InvariantCulture),
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            _typeface,
            _editor.FontSize,
            Brushes.White);
        return new Size(test.Width + (2 * Pad), 0);
    }
}

/// <summary>What the toggle gutter draws on a line.</summary>
public enum ConflictToggleGlyph { None, Unchecked, Partial, Checked }

/// <summary>How the Result pane paints a line of a managed conflict.</summary>
[Flags]
public enum ConflictRowVisual
{
    None = 0,
    Ours = 1,
    Theirs = 2,
    Added = 4,
    Excluded = 8,
    RegionTop = 16,
    RegionBottom = 32,
}

/// <summary>
/// Check-box gutter for conflict panes: one box per line the owner says has one, clicked to
/// toggle. The box, check and dash are drawn from geometries built once per size and translated
/// per line, with palette pens and brushes resolved once per palette, so a repeat frame creates
/// nothing.
/// </summary>
public sealed class ConflictToggleMargin : AbstractMargin
{
    public const double MarginWidth = 16;

    private static readonly Cursor HandCursor = new(StandardCursorType.Hand);

    private readonly TextEditor _editor;
    private readonly Func<int, ConflictToggleGlyph> _getGlyph;
    private readonly Func<int, string?>? _getTip;

    private double _boxSize;
    private StreamGeometry? _checkGeometry;
    private StreamGeometry? _dashGeometry;
    private IBrush? _boxBrush;
    private IBrush? _accentBrush;
    private IPen? _outlinePen;
    private IPen? _accentPen;
    private IPen? _markPen;
    private int _hoverLine = -1;

    /// <summary>Raised with the 0-based document line whose box was clicked.</summary>
    public event EventHandler<int>? ToggleClicked;

    internal int PoolMissesForTesting { get; private set; }
    internal int RenderCountForTesting { get; private set; }
    internal void ResetPoolCountersForTesting() => PoolMissesForTesting = 0;

    public ConflictToggleMargin(TextEditor editor, Func<int, ConflictToggleGlyph> getGlyph, Func<int, string?>? getTip = null)
    {
        _editor = editor;
        _getGlyph = getGlyph;
        _getTip = getTip;
        ClipToBounds = true;
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
        _boxBrush = null;
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize) => new(MarginWidth, 0);

    public override void Render(DrawingContext context)
    {
        RenderCountForTesting++;
        var view = TextView;
        if (view is not { VisualLinesValid: true })
        {
            return;
        }

        EnsureResources(Math.Clamp(Math.Floor(view.DefaultLineHeight) - 4, 8, 12));
        double s = _boxSize;
        double x = Math.Floor((Bounds.Width - s) / 2);

        foreach (var line in view.VisualLines)
        {
            if (line.IsDisposed || line.FirstDocumentLine == null || line.FirstDocumentLine.IsDeleted)
            {
                continue;
            }

            var glyph = _getGlyph(line.FirstDocumentLine.LineNumber - 1);
            if (glyph == ConflictToggleGlyph.None)
            {
                continue;
            }

            var tl = line.TextLines[0];
            double top = line.GetTextLineVisualYPosition(tl, VisualYPosition.LineTop) - view.VerticalOffset;
            double bottom = line.GetTextLineVisualYPosition(tl, VisualYPosition.LineBottom) - view.VerticalOffset;
            double y = Math.Floor(top + (bottom - top - s) / 2);
            var rect = new Rect(x + 0.5, y + 0.5, s - 1, s - 1);

            if (glyph == ConflictToggleGlyph.Unchecked)
            {
                context.DrawRectangle(_boxBrush, _outlinePen, rect, 2, 2);
                continue;
            }

            context.DrawRectangle(_accentBrush, _accentPen, rect, 2, 2);
            using (context.PushTransform(Matrix.CreateTranslation(x, y)))
            {
                context.DrawGeometry(null, _markPen, glyph == ConflictToggleGlyph.Checked ? _checkGeometry! : _dashGeometry!);
            }
        }
    }

    private void EnsureResources(double size)
    {
        if (_boxBrush == null)
        {
            PoolMissesForTesting++;
            _boxBrush = PaletteBrushes.Get("ColorBg");
            _accentBrush = PaletteBrushes.Get("ColorAccent");
            _outlinePen = PaletteBrushes.Pen("ColorTextMuted", 1);
            _accentPen = PaletteBrushes.Pen("ColorAccent", 1);
            _markPen = PaletteBrushes.Pen("ColorBg", 1.5);
        }

        if (_checkGeometry == null || size != _boxSize)
        {
            PoolMissesForTesting++;
            _boxSize = size;

            var check = new StreamGeometry();
            using (var ctx = check.Open())
            {
                ctx.BeginFigure(new Point(size * 0.24, size * 0.52), false);
                ctx.LineTo(new Point(size * 0.42, size * 0.70));
                ctx.LineTo(new Point(size * 0.76, size * 0.32));
                ctx.EndFigure(false);
            }
            _checkGeometry = check;

            var dash = new StreamGeometry();
            using (var ctx = dash.Open())
            {
                ctx.BeginFigure(new Point(size * 0.26, size * 0.5), false);
                ctx.LineTo(new Point(size * 0.74, size * 0.5));
                ctx.EndFigure(false);
            }
            _dashGeometry = dash;
        }
    }

    private int LineAt(double y)
    {
        var view = TextView;
        if (view is not { VisualLinesValid: true })
        {
            return -1;
        }

        var docLine = view.GetDocumentLineByVisualTop(y + view.VerticalOffset);
        return docLine == null ? -1 : docLine.LineNumber - 1;
    }

    /// <summary>Raises <see cref="ToggleClicked"/> for the box at <paramref name="y"/> (margin
    /// coordinates), if that line has one.</summary>
    internal bool ToggleAt(double y)
    {
        int line = LineAt(y);
        if (line < 0 || _getGlyph(line) == ConflictToggleGlyph.None)
        {
            return false;
        }

        ToggleClicked?.Invoke(this, line);
        return true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && ToggleAt(e.GetPosition(this).Y))
        {
            e.Handled = true;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        int line = LineAt(e.GetPosition(this).Y);
        if (line == _hoverLine)
        {
            return;
        }

        _hoverLine = line;
        bool hasBox = line >= 0 && _getGlyph(line) != ConflictToggleGlyph.None;
        Cursor = hasBox ? HandCursor : null;
        ToolTip.SetTip(this, hasBox ? _getTip?.Invoke(line) : null);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hoverLine = -1;
        Cursor = null;
        ToolTip.SetTip(this, null);
    }
}

/// <summary>
/// Dims and strikes through the lines a managed conflict will not save, so they stay visible
/// (and line-aligned with the side panes) while reading as dropped.
/// </summary>
public sealed class ConflictExcludedLineColorizer : DocumentColorizingTransformer
{
    private static readonly TextDecorationCollection Strikethrough = TextDecorations.Strikethrough;

    private readonly Func<int, bool> _isExcluded;
    private readonly Action<VisualLineElement> _apply;

    public ConflictExcludedLineColorizer(Func<int, bool> isExcluded)
    {
        _isExcluded = isExcluded;
        _apply = Apply;
    }

    protected override void ColorizeLine(DocumentLine line)
    {
        if (line.Length > 0 && _isExcluded(line.LineNumber - 1))
        {
            ChangeLinePart(line.Offset, line.EndOffset, _apply);
        }
    }

    private static void Apply(VisualLineElement element)
    {
        element.TextRunProperties.SetForegroundBrush(PaletteBrushes.Get("ColorTextFaint"));
        element.TextRunProperties.SetTextDecorations(Strikethrough);
    }
}

/// <summary>
/// Background renderer painting conflict region washes, marker bars, and intra-line highlights.
/// Adapted from SourceGit (MIT) — see docs/third-party-licenses.md.
/// </summary>
public sealed class ConflictBackgroundRenderer : SafeBackgroundRenderer
{
    private readonly TextEditor _editor;
    private readonly Func<ConflictSide?> _getSide;
    private readonly Func<ConflictParse?> _getParse;
    private readonly Func<IReadOnlyList<ConflictPaneLine>?>? _getLines;
    private readonly Func<IReadOnlyList<ConflictRegionAnalysis>?>? _getAnalysis;
    private readonly Func<int, ConflictRowVisual>? _getRowVisual;

    public override KnownLayer Layer => KnownLayer.Background;

    /// <summary>Managed rows and marker-region lines painted by the last Result pass.</summary>
    internal int RowLinesDrawnForTesting { get; private set; }
    internal int RegionLinesDrawnForTesting { get; private set; }

    /// <param name="getRowVisual">Result pane only: how a managed-region line (0-based document
    /// line) is painted; <see cref="ConflictRowVisual.None"/> for every other line.</param>
    public ConflictBackgroundRenderer(
        TextEditor editor,
        Func<ConflictSide?> getSide,
        Func<ConflictParse?> getParse,
        Func<IReadOnlyList<ConflictPaneLine>?>? getLines = null,
        Func<IReadOnlyList<ConflictRegionAnalysis>?>? getAnalysis = null,
        Func<int, ConflictRowVisual>? getRowVisual = null)
    {
        _editor = editor;
        _getSide = getSide;
        _getParse = getParse;
        _getLines = getLines;
        _getAnalysis = getAnalysis;
        _getRowVisual = getRowVisual;
    }

    protected override void DrawCore(TextView textView, DrawingContext drawingContext)
    {
        var parse = _getParse();
        var side = _getSide();
        var width = textView.Bounds.Width;

        if (side is { } currentSide)
        {
            // Ours or Theirs side pane
            var lines = _getLines?.Invoke();
            if (lines == null || lines.Count == 0)
            {
                return;
            }

            var changedTextBrush = PaletteBrushes.Get("ColorDiffAddHl");

            var analyses = _getAnalysis?.Invoke();
            Dictionary<int, RegionLineHighlight>? hlMap = null;
            if (analyses != null && analyses.Count > 0)
            {
                hlMap = new Dictionary<int, RegionLineHighlight>();
                foreach (var a in analyses)
                {
                    var list = currentSide == ConflictSide.Ours ? a.OursHighlights : a.TheirsHighlights;
                    foreach (var h in list)
                    {
                        if (h.Spans.Count > 0)
                        {
                            hlMap[h.DocumentLineIndex] = h;
                        }
                    }
                }
            }

            foreach (var line in textView.VisualLines)
            {
                if (line.IsDisposed || line.FirstDocumentLine == null || line.FirstDocumentLine.IsDeleted)
                {
                    continue;
                }

                int index = line.FirstDocumentLine.LineNumber - 1;
                if (index >= lines.Count)
                {
                    break;
                }

                var paneLine = lines[index];
                var lastTextLine = line.TextLines[^1];
                var startY = line.GetTextLineVisualYPosition(line.TextLines[0], VisualYPosition.LineTop) - textView.VerticalOffset;
                var endY = line.GetTextLineVisualYPosition(lastTextLine, VisualYPosition.LineBottom) - textView.VerticalOffset;

                var bg = ConflictRowTints.BackgroundBrush(ConflictRowTints.ForPane(paneLine.Type));
                if (bg != null)
                {
                    drawingContext.DrawRectangle(bg, null, new Rect(0, startY, width, endY - startY));
                }

                if (hlMap != null && hlMap.TryGetValue(paneLine.DocumentLineIndex, out var hl) && hl.Spans.Count > 0)
                {
                    var hlBrush = changedTextBrush;

                    int processingIdxStart = 0;
                    int processingIdxEnd = 0;
                    int nextHighlight = 0;

                    foreach (var tl in line.TextLines)
                    {
                        processingIdxEnd += tl.Length;
                        var y = line.GetTextLineVisualYPosition(tl, VisualYPosition.LineTop) - textView.VerticalOffset;
                        var h = line.GetTextLineVisualYPosition(tl, VisualYPosition.LineBottom) - textView.VerticalOffset - y;

                        while (nextHighlight < hl.Spans.Count)
                        {
                            var highlight = hl.Spans[nextHighlight];
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
                                drawingContext.DrawRectangle(hlBrush, null, new Rect(x, y, w, h));
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
        else
        {
            // Result editor: managed rows from the row visuals, marker regions from the parse. A
            // file whose conflicts are all managed has no marker regions but still has rows.
            RowLinesDrawnForTesting = 0;
            RegionLinesDrawnForTesting = 0;
            bool hasRegions = parse != null && parse.Regions.Count > 0;
            if (!hasRegions && _getRowVisual == null)
            {
                return;
            }

            var blockBorderPen = PaletteBrushes.Pen("ColorAccent", 1);

            foreach (var line in textView.VisualLines)
            {
                if (line.IsDisposed || line.FirstDocumentLine == null || line.FirstDocumentLine.IsDeleted)
                {
                    continue;
                }

                int lineIdx = line.FirstDocumentLine.LineNumber - 1;

                var visual = _getRowVisual?.Invoke(lineIdx) ?? ConflictRowVisual.None;
                if (visual != ConflictRowVisual.None)
                {
                    var rowTop = line.GetTextLineVisualYPosition(line.TextLines[0], VisualYPosition.LineTop) - textView.VerticalOffset;
                    var rowBottom = line.GetTextLineVisualYPosition(line.TextLines[^1], VisualYPosition.LineBottom) - textView.VerticalOffset;
                    RowLinesDrawnForTesting++;
                    var rowBg = ConflictRowTints.BackgroundBrush(ConflictRowTints.ForManagedRow(visual));
                    if (rowBg != null)
                    {
                        drawingContext.DrawRectangle(rowBg, null, new Rect(0, rowTop, width, rowBottom - rowTop));
                    }
                    if ((visual & ConflictRowVisual.RegionTop) != 0)
                    {
                        drawingContext.DrawLine(blockBorderPen, new Point(0, rowTop), new Point(width, rowTop));
                    }
                    if ((visual & ConflictRowVisual.RegionBottom) != 0)
                    {
                        drawingContext.DrawLine(blockBorderPen, new Point(0, rowBottom), new Point(width, rowBottom));
                    }
                    continue;
                }

                if (!hasRegions)
                {
                    continue;
                }

                var region = ConflictRowTints.FindRegion(parse, lineIdx);

                if (region == null)
                {
                    continue;
                }

                RegionLinesDrawnForTesting++;
                var lastTextLine = line.TextLines[^1];
                var startY = line.GetTextLineVisualYPosition(line.TextLines[0], VisualYPosition.LineTop) - textView.VerticalOffset;
                var endY = line.GetTextLineVisualYPosition(lastTextLine, VisualYPosition.LineBottom) - textView.VerticalOffset;
                var regionBg = ConflictRowTints.BackgroundBrush(ConflictRowTints.ForRegionLine(lineIdx, region));
                if (regionBg != null)
                {
                    drawingContext.DrawRectangle(regionBg, null, new Rect(0, startY, width, endY - startY));
                }

                // Region border
                if (lineIdx == region.StartLine)
                {
                    drawingContext.DrawLine(blockBorderPen, new Point(0, startY), new Point(width, startY));
                }
                if (lineIdx == region.EndLine)
                {
                    drawingContext.DrawLine(blockBorderPen, new Point(0, endY), new Point(width, endY));
                }
            }
        }
    }
}

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

/// <summary>
/// Helper for incremental text document updates that avoid full re-tokenization.
/// Adapted from SourceGit (MIT) — see docs/third-party-licenses.md.
/// </summary>
public static class ConflictPaneUpdateHelper
{
    public static void UpdateDocumentIncremental(TextDocument document, IReadOnlyList<ConflictPaneLine> newLines)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(newLines);

        var newText = string.Join('\n', newLines.Select(l => l.Text));
        var oldText = document.Text;

        if (oldText == newText)
        {
            return;
        }

        if (string.IsNullOrEmpty(oldText))
        {
            document.Text = newText;
            return;
        }

        // Find common prefix length
        int prefixLen = 0;
        int minLen = Math.Min(oldText.Length, newText.Length);
        while (prefixLen < minLen && oldText[prefixLen] == newText[prefixLen])
        {
            prefixLen++;
        }

        // Find common suffix length
        int suffixLen = 0;
        while (suffixLen < (minLen - prefixLen) &&
               oldText[oldText.Length - 1 - suffixLen] == newText[newText.Length - 1 - suffixLen])
        {
            suffixLen++;
        }

        int replaceStart = prefixLen;
        int replaceLength = oldText.Length - prefixLen - suffixLen;
        string replacement = newText.Substring(prefixLen, newText.Length - prefixLen - suffixLen);

        document.Replace(replaceStart, replaceLength, replacement);
    }
}
