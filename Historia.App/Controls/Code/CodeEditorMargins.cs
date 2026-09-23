namespace Historia.Controls;

/// <summary>
/// Vertical 1px separator margin between margins or between margins and the code viewport.
/// </summary>
public sealed class VerticalSeparatorMargin : AbstractMargin
{
    private Pen? _cachedPen;

    public VerticalSeparatorMargin()
    {
        ClipToBounds = true;
        ActualThemeVariantChanged += (_, _) =>
        {
            _cachedPen = null;
            InvalidateVisual();
        };
    }

    protected override Size MeasureOverride(Size availableSize) => new(1, 0);

    public override void Render(DrawingContext context)
    {
        if (_cachedPen is null)
        {
            if (this.TryFindResource("ColorBorder", out var res) && res is IBrush b)
            {
                _cachedPen = new Pen(b, 1);
            }
            else
            {
                _cachedPen = new Pen(Brushes.Gray, 1);
            }
        }

        context.DrawLine(_cachedPen, new Point(0.5, 0), new Point(0.5, Bounds.Height));
    }
}

/// <summary>
/// Custom line number margin ported from SourceGit, supporting pluggable per-line label formatting.
/// </summary>
public sealed class CodeLineNumberMargin : AbstractMargin
{
    private IBrush? _cachedForeground;

    /// <summary>
    /// Optional pluggable line label provider given 1-based document line number.
    /// Returning null or empty omits the label (useful for padding lines in aligned diff/merge views).
    /// </summary>
    public Func<int, string?>? LineLabelProvider { get; set; }

    /// <summary>
    /// Highest expected line number, used to reserve appropriate margin width.
    /// </summary>
    public int MaxLineNumber { get; set; } = 1;

    public CodeLineNumberMargin()
    {
        Margin = new Thickness(8, 0);
        ClipToBounds = true;
        ActualThemeVariantChanged += (_, _) =>
        {
            _cachedForeground = null;
            InvalidateVisual();
        };
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var typeface = new Typeface(TextArea?.FontFamily ?? FontFamily.Default);
        double fontSize = TextArea is not null ? TextArea.FontSize : 12.0;

        string sample = MaxLineNumber > 0 ? MaxLineNumber.ToString(CultureInfo.InvariantCulture) : "999";
        if (sample.Length < 3)
        {
            sample = "999";
        }

        var test = new FormattedText(
            sample,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            typeface,
            fontSize,
            Brushes.White);

        return new Size(test.Width + 16, 0);
    }

    public override void Render(DrawingContext context)
    {
        var view = TextView;
        if (view is not { VisualLinesValid: true })
        {
            return;
        }

        if (_cachedForeground is null)
        {
            if (this.TryFindResource("ColorTextFaint", out var res) && res is IBrush b)
            {
                _cachedForeground = b;
            }
            else if (this.TryFindResource("ColorTextMuted", out var resMuted) && resMuted is IBrush bm)
            {
                _cachedForeground = bm;
            }
            else
            {
                _cachedForeground = Brushes.Gray;
            }
        }

        var typeface = new Typeface(TextArea?.FontFamily ?? FontFamily.Default);
        double fontSize = TextArea is not null ? TextArea.FontSize : 12.0;

        foreach (var line in view.VisualLines)
        {
            if (line.IsDisposed || line.FirstDocumentLine == null || line.FirstDocumentLine.IsDeleted)
            {
                continue;
            }

            int lineNumber = line.FirstDocumentLine.LineNumber;
            string? label = LineLabelProvider != null
                ? LineLabelProvider(lineNumber)
                : lineNumber.ToString(CultureInfo.InvariantCulture);

            if (string.IsNullOrEmpty(label))
            {
                continue;
            }

            double y = line.GetTextLineVisualYPosition(line.TextLines[0], VisualYPosition.LineMiddle) - view.VerticalOffset;
            var txt = new FormattedText(
                label,
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                typeface,
                fontSize,
                _cachedForeground);

            context.DrawText(txt, new Point(Bounds.Width - txt.Width - 4, y - (txt.Height * 0.5)));
        }
    }
}
