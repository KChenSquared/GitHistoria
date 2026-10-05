namespace Historia.Theming;

/// <summary>
/// The App-side half of theming. <see cref="ThemeService"/> (in Core) decides the
/// effective theme; this rebuilds the Avalonia resource dictionary from <see cref="HistoriaPalette"/>
/// on every change and merges it into <see cref="Application"/>.<c>Resources</c> , keyed by
/// <see cref="HistoriaPalette.ResourceKey"/>, so <c>{DynamicResource ColorBorder}</c> and
/// friends resolve. It also drives <see cref="Application"/>.<c>
/// RequestedThemeVariant</c> so the Fluent controls follow.
/// </summary>
public sealed class AvaloniaThemeApplier : IDisposable
{
    private readonly IThemeService _theme;
    private ResourceDictionary? _applied;

    public AvaloniaThemeApplier(IThemeService theme)
    {
        _theme = theme;
        _theme.ThemeChanged += OnThemeChanged;
        Apply(_theme.Effective);
    }

    private void OnThemeChanged(object? sender, ThemeChangedEventArgs e)
        => Dispatcher.UIThread.Post(() => Apply(e.NewTheme));

    public void Apply(EffectiveTheme theme)
    {
        var app = Application.Current;
        if (app is null)
            return;

        var dict = Build();
        if (_applied is not null)
            app.Resources.MergedDictionaries.Remove(_applied);
        app.Resources.MergedDictionaries.Add(dict);
        _applied = dict;

        app.RequestedThemeVariant = theme == EffectiveTheme.Light ? ThemeVariant.Light : ThemeVariant.Dark;
        PaletteBrushes.Invalidate();
    }

    /// <summary>build from the theme service's <em>resolved</em> palette (built-in or the
    /// active custom overlay) rather than reading <see cref="HistoriaPalette"/> directly.</summary>
    private ResourceDictionary Build()
    {
        var palette = _theme.Palette;
        var dict = new ResourceDictionary();
        foreach (var token in HistoriaPalette.Tokens)
        {
            var rgb = palette.TryGetValue(token, out var v) ? v : HistoriaPalette.Get(_theme.Effective, token);
            var color = rgb.ToAvalonia();
            var key = HistoriaPalette.ResourceKey(token);
            dict[key] = new ImmutableSolidColorBrush(color);
            dict[key + "Color"] = color;
        }

        var surfaceBrush = (IBrush)dict[HistoriaPalette.ResourceKey("surface")]!;
        var surface2Brush = (IBrush)dict[HistoriaPalette.ResourceKey("surface-2")]!;
        var borderBrush = (IBrush)dict[HistoriaPalette.ResourceKey("border")]!;
        var textBrush = (IBrush)dict[HistoriaPalette.ResourceKey("text")]!;
        var textMutedBrush = (IBrush)dict[HistoriaPalette.ResourceKey("text-muted")]!;
        var hoverBrush = (IBrush)dict[HistoriaPalette.ResourceKey("list-hover")]!;

        // Fluent Menu, ContextMenu & Flyout tokens
        dict["MenuFlyoutPresenterBackground"] = surfaceBrush;
        dict["MenuFlyoutPresenterBorderBrush"] = borderBrush;
        dict["MenuFlyoutItemBackground"] = Brushes.Transparent;
        dict["MenuFlyoutItemForeground"] = textBrush;
        dict["MenuFlyoutItemBackgroundPointerOver"] = hoverBrush;
        dict["MenuFlyoutItemForegroundPointerOver"] = textBrush;
        dict["MenuFlyoutItemBackgroundPressed"] = surface2Brush;
        dict["MenuFlyoutItemForegroundPressed"] = textBrush;
        dict["MenuFlyoutItemBackgroundDisabled"] = Brushes.Transparent;
        dict["MenuFlyoutItemForegroundDisabled"] = textMutedBrush;
        dict["MenuFlyoutSubItemChevron"] = textMutedBrush;
        dict["MenuFlyoutSubItemChevronPointerOver"] = textBrush;
        dict["MenuFlyoutItemKeyboardAcceleratorTextForeground"] = textMutedBrush;
        dict["MenuFlyoutItemKeyboardAcceleratorTextForegroundPointerOver"] = textBrush;

        dict["FlyoutPresenterBackground"] = surfaceBrush;

        // Fluent ToolTip tokens
        dict["ToolTipBackground"] = surfaceBrush;
        dict["ToolTipForeground"] = textBrush;
        dict["ToolTipBorderBrush"] = borderBrush;

        // Fluent ScrollBar tokens
        var scrollbarBrush = (IBrush)dict[HistoriaPalette.ResourceKey("scrollbar")]!;
        dict["ScrollBarPanningThumbBackground"] = scrollbarBrush;
        dict["ScrollBarThumbBackgroundColor"] = scrollbarBrush;
        dict["ScrollBarThumbFillPointerOver"] = textMutedBrush;
        dict["ScrollBarThumbFillPressed"] = textBrush;
        dict["ScrollBarThumbFillDisabled"] = borderBrush;

        dict["ScrollBarBackground"] = Brushes.Transparent;
        dict["ScrollBarBackgroundPointerOver"] = Brushes.Transparent;
        dict["ScrollBarBorderBrush"] = Brushes.Transparent;
        dict["ScrollBarTrackFill"] = Brushes.Transparent;
        dict["ScrollBarTrackStroke"] = Brushes.Transparent;
        dict["ScrollBarTrackFillPointerOver"] = Brushes.Transparent;
        dict["ScrollBarTrackStrokePointerOver"] = Brushes.Transparent;

        dict["ScrollBarButtonBackground"] = Brushes.Transparent;
        dict["ScrollBarButtonBorderBrush"] = Brushes.Transparent;
        dict["ScrollBarButtonArrowForeground"] = textMutedBrush;
        dict["ScrollBarButtonArrowForegroundPointerOver"] = textBrush;
        dict["ScrollBarButtonBackgroundPointerOver"] = hoverBrush;
        dict["ScrollBarButtonBorderBrushPointerOver"] = Brushes.Transparent;
        dict["ScrollBarButtonArrowForegroundPressed"] = textBrush;
        dict["ScrollBarButtonBackgroundPressed"] = surface2Brush;
        dict["ScrollBarButtonBorderBrushPressed"] = Brushes.Transparent;
        dict["ScrollBarButtonArrowForegroundDisabled"] = borderBrush;
        dict["ScrollBarButtonBackgroundDisabled"] = Brushes.Transparent;
        dict["ScrollBarButtonBorderBrushDisabled"] = Brushes.Transparent;

        return dict;
    }

    public void Dispose() => _theme.ThemeChanged -= OnThemeChanged;
}
