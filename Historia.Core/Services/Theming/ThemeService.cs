namespace Historia.Services.Theming;

/// <inheritdoc cref="IThemeService"/>
/// <remarks>
/// this type is now UI-framework-free. It resolves the effective theme and raises
/// <see cref="ThemeChanged"/>; applying it to a concrete resource dictionary is the App layer's
/// job (<c>AvaloniaThemeApplier</c> subscribes to <see cref="ThemeChanged"/>). Subscribers are
/// responsible for marshalling to their UI thread.
/// <para>
/// it also exposes the <em>resolved</em> <see cref="Palette"/> and
/// <see cref="LaneColors"/> — the built-in tables, or a custom theme's overlay when
/// <see cref="ThemeMode.Custom"/> is active — so consumers no longer read <c>HistoriaPalette</c> /
/// <c>LanePalette</c> directly.
/// </para>
/// </remarks>
public sealed class ThemeService : IThemeService, IDisposable
{
    private readonly ISettingsService _settings;
    private readonly ISystemThemeProbe _probe;
    private readonly ICustomThemeService _customThemes;
    private readonly object _sync = new();

    private EffectiveTheme _effective;

    public event EventHandler<ThemeChangedEventArgs>? ThemeChanged;

    public ThemeService(ISettingsService settings, ISystemThemeProbe probe, ICustomThemeService customThemes)
    {
        _settings = settings;
        _probe = probe;
        _customThemes = customThemes;
        _effective = Resolve(settings.Current.AppTheme, settings.Current.HighContrastOverride);
        _probe.Changed += OnOsThemeChanged;
        _customThemes.Changed += OnCustomThemesChanged;
    }

    public ThemeMode CurrentMode => _settings.Current.AppTheme;

    public bool HighContrastEnabled =>
        _settings.Current.HighContrastOverride || _probe.IsHighContrast;

    public EffectiveTheme Effective
    {
        get { lock (_sync) return _effective; }
    }

    public string EffectiveCssThemeName => Effective switch
    {
        EffectiveTheme.Light => "light",
        EffectiveTheme.HighContrast => "high-contrast",
        _ => "dark",
    };

    public IReadOnlyDictionary<string, Rgb> Palette =>
        ActiveCustom() is { } t ? _customThemes.Compose(t).Tokens : HistoriaPalette.For(Effective);

    public IReadOnlyList<Rgb> LaneColors
    {
        get
        {
            if (ActiveCustom() is { } t)
                return _customThemes.Compose(t).Lanes;

            var arr = new Rgb[LanePalette.Size];
            for (var i = 0; i < arr.Length; i++)
                arr[i] = LanePalette.ColorFor(i, Effective);
            return arr;
        }
    }

    /// <summary>The custom theme to overlay, or null — null whenever HC is on (HC always wins)
    /// or the mode is not Custom or the stored id no longer resolves.</summary>
    private CustomTheme? ActiveCustom()
    {
        if (CurrentMode != ThemeMode.Custom || HighContrastEnabled)
            return null;
        var id = _settings.Current.CustomThemeId;
        return string.IsNullOrEmpty(id) ? null : _customThemes.Get(id);
    }

    public async Task ApplyModeAsync(ThemeMode mode, CancellationToken ct = default)
    {
        await _settings.UpdateAsync(s => s with { AppTheme = mode }, ct).ConfigureAwait(false);
        if (!Recompute() && mode == ThemeMode.Custom)
            ForceRefresh();
    }

    public async Task ApplyCustomThemeAsync(string themeId, CancellationToken ct = default)
    {
        await _settings.UpdateAsync(s => s with { AppTheme = ThemeMode.Custom, CustomThemeId = themeId }, ct)
            .ConfigureAwait(false);
        if (!Recompute())
            ForceRefresh();
    }

    public async Task SetHighContrastAsync(bool enabled, CancellationToken ct = default)
    {
        await _settings.UpdateAsync(s => s with { HighContrastOverride = enabled }, ct).ConfigureAwait(false);
        Recompute();
    }

    private void OnOsThemeChanged(object? sender, EventArgs e) => Recompute();

    private void OnCustomThemesChanged(object? sender, EventArgs e)
    {
        // A custom theme was saved or deleted. If one is active, its colours may have changed
        // even though the effective base theme has not — force consumers to re-read.
        if (!Recompute() && CurrentMode == ThemeMode.Custom)
            ForceRefresh();
    }

    /// <summary>Recompute the effective theme; raise <see cref="ThemeChanged"/> and return true
    /// only if it actually changed.</summary>
    private bool Recompute()
    {
        var next = Resolve(_settings.Current.AppTheme, _settings.Current.HighContrastOverride);
        EffectiveTheme previous;
        lock (_sync)
        {
            if (next == _effective)
                return false;
            previous = _effective;
            _effective = next;
        }

        ThemeChanged?.Invoke(this, new ThemeChangedEventArgs(previous, next));
        return true;
    }

    /// <summary>Re-raise <see cref="ThemeChanged"/> with an unchanged effective theme so
    /// consumers re-read <see cref="Palette"/> / <see cref="LaneColors"/>.</summary>
    private void ForceRefresh()
    {
        var e = Effective;
        ThemeChanged?.Invoke(this, new ThemeChangedEventArgs(e, e));
    }

    private EffectiveTheme Resolve(ThemeMode mode, bool highContrastOverride)
    {
        // Custom resolves through its chosen base (Dark/Light) so the Fluent variant and
        // every EffectiveTheme switch still work. HC override still wins over everything.
        if (mode == ThemeMode.Custom && !highContrastOverride && !_probe.IsHighContrast)
        {
            var id = _settings.Current.CustomThemeId;
            var baseTheme = (string.IsNullOrEmpty(id) ? null : _customThemes.Get(id))?.BaseTheme
                            ?? EffectiveTheme.Dark;
            mode = baseTheme == EffectiveTheme.Light ? ThemeMode.Light : ThemeMode.Dark;
        }

        return ThemeResolver.Resolve(
            mode,
            highContrastOverride,
            _probe.IsHighContrast,
            _probe.IsLightTheme);
    }

    public void Dispose()
    {
        _probe.Changed -= OnOsThemeChanged;
        _customThemes.Changed -= OnCustomThemesChanged;
    }
}
