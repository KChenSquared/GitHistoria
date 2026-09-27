
namespace Historia.Services.Theming;

/// <summary>
/// The single source of truth for every colour the native WPF layer paints — the window
/// chrome, the generated resource dictionary, and the graph renderer. Token names
/// match the CSS custom properties in <c>wwwroot/app.css</c> one-for-one, minus the
/// <c>--color-</c> prefix; <c>PaletteParityTests</c> asserts the two agree.
/// <para>
/// Dark and Light are Visual Studio Code Dark Modern / Light Modern. Every entry
/// carries its VS Code key (Table A, verified verbatim from the theme JSON) or its derivation
/// (Table B, recomputed from Table A) so re-verification is a diff against upstream.
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
        // conflict resolver washes
        "conflict-ours-bg", "conflict-theirs-bg", "conflict-base-bg", "conflict-marker-bg", "conflict-resolved-bg",
        // conflict intra-line highlights
        "conflict-ours-hl", "conflict-theirs-hl",
        // code editor selection
        "code-selection",
        // graph ref badge label — white in dark/HC, near-black in light. The web
        // layer never consumes it, but parity keeps the two copies honest (rule 26).
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

    // ── Dark Modern ──────────────────────────────────────────────────────────────────────
    private static readonly IReadOnlyDictionary<string, Rgb> Dark = Build(
        ("bg",            "#1F1F1F"),   // A: editor.background
        ("chrome",        "#181818"),   // A: sideBar/titleBar/statusBar/panel .background
        ("surface",       "#202020"),   // A: editorWidget.background
        ("surface-2",     "#313131"),   // A: input.background, dropdown.background
        ("border",        "#2B2B2B"),   // A: panel.border, sideBar.border, titleBar.border
        ("border-strong", "#3C3C3C"),   // A: input.border, dropdown.border
        ("primary",       "#0078D4"),   // A: focusBorder, button.background
        ("primary-h",     "#026EC1"),   // A: button.hoverBackground
        ("accent",        "#4DAAFC"),   // A: textLink.foreground
        ("text",          "#CCCCCC"),   // A: foreground
        ("text-muted",    "#9D9D9D"),   // A: descriptionForeground
        ("text-faint",    "#6E7681"),   // A: editorLineNumber.foreground
        ("caption-text",  "#CCCCCC"),   // A: titleBar.activeForeground
        ("danger",        "#F85149"),   // A: errorForeground
        ("warning",       "#BB8009"),   // B: settings.modifiedItemIndicator, alpha dropped
        ("success",       "#2EA043"),   // B: = git-added
        ("list-hover",    "#2B2B2B"),   // B: = border (Dark Modern's one-step-up surface)
        ("list-active",   "#0078D433"), // B: primary @ 20% alpha
        ("list-inactive", "#2B2B2B"),   // B: = list-hover
        ("scrollbar",     "#9D9D9D66"), // B: text-muted @ 40% alpha
        ("git-added",     "#2EA043"),   // A: editorGutter.addedBackground
        ("git-deleted",   "#F85149"),   // A: editorGutter.deletedBackground
        ("git-modified",  "#E2C08D"),   // A: chat.editedFileForeground
        ("git-untracked", "#2EA043"),   // B: = git-added (no hue distinction)
        ("git-conflict",  "#F85149"),   // B: = danger
        ("git-ignored",   "#9D9D9D"),   // B: = text-muted
        ("diff-add-bg",   "#2EA04326"), // B: git-added @ 15% alpha
        ("diff-del-bg",   "#F8514926"), // B: git-deleted @ 15% alpha
        ("diff-add-hl",   "#2EA04359"), // B: git-added @ 35% alpha (stronger intra-line wash)
        ("diff-del-hl",   "#F8514959"), // B: git-deleted @ 35% alpha (stronger intra-line wash)
        ("diff-empty-bg", "#80808014"), // B: neutral padding row wash
        ("conflict-ours-bg",     "#0078D433"), // B: primary @ 20% alpha
        ("conflict-theirs-bg",   "#BB800933"), // B: warning @ 20% alpha
        ("conflict-base-bg",     "#9D9D9D26"), // B: text-muted @ 15% alpha
        ("conflict-marker-bg",   "#3C3C3C4D"), // B: border-strong @ 30% alpha
        ("conflict-resolved-bg", "#2EA04326"), // B: success @ 15% alpha
        ("conflict-ours-hl",     "#0078D466"), // B: primary @ 40% alpha
        ("conflict-theirs-hl",   "#BB800966"), // B: warning @ 40% alpha
        ("code-selection",       "#264F78"),   // A: editor.selectionBackground
        ("ref-label",     "#FFFFFF"),  // graph ref badge label
        // terminal. term-* are role aliases (B); the ANSI set is VS Code dark_vs
        // terminal.ansi* verbatim (A).
        ("term-bg",         "#1F1F1F"), // B: = bg
        ("term-fg",         "#CCCCCC"), // B: = text
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

    // ── Light Modern ─────────────────────────────────────────────────────────────────────
    private static readonly IReadOnlyDictionary<string, Rgb> Light = Build(
        ("bg",            "#FFFFFF"),   // A: editor.background
        ("chrome",        "#F8F8F8"),   // A: sideBar/titleBar/statusBar/panel .background
        ("surface",       "#F8F8F8"),   // A: editorWidget.background
        ("surface-2",     "#FFFFFF"),   // A: input.background, dropdown.background
        ("border",        "#E5E5E5"),   // A: panel.border, sideBar.border, titleBar.border
        ("border-strong", "#CECECE"),   // A: input.border, dropdown.border
        ("primary",       "#005FB8"),   // A: focusBorder, button.background
        ("primary-h",     "#0258A8"),   // A: button.hoverBackground
        ("accent",        "#005FB8"),   // A: textLink.foreground
        ("text",          "#3B3B3B"),   // A: foreground
        ("text-muted",    "#616161"),   // A: activityBar.inactiveForeground (descriptionForeground is unusable — = foreground)
        ("text-faint",    "#6E7681"),   // A: editorLineNumber.foreground
        ("caption-text",  "#1E1E1E"),   // A: titleBar.activeForeground
        ("danger",        "#F85149"),   // A: errorForeground (Light Modern uses the same red as dark)
        ("warning",       "#BB8009"),   // B: settings.modifiedItemIndicator, alpha dropped
        ("success",       "#2EA043"),   // B: = git-added
        ("list-hover",    "#F2F2F2"),   // A: list.hoverBackground
        ("list-active",   "#E8E8E8"),   // A: list.activeSelectionBackground
        ("list-inactive", "#E4E6F1"),   // B: modernActivityBarItem.activeBackground
        ("scrollbar",     "#61616166"), // B: text-muted @ 40% alpha
        ("git-added",     "#2EA043"),   // A: editorGutter.addedBackground
        ("git-deleted",   "#F85149"),   // A: editorGutter.deletedBackground
        ("git-modified",  "#895503"),   // A: chat.editedFileForeground
        ("git-untracked", "#2EA043"),   // B: = git-added
        ("git-conflict",  "#F85149"),   // B: = danger
        ("git-ignored",   "#616161"),   // B: = text-muted
        ("diff-add-bg",   "#2EA04326"), // B: git-added @ 15% alpha
        ("diff-del-bg",   "#F8514926"), // B: git-deleted @ 15% alpha
        ("diff-add-hl",   "#2EA0434D"), // B: git-added @ 30% alpha (stronger intra-line wash)
        ("diff-del-hl",   "#F851494D"), // B: git-deleted @ 30% alpha (stronger intra-line wash)
        ("diff-empty-bg", "#80808014"), // B: neutral padding row wash
        ("conflict-ours-bg",     "#005FB826"), // B: primary @ 15% alpha
        ("conflict-theirs-bg",   "#BB800926"), // B: warning @ 15% alpha
        ("conflict-base-bg",     "#61616126"), // B: text-muted @ 15% alpha
        ("conflict-marker-bg",   "#CECECE4D"), // B: border-strong @ 30% alpha
        ("conflict-resolved-bg", "#2EA04326"), // B: success @ 15% alpha
        ("conflict-ours-hl",     "#005FB84D"), // B: primary @ 30% alpha
        ("conflict-theirs-hl",   "#BB80094D"), // B: warning @ 30% alpha
        ("code-selection",       "#ADD6FF"),   // A: editor.selectionBackground
        ("ref-label",     "#1F1F1F"),  // near-black on the light field
        // terminal. term-* are role aliases (B); the ANSI set is VS Code light_vs
        // terminal.ansi* verbatim (A).
        ("term-bg",         "#FFFFFF"), // B: = bg
        ("term-fg",         "#3B3B3B"), // B: = text
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
    // Its own accessibility contract; no VS Code counterpart. Additions are derived
    // from the existing values: chrome = bg (one black ground), border-strong =
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
        // clears 4.5:1 on #000000 (Table B). TerminalContrastTests (M6) verifies every entry.
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
    /// Token → WPF resource key. PascalCase with three legacy spellings preserved so existing
    /// <c>{DynamicResource}</c> bindings in <c>MainWindow.xaml</c> keep working.
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
