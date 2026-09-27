
namespace Historia.Services.Theming;

public sealed record ThemeChangedEventArgs(EffectiveTheme OldTheme, EffectiveTheme NewTheme);

/// <summary>
/// Resolves the effective theme from settings and the OS and exposes the resolved palette;
/// the App's <c>AvaloniaThemeApplier</c> turns it into resources. Registered as a singleton.
/// </summary>
public interface IThemeService
{
    /// <summary>The user's chosen mode, as persisted in settings.</summary>
    ThemeMode CurrentMode { get; }

    /// <summary>Whether the High Contrast override is on (settings or OS).</summary>
    bool HighContrastEnabled { get; }

    /// <summary>
    /// What is actually rendered: <see cref="ThemeMode.System"/> resolved against the OS
    /// theme, with High Contrast taking precedence over Dark and Light.
    /// </summary>
    EffectiveTheme Effective { get; }

    /// <summary>Lower-case name of the effective theme: "dark", "light", or "high-contrast".</summary>
    string EffectiveCssThemeName { get; }

    /// <summary>
    /// The resolved token → colour map every consumer should paint from. Identical to
    /// <c>HistoriaPalette.For(Effective)</c> for the built-in modes; the custom overlay when
    /// <see cref="CurrentMode"/> is <see cref="ThemeMode.Custom"/> and a theme is selected (HC
    /// still wins). Recomputed on <see cref="ThemeChanged"/>.
    /// </summary>
    IReadOnlyDictionary<string, Rgb> Palette { get; }

    /// <summary>The resolved lane-colour list (length <c>LanePalette.Size</c>) — the
    /// built-in rainbow for a theme, or the custom overlay under <see cref="ThemeMode.Custom"/>.</summary>
    IReadOnlyList<Rgb> LaneColors { get; }

    /// <summary>Persists the mode and recomputes the effective theme, raising <see cref="ThemeChanged"/> when it changes.</summary>
    Task ApplyModeAsync(ThemeMode mode, CancellationToken ct = default);

    /// <summary>Selects a custom theme by id — sets the mode to
    /// <see cref="ThemeMode.Custom"/>, stores the id, and refreshes even when the effective
    /// base theme is unchanged.</summary>
    Task ApplyCustomThemeAsync(string themeId, CancellationToken ct = default);

    /// <summary>Toggles the High Contrast override independently of the base mode.</summary>
    Task SetHighContrastAsync(bool enabled, CancellationToken ct = default);

    /// <summary>Raised when the effective theme changes, including OS-triggered changes.</summary>
    event EventHandler<ThemeChangedEventArgs>? ThemeChanged;
}
