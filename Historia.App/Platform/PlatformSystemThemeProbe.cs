namespace Historia.Platform;

/// <summary>
/// App-side <see cref="ISystemThemeProbe"/> over Avalonia's cross-platform
/// <see cref="IPlatformSettings"/>. Works on Windows, macOS, and Linux with no platform code here.
/// </summary>
public sealed class PlatformSystemThemeProbe : ISystemThemeProbe, IDisposable
{
    private readonly IPlatformSettings? _settings;

    public event EventHandler? Changed;

    public PlatformSystemThemeProbe()
    {
        _settings = Application.Current?.PlatformSettings;
        if (_settings is not null)
            _settings.ColorValuesChanged += OnColorValuesChanged;
    }

    public bool IsLightTheme =>
        (_settings?.GetColorValues().ThemeVariant ?? PlatformThemeVariant.Dark) == PlatformThemeVariant.Light;

    public bool IsHighContrast =>
        _settings?.GetColorValues().ContrastPreference == ColorContrastPreference.High;

    private void OnColorValuesChanged(object? sender, PlatformColorValues e)
        => Changed?.Invoke(this, EventArgs.Empty);

    public void Dispose()
    {
        if (_settings is not null)
            _settings.ColorValuesChanged -= OnColorValuesChanged;
    }
}
