namespace Historia.Services.Theming;

/// <inheritdoc cref="IThemeService"/>
/// <remarks>
/// This type is UI-framework-free. It resolves the effective theme and raises
/// <see cref="ThemeChanged"/>; applying it to a concrete resource dictionary is the App layer's
/// job (<c>AvaloniaThemeApplier</c> subscribes to <see cref="ThemeChanged"/>). Subscribers are
/// responsible for marshalling to their UI thread.
/// <para>
/// It also exposes the <em>resolved</em> <see cref="Palette"/> and <see cref="LaneColors"/> —
/// the built-in tables, or a custom theme's overlay while <see cref="CustomThemeActive"/> — so
/// consumers never read <c>HistoriaPalette</c> / <c>LanePalette</c> directly.
/// </para>
/// <para>
/// A custom theme overrides the Light / Dark / System choice; it never replaces it. It renders
/// only while <see cref="AppSettings.UseCustomTheme"/> is on, <see cref="AppSettings.CustomThemeId"/>
/// resolves to a saved theme and High Contrast is off, and it resolves through its own Dark or
/// Light base. Otherwise <see cref="AppSettings.AppTheme"/> renders.
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
        _effective = Resolve();
        _probe.Changed += OnOsThemeChanged;
        _customThemes.Changed += OnCustomThemesChanged;
    }

    public ThemeMode CurrentMode => _settings.Current.AppTheme;

    public bool CustomThemeActive => ActiveCustom() is not null;

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

    /// <summary>The custom theme to overlay, or null — null whenever the override is off, HC is
    /// on (HC always wins) or the stored id no longer resolves.</summary>
    private CustomTheme? ActiveCustom()
    {
        var s = _settings.Current;
        if (!s.UseCustomTheme || HighContrastEnabled)
            return null;
        var id = s.CustomThemeId;
        return string.IsNullOrEmpty(id) ? null : _customThemes.Get(id);
    }

    public async Task ApplyModeAsync(ThemeMode mode, CancellationToken ct = default)
    {
        await _settings.UpdateAsync(s => s with { AppTheme = mode }, ct).ConfigureAwait(false);
        Recompute();
    }

    public async Task SetCustomThemeEnabledAsync(bool enabled, CancellationToken ct = default)
    {
        // Read outside the settings update: the mutation runs under the settings lock, and
        // listing themes can touch the disk.
        IReadOnlyList<CustomTheme> saved = enabled ? _customThemes.List() : [];
        await _settings.UpdateAsync(s =>
        {
            if (!enabled)
                return s with { UseCustomTheme = false };
            var id = s.CustomThemeId;
            if (saved.Count > 0 && !saved.Any(t => string.Equals(t.Id, id, StringComparison.Ordinal)))
                id = saved[0].Id;
            return s with { UseCustomTheme = true, CustomThemeId = id };
        }, ct).ConfigureAwait(false);
        if (!Recompute())
            ForceRefresh();
    }

    public async Task SelectCustomThemeAsync(string themeId, CancellationToken ct = default)
    {
        await _settings.UpdateAsync(s => s with { CustomThemeId = themeId }, ct).ConfigureAwait(false);
        if (_settings.Current.UseCustomTheme && !Recompute())
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
        // A custom theme was saved or deleted. While the override is on, its colours may have
        // changed (or it may have started or stopped resolving) even though the effective base
        // theme has not — force consumers to re-read.
        if (!Recompute() && _settings.Current.UseCustomTheme)
            ForceRefresh();
    }

    /// <summary>Recompute the effective theme; raise <see cref="ThemeChanged"/> and return true
    /// only if it actually changed.</summary>
    private bool Recompute()
    {
        var next = Resolve();
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

    private EffectiveTheme Resolve()
    {
        // An active custom theme resolves through its chosen base (Dark/Light) so the Fluent
        // variant and every EffectiveTheme switch still work. HC still wins over everything,
        // and ActiveCustom() is null while it is on.
        var s = _settings.Current;
        var mode = ActiveCustom() is { } custom
            ? (custom.BaseTheme == EffectiveTheme.Light ? ThemeMode.Light : ThemeMode.Dark)
            : s.AppTheme;

        return ThemeResolver.Resolve(
            mode,
            s.HighContrastOverride,
            _probe.IsHighContrast,
            _probe.IsLightTheme);
    }

    public void Dispose()
    {
        _probe.Changed -= OnOsThemeChanged;
        _customThemes.Changed -= OnCustomThemesChanged;
    }
}
