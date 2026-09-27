namespace Historia.Theming;

/// <summary>
/// the App-side half of theming. <see cref="ThemeService"/> (in Core) decides the
/// effective theme; this rebuilds the Avalonia resource dictionary from <see cref="HistoriaPalette"/>
/// on every change and merges it into <see cref="Application"/>.<c>Resources</c> — the exact
/// shape the old WPF <c>ThemeService.BuildDictionary</c> produced, so <c>{DynamicResource
/// ColorBorder}</c> and friends keep resolving. It also drives <see cref="Application"/>.<c>
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
        return dict;
    }

    public void Dispose() => _theme.ThemeChanged -= OnThemeChanged;
}
