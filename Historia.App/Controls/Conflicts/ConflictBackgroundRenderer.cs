namespace Historia.Controls;

/// <summary>
/// Background renderer painting conflict region washes, marker bars, and intra-line highlights.
/// Adapted from SourceGit (MIT) — see docs/third-party-licenses.md.
/// </summary>
public sealed class ConflictBackgroundRenderer : SafeBackgroundRenderer
{
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
        Func<ConflictSide?> getSide,
        Func<ConflictParse?> getParse,
        Func<IReadOnlyList<ConflictPaneLine>?>? getLines = null,
        Func<IReadOnlyList<ConflictRegionAnalysis>?>? getAnalysis = null,
        Func<int, ConflictRowVisual>? getRowVisual = null)
    {
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
