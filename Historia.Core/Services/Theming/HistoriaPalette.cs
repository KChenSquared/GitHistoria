
namespace Historia.Services.Theming;

/// <summary>
/// The single source of truth for every colour the app paints — the window
/// chrome, the generated resource dictionary, and the graph renderer. Every theme
/// defines exactly the same token set; <c>PaletteParityTests</c> asserts it.
/// <para>
/// Both themes start from Visual Studio Code Dark Modern / Light Modern. Dark replaces the
/// neutral greys with a navy family (Table S) so the chrome, graph and editor share one cool
/// field under the neon lane colours. Light is Light Modern on one pure-white field: <c>bg</c>,
/// <c>chrome</c>, <c>surface</c>, <c>surface-2</c> and <c>term-bg</c> are all white, with no
/// near-white panels, and every text token is pure black. Every other entry carries its VS Code
/// key (Table A, verified verbatim from the theme JSON) or its derivation (Table B, recomputed
/// from the others). The diff line washes, changed-text washes and gutter tints take GitHub
/// Desktop's Primer values (Table G: <c>primer-support</c> 4.0.0 colour-system variables, with the
/// Sass expression that yields each), so added and deleted rows read as the greens and reds
/// GitHub Desktop paints on its own editor field.
/// </para>
/// </summary>
public static class HistoriaPalette
{
    /// <summary>The ordered token set. Every theme dictionary defines exactly these keys.</summary>
    public static readonly IReadOnlyList<string> Tokens =
    [
        // structure
        "bg", "chrome", "surface", "surface-2", "border", "border-strong",
        // brand / links
        "primary", "primary-h", "accent",
        // text
        "text", "text-muted", "text-faint", "caption-text",
        // status
        "danger", "warning", "success",
        // lists
        "list-hover", "list-active", "list-inactive", "scrollbar",
        // git decoration
        "git-added", "git-deleted", "git-modified", "git-untracked", "git-conflict", "git-ignored",
        // diff line backgrounds
        "diff-add-bg", "diff-del-bg",
        // diff intra-line highlights & padding
        "diff-add-hl", "diff-del-hl", "diff-empty-bg",
        // diff line-number gutter on added / deleted rows
        "diff-add-gutter", "diff-del-gutter",
        // conflict resolver washes
        "conflict-ours-bg", "conflict-theirs-bg", "conflict-base-bg", "conflict-marker-bg", "conflict-resolved-bg",
        // conflict intra-line highlights
        "conflict-ours-hl", "conflict-theirs-hl",
        // code editor selection
        "code-selection",
        // graph ref badge label and icons — white in every theme, on every lane colour.
        "ref-label",
        // terminal
        "term-bg", "term-fg", "term-cursor", "term-selection",
        "ansi-black", "ansi-red", "ansi-green", "ansi-yellow",
        "ansi-blue", "ansi-magenta", "ansi-cyan", "ansi-white",
        "ansi-br-black", "ansi-br-red", "ansi-br-green", "ansi-br-yellow",
        "ansi-br-blue", "ansi-br-magenta", "ansi-br-cyan", "ansi-br-white",
    ];

    public static IReadOnlyDictionary<string, Rgb> For(EffectiveTheme theme) => theme switch
    {
        EffectiveTheme.Light => Light,
        EffectiveTheme.HighContrast => HighContrast,
        _ => Dark,
    };

    public static Rgb Get(EffectiveTheme theme, string token) => For(theme)[token];

