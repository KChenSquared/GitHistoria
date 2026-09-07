namespace Historia.Components;

/// <summary>
/// Path data for the utility icon set (§17 "Icons"). Most glyphs are
/// <see href="https://lucide.dev/">Lucide</see> geometry, vendored as source under
/// <c>assets/lucide/</c> and flattened into <c>IconPaths.Generated.cs</c> by
/// <c>dev/vendor-lucide.ps1</c> — nothing is fetched at build or run time (Rule 0).
/// Lucide glyphs are stroke-based on a 24×24 grid; a few Historia-specific marks (the
/// graph-badge floppy / cloud / tag) stay hand-authored 16×16 fill paths. Every glyph
/// renders in <c>currentColor</c> (Rule 29). See <c>docs/third-party-licenses.md</c>
/// for the Lucide ISC licence.
/// </summary>
public static partial class IconPaths
{
    /// <summary>One glyph: its path data and whether it is drawn stroked (Lucide) or filled (Historia).</summary>
    public readonly record struct Glyph(string Data, bool Stroke);

    // Historia-specific hand-authored 16×16 fill silhouettes. The graph-badge trio is deliberately
    // kept (the floppy "save" is called out in the todo history; the cloud/tag marks are tuned
    // for the 12 px badge renderer, §V6-G1 / §V3-B).
    private static readonly Dictionary<string, string> CustomFill = new(StringComparer.Ordinal)
    {
        // §V6: floppy-disk silhouette — the "exists locally" marker on a graph badge.
        ["save"] = "M2 2h9l3 3v9H2zM5 2.5h4v3.2H5zM4.5 8.5h7v5h-7z",
        // §V3-B: cloud silhouette — the "origin"/remote marker on a graph badge.
        ["remote"] = "M4.6 13.5A3.4 3.4 0 0 1 4.3 6.75 4.7 4.7 0 0 1 13.2 7.7 3 3 0 0 1 12.7 13.5Z",
        // graph-badge tag mark (kept 16×16 fill so the badge cluster stays pixel-tuned).
        ["tag"] = "M2 2h6l6 6-6 6-6-6zM5 4.5A1.5 1.5 0 1 0 5 7.5 1.5 1.5 0 0 0 5 4.5z",
    };

    // Historia icon name → Lucide glyph key (dev/lucide-src). Keeps existing call sites stable.
    private static readonly (string Historia, string Lucide)[] Aliases =
    {
        ("folder", "folder"),
        ("terminal", "terminal"),
        ("settings", "settings"),
        ("trash", "trash-2"),
        ("pencil", "pencil"),
        ("package", "package"),
        ("lock", "lock"),
        ("unlock", "lock-open"),
        ("close", "x"),
        ("chevron-right", "chevron-right"),
        ("chevron-down", "chevron-down"),
        ("stash", "archive"),
    };

    // Lazy so we never depend on static-initializer ordering between this file and the
    // generated partial that populates LucideGlyphs.
    private static FrozenDictionary<string, Glyph>? _glyphs;
    private static FrozenDictionary<string, Glyph> Glyphs => _glyphs ??= BuildGlyphs();

    private static FrozenDictionary<string, Glyph> BuildGlyphs()
    {
        var map = new Dictionary<string, Glyph>(StringComparer.Ordinal);

        foreach (var (name, d) in CustomFill)
            map[name] = new Glyph(d, Stroke: false);

        var aliasTargets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (Historia, lucide) in Aliases)
        {
            map[Historia] = new Glyph(LucideGlyphs[lucide], Stroke: true);
            aliasTargets.Add(lucide);
        }

        // Every remaining vendored Lucide glyph is exposed under its own name — but a glyph
        // that already has an Historia alias is not also surfaced under its Lucide name (that
        // second name would just read as a dead entry to IconPathTests).
        foreach (var (name, d) in LucideGlyphs)
            if (!map.ContainsKey(name) && !aliasTargets.Contains(name))
                map[name] = new Glyph(d, Stroke: true);

        return map.ToFrozenDictionary(StringComparer.Ordinal);
    }

    /// <summary>All defined icon names (used by <c>IconPathTests</c>).</summary>
    public static IReadOnlyCollection<string> Names => Glyphs.Keys;

    public static bool Has(string name) => Glyphs.ContainsKey(name);

    /// <summary>Path data for <paramref name="name"/>.</summary>
    public static string Get(string name) => Resolve(name).Data;

    /// <summary>True when the glyph is stroke-based (Lucide 24×24) rather than a fill silhouette.</summary>
    public static bool IsStroke(string name) => Resolve(name).Stroke;

    /// <summary>The full glyph record (path data + stroke flag).</summary>
    public static Glyph Resolve(string name) =>
        Glyphs.TryGetValue(name, out var g)
            ? g
            : throw new ArgumentException($"No icon path for '{name}'.", nameof(name));
}
