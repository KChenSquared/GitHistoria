namespace Historia.Components;

/// <summary>
/// Path data for the utility icon set ("Icons"). Glyphs are Google
/// Material Symbols (Outlined style, 24px optical size, weight 400), vendored as SVG
/// under <c>assets/material-symbols/</c> and flattened into <c>IconPaths.Generated.cs</c> by
/// <c>dev/vendor-material-symbols.ps1</c> — nothing is fetched at build or run time (Rule 0).
/// Glyphs sit on Material's 960-unit grid (<c>viewBox 0 -960 960 960</c>, y in [-960, 0]) and
/// render in <c>currentColor</c> (Rule 29). See <c>docs/third-party-licenses.md</c>
/// for the Apache-2.0 licence.
/// </summary>
public static partial class IconPaths
{
    /// <summary>
    /// Material Symbols viewBox grid size (960×960 units, coordinates x ∈ [0, 960], y ∈ [-960, 0]).
    /// To render at <c>size</c> pixels, translate by (0, 960) then scale by <c>size / 960</c>.
    /// </summary>
    public const double GridSize = 960;

    /// <summary>All defined icon names (used by <c>IconPathTests</c>).</summary>
    public static IReadOnlyCollection<string> Names => (IReadOnlyCollection<string>)MaterialSymbols.Keys;

    public static bool Has(string name) => MaterialSymbols.ContainsKey(name);

    /// <summary>Path data for <paramref name="name"/>.</summary>
    public static string Get(string name) =>
        MaterialSymbols.TryGetValue(name, out var d)
            ? d
            : throw new ArgumentException($"No icon path for '{name}'.", nameof(name));
}