    // ── Dark ────────────────────────────────────────────────────────────────────────────
    private static readonly IReadOnlyDictionary<string, Rgb> Dark = Build(
        ("bg",            "#191925"),   // S: navy field under the neon lane colours
        ("chrome",        "#13131D"),   // S: bg one step darker (sidebars, title bar, panels)
        ("surface",       "#1E1E2D"),   // S: bg one step lighter (popups, widgets)
        ("surface-2",     "#2A2A3D"),   // S: inputs, dropdowns
        ("border",        "#272739"),   // S: panel / sidebar / title bar dividers
        ("border-strong", "#3B3B55"),   // S: input / dropdown outlines
        ("primary",       "#0078D4"),   // A: focusBorder, button.background
        ("primary-h",     "#026EC1"),   // A: button.hoverBackground
        ("accent",        "#4DAAFC"),   // A: textLink.foreground
        ("text",          "#CFCFDC"),   // S: cool off-white
        ("text-muted",    "#9D9DB5"),   // S: cool grey
        ("text-faint",    "#6D6F8C"),   // S: line numbers, hints
        ("caption-text",  "#CFCFDC"),   // B: = text
        ("danger",        "#F85149"),   // A: errorForeground
        ("warning",       "#BB8009"),   // B: settings.modifiedItemIndicator, alpha dropped
        ("success",       "#2EA043"),   // B: = git-added
        ("list-hover",    "#272739"),   // B: = border (one-step-up surface)
        ("list-active",   "#0078D433"), // B: primary @ 20% alpha
        ("list-inactive", "#272739"),   // B: = list-hover
        ("scrollbar",     "#9D9DB566"), // B: text-muted @ 40% alpha
        ("git-added",     "#2EA043"),   // A: editorGutter.addedBackground
        ("git-deleted",   "#F85149"),   // A: editorGutter.deletedBackground
        ("git-modified",  "#E2C08D"),   // A: chat.editedFileForeground
        ("git-untracked", "#2EA043"),   // B: = git-added (no hue distinction)
        ("git-conflict",  "#F85149"),   // B: = danger
        ("git-ignored",   "#9D9DB5"),   // B: = text-muted
        ("diff-add-bg",   "#113A1B"),   // G: darken($green-900, 3%)
        ("diff-del-bg",   "#450C0F"),   // G: darken($red-900, 15%)
        ("diff-add-hl",   "#22863A"),   // G: $green-600
        ("diff-del-hl",   "#B31D28"),   // G: $red-700
        ("diff-empty-bg", "#80808014"), // B: neutral padding row wash
        ("diff-add-gutter", "#0B2611"), // G: darken($green-900, 8%)
        ("diff-del-gutter", "#2F090A"), // G: darken($red-900, 20%)
        ("conflict-ours-bg",     "#0078D433"), // B: primary @ 20% alpha
        ("conflict-theirs-bg",   "#BB800933"), // B: warning @ 20% alpha
        ("conflict-base-bg",     "#9D9DB526"), // B: text-muted @ 15% alpha
        ("conflict-marker-bg",   "#3B3B554D"), // B: border-strong @ 30% alpha
        ("conflict-resolved-bg", "#2EA04326"), // B: success @ 15% alpha
        ("conflict-ours-hl",     "#0078D466"), // B: primary @ 40% alpha
        ("conflict-theirs-hl",   "#BB800966"), // B: warning @ 40% alpha
        ("code-selection",       "#2C3C78"),   // S: indigo-leaning selection
        ("ref-label",     "#FFFFFF"),  // graph ref badge label
        // terminal. term-* are role aliases (B); the ANSI set is VS Code dark_vs
        // terminal.ansi* verbatim (A).
        ("term-bg",         "#191925"), // B: = bg
        ("term-fg",         "#CFCFDC"), // B: = text
        ("term-cursor",     "#4DAAFC"), // B: = accent
        ("term-selection",  "#0078D433"),// B: = list-active
        ("ansi-black",      "#000000"), // A: terminal.ansiBlack (dark_vs)
        ("ansi-red",        "#CD3131"), // A: terminal.ansiRed
        ("ansi-green",      "#0DBC79"), // A: terminal.ansiGreen
        ("ansi-yellow",     "#E5E510"), // A: terminal.ansiYellow
        ("ansi-blue",       "#2472C8"), // A: terminal.ansiBlue
        ("ansi-magenta",    "#BC3FBC"), // A: terminal.ansiMagenta
        ("ansi-cyan",       "#11A8CD"), // A: terminal.ansiCyan
        ("ansi-white",      "#E5E5E5"), // A: terminal.ansiWhite
        ("ansi-br-black",   "#666666"), // A: terminal.ansiBrightBlack
        ("ansi-br-red",     "#F14C4C"), // A: terminal.ansiBrightRed
        ("ansi-br-green",   "#23D18B"), // A: terminal.ansiBrightGreen
        ("ansi-br-yellow",  "#F5F543"), // A: terminal.ansiBrightYellow
        ("ansi-br-blue",    "#3B8EEA"), // A: terminal.ansiBrightBlue
        ("ansi-br-magenta", "#D670D6"), // A: terminal.ansiBrightMagenta
        ("ansi-br-cyan",    "#29B8DB"), // A: terminal.ansiBrightCyan
        ("ansi-br-white",   "#E5E5E5")); // A: terminal.ansiBrightWhite

