namespace Historia.Models;

/// <summary>
/// §V6-G9: a user-authored theme. Persisted as one JSON file per theme under
/// <c>&lt;DataDirectory&gt;/themes/&lt;id&gt;.json</c> (camelCase keys). A custom theme overrides
/// any subset of <c>HistoriaPalette.Tokens</c> and the nine lane colours over a Dark or Light base;
/// anything it does not specify falls through to that base.
/// </summary>
public sealed record CustomTheme
{
    /// <summary>Stable id, equal to the file stem. Assigned on create, never shown to the user.</summary>
    public required string Id { get; init; }

    /// <summary>User-facing display name. Editable; may collide with another theme's name.</summary>
    public required string Name { get; init; }

    /// <summary>Which built-in supplies every unspecified value: <c>"Dark"</c> or <c>"Light"</c>.</summary>
    public string Base { get; init; } = "Dark";

    /// <summary>Token → <c>#RRGGBB</c>(<c>AA</c>) overrides. Unknown token keys are ignored on load.</summary>
    public IReadOnlyDictionary<string, string> Tokens { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Up to nine lane-colour overrides; a null (or missing) entry inherits the base lane.</summary>
    public IReadOnlyList<string?> Lane { get; init; } = [];

    /// <summary>
    /// The launch splash for this theme: an <c>avares://</c> URI (the built-in copied in when
    /// the theme is created "from Light" / "from Dark") or a path to the user's own PNG. Null /
    /// empty falls back to the built-in splash for <see cref="BaseTheme"/>.
    /// </summary>
    public string? SplashImagePath { get; init; }

    /// <summary>How long the splash stays up, whole seconds, clamped to 0–10. <c>0</c> disables
    /// the splash for this theme entirely. Defaults to the built-in 2 s.</summary>
    public int SplashSeconds { get; init; } = 2;

    /// <summary>The base built-in as an <see cref="EffectiveTheme"/> (HC is never a custom base).</summary>
    public EffectiveTheme BaseTheme =>
        string.Equals(Base, "Light", System.StringComparison.OrdinalIgnoreCase)
            ? EffectiveTheme.Light
            : EffectiveTheme.Dark;

    /// <summary>The <c>avares://</c> location of the built-in splash for a <c>"Light"</c> /
    /// <c>"Dark"</c> base — the value seeded into <see cref="SplashImagePath"/> on create.</summary>
    public static string BuiltInSplashLocation(string baseName) =>
        string.Equals(baseName, "Light", System.StringComparison.OrdinalIgnoreCase)
            ? "avares://Historia/Assets/splash-light.png"
            : "avares://Historia/Assets/splash-dark.png";
}

/// <summary>§V6-G9: a fully resolved colour set — the built-in base with a custom theme's
/// overrides applied. <see cref="Lanes"/> is always <c>LanePalette.Size</c> long.</summary>
public sealed record ResolvedPalette(IReadOnlyDictionary<string, Rgb> Tokens, IReadOnlyList<Rgb> Lanes);
