namespace Historia.Controls;

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