    // ── Light ───────────────────────────────────────────────────────────────────────────
    private static readonly IReadOnlyDictionary<string, Rgb> Light = Build(
        ("bg",            "#FFFFFF"),   // A: editor.background
        ("chrome",        "#FFFFFF"),   // B: = bg (one white field, no near-white panels)
        ("surface",       "#FFFFFF"),   // B: = bg
        ("surface-2",     "#FFFFFF"),   // A: input.background, dropdown.background
        ("border",        "#E5E5E5"),   // A: panel.border, sideBar.border, titleBar.border
        ("border-strong", "#CECECE"),   // A: input.border, dropdown.border
        ("primary",       "#005FB8"),   // A: focusBorder, button.background
        ("primary-h",     "#0258A8"),   // A: button.hoverBackground
        ("accent",        "#005FB8"),   // A: textLink.foreground
        ("text",          "#000000"),   // B: pure black body text
        ("text-muted",    "#000000"),   // B: = text (no grey text in Light)
        ("text-faint",    "#000000"),   // B: = text
        ("caption-text",  "#000000"),   // B: = text
        ("danger",        "#F85149"),   // A: errorForeground (Light Modern uses the same red as dark)
        ("warning",       "#BB8009"),   // B: settings.modifiedItemIndicator, alpha dropped
        ("success",       "#2EA043"),   // B: = git-added
        ("list-hover",    "#F2F2F2"),   // A: list.hoverBackground
        ("list-active",   "#E8E8E8"),   // A: list.activeSelectionBackground
        ("list-inactive", "#E4E6F1"),   // B: modernActivityBarItem.activeBackground
        ("scrollbar",     "#61616166"), // B: neutral grey #616161 @ 40% alpha
        ("git-added",     "#2EA043"),   // A: editorGutter.addedBackground
        ("git-deleted",   "#F85149"),   // A: editorGutter.deletedBackground
        ("git-modified",  "#895503"),   // A: chat.editedFileForeground
        ("git-untracked", "#2EA043"),   // B: = git-added
        ("git-conflict",  "#F85149"),   // B: = danger
        ("git-ignored",   "#616161"),   // B: neutral grey — ignored files stay de-emphasised
        ("diff-add-bg",   "#E6FFED"),   // G: darken($green-000, 2%)
        ("diff-del-bg",   "#FFEEF0"),   // G: $red-000
        ("diff-add-hl",   "#ACF2BD"),   // G: literal
        ("diff-del-hl",   "#FDB8C0"),   // G: literal
        ("diff-empty-bg", "#80808014"), // B: neutral padding row wash
        ("diff-add-gutter", "#CDFFD8"), // G: darken($green-100, 3%)
        ("diff-del-gutter", "#FFDCE0"), // G: $red-100
        ("conflict-ours-bg",     "#005FB826"), // B: primary @ 15% alpha
        ("conflict-theirs-bg",   "#BB800926"), // B: warning @ 15% alpha
        ("conflict-base-bg",     "#61616126"), // B: neutral grey #616161 @ 15% alpha
        ("conflict-marker-bg",   "#CECECE4D"), // B: border-strong @ 30% alpha
        ("conflict-resolved-bg", "#2EA04326"), // B: success @ 15% alpha
        ("conflict-ours-hl",     "#005FB84D"), // B: primary @ 30% alpha
        ("conflict-theirs-hl",   "#BB80094D"), // B: warning @ 30% alpha
        ("code-selection",       "#ADD6FF"),   // A: editor.selectionBackground
        ("ref-label",     "#FFFFFF"),  // graph ref badge label
        // terminal. term-* are role aliases (B); the ANSI set is VS Code light_vs
        // terminal.ansi* verbatim (A).
        ("term-bg",         "#FFFFFF"), // B: = bg
        ("term-fg",         "#000000"), // B: = text
        ("term-cursor",     "#005FB8"), // B: = accent
        ("term-selection",  "#E8E8E8"), // B: = list-active
        ("ansi-black",      "#000000"), // A: terminal.ansiBlack (light_vs)
        ("ansi-red",        "#CD3131"), // A: terminal.ansiRed
        ("ansi-green",      "#00BC00"), // A: terminal.ansiGreen
        ("ansi-yellow",     "#949800"), // A: terminal.ansiYellow
        ("ansi-blue",       "#0451A5"), // A: terminal.ansiBlue
        ("ansi-magenta",    "#BC05BC"), // A: terminal.ansiMagenta
        ("ansi-cyan",       "#0598BC"), // A: terminal.ansiCyan
        ("ansi-white",      "#555555"), // A: terminal.ansiWhite
        ("ansi-br-black",   "#666666"), // A: terminal.ansiBrightBlack
        ("ansi-br-red",     "#CD3131"), // A: terminal.ansiBrightRed
        ("ansi-br-green",   "#14CE14"), // A: terminal.ansiBrightGreen
        ("ansi-br-yellow",  "#B5BA00"), // A: terminal.ansiBrightYellow
        ("ansi-br-blue",    "#0451A5"), // A: terminal.ansiBrightBlue
        ("ansi-br-magenta", "#BC05BC"), // A: terminal.ansiBrightMagenta
        ("ansi-br-cyan",    "#0598BC"), // A: terminal.ansiBrightCyan
        ("ansi-br-white",   "#A5A5A5")); // A: terminal.ansiBrightWhite

