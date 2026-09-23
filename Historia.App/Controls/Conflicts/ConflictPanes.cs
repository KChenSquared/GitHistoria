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

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        AttachSyntaxHighlighting();
        HookScrollViewer();
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

/// <summary>
/// Line number margin for conflict panes with 1:1 aligned document line numbers.
/// Adapted from SourceGit (MIT) — see docs/third-party-licenses.md.
/// </summary>
public sealed class ConflictLineNumberMargin : AbstractMargin
{
    private readonly TextEditor _editor;
    private readonly Func<IReadOnlyList<ConflictPaneLine>?>? _getLines;
    private IBrush? _foregroundBrush;
    private Typeface _typeface;

    public ConflictLineNumberMargin(TextEditor editor, Func<IReadOnlyList<ConflictPaneLine>?>? getLines = null)
    {
        _editor = editor;
        _getLines = getLines;
        _typeface = new Typeface(_editor.FontFamily, _editor.FontStyle, _editor.FontWeight);
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
        _typeface = new Typeface(_editor.FontFamily, _editor.FontStyle, _editor.FontWeight);
    }

    public override void Render(DrawingContext context)
    {
        var view = TextView;
        if (view is not { VisualLinesValid: true } || _foregroundBrush == null)
        {
            return;
        }

        var lines = _getLines?.Invoke();

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

            if (string.IsNullOrEmpty(numText))
            {
                continue;
            }

            var y = line.GetTextLineVisualYPosition(line.TextLines[0], VisualYPosition.LineMiddle) - view.VerticalOffset;
            var txt = new FormattedText(
                numText,
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                _typeface,
                _editor.FontSize,
                _foregroundBrush);

            context.DrawText(txt, new Point(Bounds.Width - txt.Width, y - (txt.Height * 0.5)));
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
        return new Size(test.Width + 8, 0);
    }
}

/// <summary>
/// Background renderer painting conflict region washes, base sections, marker bars, and intra-line highlights.
/// Adapted from SourceGit (MIT) — see docs/third-party-licenses.md.
/// </summary>
public sealed class ConflictBackgroundRenderer : SafeBackgroundRenderer
{
    private readonly TextEditor _editor;
    private readonly Func<ConflictSide?> _getSide;
    private readonly Func<ConflictParse?> _getParse;
    private readonly Func<IReadOnlyList<ConflictPaneLine>?>? _getLines;
    private readonly Func<IReadOnlyList<ConflictRegionAnalysis>?>? _getAnalysis;

    private IBrush? _oursBg;
    private IBrush? _theirsBg;
    private IBrush? _baseBg;
    private IBrush? _markerBg;
    private IBrush? _resolvedBg;
    private IBrush? _oursHl;
    private IBrush? _theirsHl;
    private IPen? _blockBorderPen;

    public override KnownLayer Layer => KnownLayer.Background;

    public ConflictBackgroundRenderer(
        TextEditor editor,
        Func<ConflictSide?> getSide,
        Func<ConflictParse?> getParse,
        Func<IReadOnlyList<ConflictPaneLine>?>? getLines = null,
        Func<IReadOnlyList<ConflictRegionAnalysis>?>? getAnalysis = null)
    {
        _editor = editor;
        _getSide = getSide;
        _getParse = getParse;
        _getLines = getLines;
        _getAnalysis = getAnalysis;
        RefreshResources();
    }

