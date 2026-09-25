namespace Historia.Theming;

/// <summary>
/// Fast, zero-allocation palette brush and pen cache for rendering controls.
/// Cleared and invalidated whenever the active theme or palette changes.
/// </summary>
public static class PaletteBrushes
{
    private static readonly Dictionary<string, IBrush> BrushCache = new(StringComparer.Ordinal);
    private static readonly Dictionary<(string Key, byte Alpha), IBrush> TranslucentBrushCache = [];
    private static readonly Dictionary<(string Key, double Thickness), IPen> PenCache = [];

    public static event EventHandler? Changed;

    public static IBrush Get(string resourceKey)
    {
        if (BrushCache.TryGetValue(resourceKey, out var brush))
            return brush;

        var app = Application.Current;
        if (app is not null && app.TryFindResource(resourceKey, out var res) && res is IBrush b)
        {
            BrushCache[resourceKey] = b;
            return b;
        }

        return Brushes.Transparent;
    }

    public static IBrush GetTranslucent(string resourceKey, byte alpha = 0x30)
    {
        var key = (resourceKey, alpha);
        if (TranslucentBrushCache.TryGetValue(key, out var brush))
            return brush;

        var baseBrush = Get(resourceKey);
        if (baseBrush is ISolidColorBrush scb)
        {
            brush = new ImmutableSolidColorBrush(Color.FromArgb(alpha, scb.Color.R, scb.Color.G, scb.Color.B));
        }
        else
        {
            brush = new ImmutableSolidColorBrush(Color.FromArgb(alpha, 0x4A, 0x90, 0xE2));
        }

        TranslucentBrushCache[key] = brush;
        return brush;
    }

    public static IPen Pen(string resourceKey, double thickness)
    {
        var key = (resourceKey, thickness);
        if (PenCache.TryGetValue(key, out var pen))
            return pen;

        var brush = Get(resourceKey);
        pen = new Pen(brush, thickness);
        PenCache[key] = pen;
        return pen;
    }

    public static void Invalidate()
    {
        BrushCache.Clear();
        TranslucentBrushCache.Clear();
        PenCache.Clear();
        Changed?.Invoke(null, EventArgs.Empty);
    }
}