    // ── High Contrast ────────────────────────────────────────────────────────────────────
    // Its own accessibility contract; no VS Code counterpart. Derived tokens come
    // from the core HC values: chrome = bg (one black ground), border-strong =
    // border, list-active = the selection yellow at full opacity, git-*/diff-* from the
    // danger/success pair. Every foreground/background pairing stays above 7:1.
    private static readonly IReadOnlyDictionary<string, Rgb> HighContrast = Build(
        ("bg",            "#000000"),
        ("chrome",        "#000000"),   // = bg
        ("surface",       "#1a1a1a"),
        ("surface-2",     "#2a2a2a"),
        ("border",        "#ffffff"),
        ("border-strong", "#ffffff"),   // = border
        ("primary",       "#ffff00"),
        ("primary-h",     "#ffff80"),
        ("accent",        "#00ffff"),
        ("text",          "#ffffff"),
        ("text-muted",    "#cccccc"),
        ("text-faint",    "#cccccc"),   // = text-muted (13:1 on black)
        ("caption-text",  "#ffffff"),   // = text
        ("danger",        "#ff4444"),
        ("warning",       "#ffff00"),   // = primary yellow
        ("success",       "#44ff44"),
        ("list-hover",    "#2a2a2a"),   // = surface-2 (subtle); active carries the yellow
        ("list-active",   "#ffff00"),   // selection yellow at full opacity
        ("list-inactive", "#2a2a2a"),   // = surface-2
        ("scrollbar",     "#cccccc"),
        ("git-added",     "#44ff44"),   // = success
        ("git-deleted",   "#ff4444"),   // = danger
        ("git-modified",  "#ffff00"),   // = primary
        ("git-untracked", "#44ff44"),   // = success
        ("git-conflict",  "#ff4444"),   // = danger
        ("git-ignored",   "#cccccc"),   // = text-muted
        ("diff-add-bg",   "#44ff4433"),
        ("diff-del-bg",   "#ff444433"),
        ("diff-add-hl",   "#44ff4466"), // = git-added @ 40% alpha
        ("diff-del-hl",   "#ff444466"), // = git-deleted @ 40% alpha
        ("diff-empty-bg", "#00000000"), // = transparent on black ground
        ("diff-add-gutter", "#44ff4433"), // = diff-add-bg
        ("diff-del-gutter", "#ff444433"), // = diff-del-bg
        ("conflict-ours-bg",     "#00ffff33"), // = accent cyan @ 20% alpha
        ("conflict-theirs-bg",   "#ffff0033"), // = primary yellow @ 20% alpha
        ("conflict-base-bg",     "#ffffff26"), // = text white @ 15% alpha
        ("conflict-marker-bg",   "#ffffff4d"), // = border @ 30% alpha
        ("conflict-resolved-bg", "#44ff4433"), // = success @ 20% alpha
        ("conflict-ours-hl",     "#00ffff66"), // = accent cyan @ 40% alpha
        ("conflict-theirs-hl",   "#ffff0066"), // = primary yellow @ 40% alpha
        ("code-selection",       "#ffff00"),   // = selection yellow at full opacity
        ("ref-label",     "#ffffff"),
        // terminal. HC has no VS Code counterpart. term-bg/fg come from the HC
        // structure tokens; the ANSI set is the dark_vs hues lifted toward #ffffff until each
        // clears 4.5:1 on #000000 (Table B). TerminalContrastTests verifies every entry.
        ("term-bg",         "#000000"), // = bg
        ("term-fg",         "#ffffff"), // = text
        ("term-cursor",     "#00ffff"), // = accent
        ("term-selection",  "#ffff00"), // = list-active (selection yellow)
        ("ansi-black",      "#767676"), // B: lifted (#000000 fails on black)
        ("ansi-red",        "#ff5f5f"), // B: lifted from #cd3131
        ("ansi-green",      "#44ff44"), // B: = success
        ("ansi-yellow",     "#ffff00"), // B: = primary
        ("ansi-blue",       "#6cb6ff"), // B: lifted from #2472c8
        ("ansi-magenta",    "#ff77ff"), // B: lifted from #bc3fbc
        ("ansi-cyan",       "#44ffff"), // B: lifted from #11a8cd
        ("ansi-white",      "#ffffff"), // B: = text
        ("ansi-br-black",   "#a6a6a6"), // B: lifted from #666666
        ("ansi-br-red",     "#ff8787"), // B: lifted from #f14c4c
        ("ansi-br-green",   "#7bff7b"), // B: lifted from #23d18b
        ("ansi-br-yellow",  "#ffff87"), // B: lifted from #f5f543
        ("ansi-br-blue",    "#9cd0ff"), // B: lifted from #3b8eea
        ("ansi-br-magenta", "#ff9cff"), // B: lifted from #d670d6
        ("ansi-br-cyan",    "#87ffff"), // B: lifted from #29b8db
        ("ansi-br-white",   "#ffffff")); // B: = text

    private static IReadOnlyDictionary<string, Rgb> Build(params (string Token, string Hex)[] rows)
    {
        var map = new Dictionary<string, Rgb>(rows.Length, StringComparer.Ordinal);
        foreach (var (token, hex) in rows)
            map[token] = Parse(hex);
        return map;
    }

    /// <summary>Parses <c>#RRGGBB</c> or <c>#RRGGBBAA</c> (CSS order) into an <see cref="Rgb"/>.</summary>
    public static Rgb Parse(string hex) => Rgb.Parse(hex);

    /// <summary>
    /// Token → Avalonia resource key: <c>Color</c> + the PascalCased token (<c>text-muted</c> →
    /// <c>ColorTextMuted</c>), except <c>primary-h</c>, which maps to <c>ColorPrimaryHover</c>.
    /// </summary>
    public static string ResourceKey(string token) => token switch
    {
        "surface-2" => "ColorSurface2",
        "primary-h" => "ColorPrimaryHover",
        "text-muted" => "ColorTextMuted",
        _ => "Color" + string.Concat(token.Split('-').Select(Capitalize)),
    };

    private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
