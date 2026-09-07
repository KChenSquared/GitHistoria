namespace Historia.Services.Theming;

/// <summary>
/// The OS-level appearance signal — light/dark and high-contrast — abstracted so
/// <see cref="Historia.Core"/> needs no platform code (V4 §split). The App layer implements it
/// over <c>Application.Current.PlatformSettings.ColorValuesChanged</c>, which is cross-platform;
/// tests supply a fixed implementation.
/// </summary>
public interface ISystemThemeProbe
{
    /// <summary>True when the OS reports a light app theme; false for dark.</summary>
    bool IsLightTheme { get; }

    /// <summary>True when an OS high-contrast scheme is active.</summary>
    bool IsHighContrast { get; }

    /// <summary>Raised when <see cref="IsLightTheme"/> or <see cref="IsHighContrast"/> may have changed.</summary>
    event EventHandler? Changed;
}