    public void RefreshResources()
    {
        _oursBg = _editor.FindResource("ColorConflictOursBg") as IBrush ?? Brushes.Transparent;
        _theirsBg = _editor.FindResource("ColorConflictTheirsBg") as IBrush ?? Brushes.Transparent;
        _baseBg = _editor.FindResource("ColorConflictBaseBg") as IBrush ?? Brushes.Transparent;
        _markerBg = _editor.FindResource("ColorConflictMarkerBg") as IBrush ?? Brushes.Transparent;
        _resolvedBg = _editor.FindResource("ColorConflictResolvedBg") as IBrush ?? Brushes.Transparent;

        _oursHl = _editor.FindResource("ColorDiffAddHl") as IBrush ?? Brushes.Transparent;
        _theirsHl = _editor.FindResource("ColorDiffDelHl") as IBrush ?? Brushes.Transparent;

        var accent = _editor.FindResource("ColorAccent") as IBrush ?? Brushes.DodgerBlue;
        _blockBorderPen = new Pen(accent, 1);
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

                IBrush? bg = paneLine.Type switch
                {
                    ConflictPaneLineType.Ours => _oursBg,
                    ConflictPaneLineType.Theirs => _theirsBg,
                    ConflictPaneLineType.Base => _baseBg,
                    ConflictPaneLineType.Marker => _markerBg,
                    _ => null,
                };

                if (bg != null)
                {
                    drawingContext.DrawRectangle(bg, null, new Rect(0, startY, width, endY - startY));
                }
            }
        }
        else
        {
            // Result editor: draw based on parse regions
            if (parse == null || parse.Regions.Count == 0)
            {
                return;
            }

            foreach (var line in textView.VisualLines)
            {
                if (line.IsDisposed || line.FirstDocumentLine == null || line.FirstDocumentLine.IsDeleted)
                {
                    continue;
                }

                int lineIdx = line.FirstDocumentLine.LineNumber - 1;
                var region = parse.Regions.FirstOrDefault(r => lineIdx >= r.StartLine && lineIdx <= r.EndLine);

                if (region == null)
                {
                    continue;
                }

                var lastTextLine = line.TextLines[^1];
                var startY = line.GetTextLineVisualYPosition(line.TextLines[0], VisualYPosition.LineTop) - textView.VerticalOffset;
                var endY = line.GetTextLineVisualYPosition(lastTextLine, VisualYPosition.LineBottom) - textView.VerticalOffset;
                var rect = new Rect(0, startY, width, endY - startY);

                if (lineIdx == region.StartLine || lineIdx == region.SeparatorLine || lineIdx == region.EndLine
                    || (region.HasBase && lineIdx == region.BaseMarkerLine))
                {
                    if (_markerBg != null)
                    {
                        drawingContext.DrawRectangle(_markerBg, null, rect);
                    }
                }
                else if (lineIdx >= region.OursStartLine && lineIdx <= region.OursEndLine)
                {
                    if (_oursBg != null)
                    {
                        drawingContext.DrawRectangle(_oursBg, null, rect);
                    }
                }
                else if (region.HasBase && lineIdx >= region.BaseStartLine && lineIdx <= region.BaseEndLine)
                {
                    if (_baseBg != null)
                    {
                        drawingContext.DrawRectangle(_baseBg, null, rect);
                    }
                }
                else if (lineIdx >= region.TheirsStartLine && lineIdx <= region.TheirsEndLine)
                {
                    if (_theirsBg != null)
                    {
                        drawingContext.DrawRectangle(_theirsBg, null, rect);
                    }
                }

                // Region border
                if (_blockBorderPen != null)
                {
                    if (lineIdx == region.StartLine)
                    {
                        drawingContext.DrawLine(_blockBorderPen, new Point(0, startY), new Point(width, startY));
                    }
                    if (lineIdx == region.EndLine)
                    {
                        drawingContext.DrawLine(_blockBorderPen, new Point(0, endY), new Point(width, endY));
                    }
                }
            }
        }
    }
}

/// <summary>
/// Overview minimap rendering conflict regions and unresolved counts.
/// Adapted from SourceGit (MIT) — see docs/third-party-licenses.md.
/// </summary>
public sealed class ConflictResolverMinimap : Control
{
    private ConflictParse? _parse;
    private (int Start, int End)? _displayRange;
    private IBrush? _conflictBrush;
    private IBrush? _rangeBrush;
    private IPen? _rangePen;

    public ConflictParse? Parse
    {
        get => _parse;
        set
        {
            _parse = value;
            InvalidateVisual();
        }
    }

    public (int Start, int End)? DisplayRange
    {
        get => _displayRange;
        set
        {
            _displayRange = value;
            InvalidateVisual();
        }
    }

    public event EventHandler<int>? LineNavigationRequested;

    public ConflictResolverMinimap()
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
        _conflictBrush = this.FindResource("ColorWarning") as IBrush ?? Brushes.Orange;
        var accent = this.FindResource("ColorAccent") as IBrush ?? Brushes.RoyalBlue;
        _rangeBrush = accent;
        _rangePen = new Pen(accent, 1);
    }

    public override void Render(DrawingContext context)
    {
        context.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, Bounds.Width, Bounds.Height));

        if (_parse == null || _parse.LineCount == 0 || Bounds.Height <= 0)
        {
            return;
        }

        int total = _parse.LineCount;

        if (_conflictBrush != null)
        {
            foreach (var region in _parse.Regions)
            {
                double startY = region.StartLine / (double)total * Bounds.Height;
                double h = Math.Max(2.0, (region.EndLine - region.StartLine + 1) / (double)total * Bounds.Height);
                context.DrawRectangle(_conflictBrush, null, new Rect(0, startY, Bounds.Width, h));
            }
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
