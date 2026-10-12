namespace Historia.Controls;

/// <summary>
/// A monochrome icon glyph for AXAML: <c>&lt;ctl:IconGlyph Icon="close" Size="12" /&gt;</c>.
/// It draws through <see cref="ViewKit.IconGlyphPath"/>, so it looks exactly like a glyph built in
/// code. The path is rebuilt only when <see cref="Icon"/>, <see cref="Size"/> or
/// <see cref="ForegroundKey"/> changes; a theme change needs no rebuild because the path's fill is
/// a dynamic resource that re-resolves on its own.
/// </summary>
public sealed class IconGlyph : ContentControl
{
    public static readonly StyledProperty<string?> IconProperty =
        AvaloniaProperty.Register<IconGlyph, string?>(nameof(Icon));

    public static readonly StyledProperty<double> SizeProperty =
        AvaloniaProperty.Register<IconGlyph, double>(nameof(Size), 16d);

    public static readonly StyledProperty<string> ForegroundKeyProperty =
        AvaloniaProperty.Register<IconGlyph, string>(nameof(ForegroundKey), "ColorText");

    static IconGlyph()
    {
        IconProperty.Changed.AddClassHandler<IconGlyph>((g, _) => g.Rebuild());
        SizeProperty.Changed.AddClassHandler<IconGlyph>((g, _) => g.Rebuild());
        ForegroundKeyProperty.Changed.AddClassHandler<IconGlyph>((g, _) => g.Rebuild());
    }

    /// <summary>Name of the glyph in <see cref="Historia.Components.IconPaths"/>; null or empty draws nothing.</summary>
    public string? Icon
    {
        get => GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    /// <summary>Edge length in pixels of the square the glyph is drawn in.</summary>
    public double Size
    {
        get => GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    /// <summary>Palette resource key (for example <c>ColorTextMuted</c>) the glyph is filled with.</summary>
    public string ForegroundKey
    {
        get => GetValue(ForegroundKeyProperty);
        set => SetValue(ForegroundKeyProperty, value);
    }

    private void Rebuild()
    {
        Content = string.IsNullOrEmpty(Icon) ? null : IconGlyphPath(Icon, Size, ForegroundKey);
    }
}
