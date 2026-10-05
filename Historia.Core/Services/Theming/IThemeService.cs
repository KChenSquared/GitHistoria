
namespace Historia.Services.Theming;

public sealed record ThemeChangedEventArgs(EffectiveTheme OldTheme, EffectiveTheme NewTheme);

/// <summary>
/// Resolves the effective theme from settings and the OS and exposes the resolved palette;
/// the App's <c>AvaloniaThemeApplier</c> turns it into resources. Registered as a singleton.
/// </summary>
public interface IThemeService
{
    /// <summary>The user's Light / Dark / System choice, as persisted in settings — unchanged
    /// while a custom theme overrides it.</summary>
    ThemeMode CurrentMode { get; }

    /// <summary>Whether a custom theme is rendering in place of <see cref="CurrentMode"/>: the
    /// override is on, the selected theme resolves to a saved theme, and High Contrast is
    /// off.</summary>
    bool CustomThemeActive { get; }

    /// <summary>Whether the High Contrast override is on (settings or OS).</summary>
    bool HighContrastEnabled { get; }

    /// <summary>
    /// What is actually rendered: <see cref="ThemeMode.System"/> resolved against the OS
    /// theme, or an active custom theme's Dark / Light base, with High Contrast taking
    /// precedence over both.
    /// </summary>
    EffectiveTheme Effective { get; }

    /// <summary>Lower-case name of the effective theme: "dark", "light", or "high-contrast".</summary>
    string EffectiveCssThemeName { get; }

    /// <summary>
    /// The resolved token → colour map every consumer should paint from. Identical to
    /// <c>HistoriaPalette.For(Effective)</c> for the built-in modes; the custom overlay while
    /// <see cref="CustomThemeActive"/>. Recomputed on <see cref="ThemeChanged"/>.
    /// </summary>
    IReadOnlyDictionary<string, Rgb> Palette { get; }

    /// <summary>The resolved lane-colour list (length <c>LanePalette.Size</c>) — the
    /// built-in rainbow for a theme, or the custom overlay while
    /// <see cref="CustomThemeActive"/>.</summary>
    IReadOnlyList<Rgb> LaneColors { get; }

    /// <summary>Persists the Light / Dark / System choice and recomputes the effective theme,
    /// raising <see cref="ThemeChanged"/> when it changes.</summary>
    Task ApplyModeAsync(ThemeMode mode, CancellationToken ct = default);

    /// <summary>Turns the custom-theme override on or off. Turning it on when the selected id
    /// no longer resolves selects the first saved theme. Refreshes even when the effective base
    /// theme is unchanged.</summary>
    Task SetCustomThemeEnabledAsync(bool enabled, CancellationToken ct = default);

    /// <summary>Stores the selected custom theme's id, whether or not the override is on;
    /// refreshes when it is.</summary>
    Task SelectCustomThemeAsync(string themeId, CancellationToken ct = default);

    /// <summary>Toggles the High Contrast override independently of the base mode.</summary>
    Task SetHighContrastAsync(bool enabled, CancellationToken ct = default);

    /// <summary>Raised when the effective theme changes, including OS-triggered changes.</summary>
    event EventHandler<ThemeChangedEventArgs>? ThemeChanged;
}
