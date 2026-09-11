namespace Historia.Models;

/// <summary>
/// Application-wide settings (§10.1). Persisted as <c>settings.json</c> in
/// <c>%APPDATA%/Historia/</c>, loaded once synchronously at startup (§6.2.2) and written back
/// with debounced atomic writes (§9.3).
/// </summary>
public sealed record AppSettings
{
    /// <summary>Schema version for the migration pipeline (§9.3). v2: graph columns (§V2-11.1);
    /// v3: issue autolinks (§V2-17.6); v4: hidden Author/Date/SHA, session restore on, recents 30
    /// (§V3-13); v5: multi-line tab strip (§V3-B); v6: persistent graph cache toggle; v7: graph
    /// row hard cap; v8: local repo-state cache toggle; v9: terminal (V5); v10: tab-width mode
    /// (V5.5); v11: custom-theme id (§V6-G9); v12: splash-screen toggle.</summary>
    public int SchemaVersion { get; init; } = 16;

    // ── Profile &amp; Identity ──
    /// <summary>§14.2: the default profile — the clone-dialog pre-selection and what an opened
    /// repo adopts when it has no per-repo selection and the default is non-global. Null = the
    /// reserved global profile is the default (the automatic case).</summary>
    public Guid? DefaultProfileId { get; init; }
    public bool ConfirmGlobalConfigChanges { get; init; } = true;

    // ── Network &amp; Privacy (§13) ──
    /// <summary>Gates background polling / hosting-API calls; effective only while a
    /// token-bearing profile is the active per-repo selection.</summary>
    public bool EnableBackgroundPolling { get; init; } = true;
    public TimeSpan PollingInterval { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>§12.1: drives <c>IAutoFetchService</c>'s <c>[InitialFetch]</c> + <c>[AutoFetch]</c>
    /// loop, which periodically <c>git fetch</c>es every open repository. Unlike
    /// <see cref="EnableBackgroundPolling"/> it is <b>not</b> gated to a token-bearing profile —
    /// it fetches SSH and <i>Local config</i> repos too — so this toggle is the single opt-out
    /// that keeps constitution Rule 0.2 satisfiable. Default on.</summary>
    public bool AutoFetchEnabled { get; init; } = true;

    // ── UI preferences ──
    /// <summary>User choice; <c>EffectiveTheme</c> is derived (§11.6). New installs follow the
    /// OS; existing users keep whatever they had (no migration).</summary>
    public ThemeMode AppTheme { get; init; } = ThemeMode.System;

    /// <summary>Independent override; wins over <see cref="AppTheme"/> (§11.6).</summary>
    public bool HighContrastOverride { get; init; }

    /// <summary>§V6-G9: the id of the active custom theme (a file stem under
    /// <c>themes/</c>). Only meaningful while <see cref="AppTheme"/> is
    /// <see cref="ThemeMode.Custom"/>; null otherwise.</summary>
    public string? CustomThemeId { get; init; }

    public DiffViewMode DefaultDiffView { get; init; } = DiffViewMode.SideBySide;
    public bool ShowAvatars { get; init; } = true;

    /// <summary>v8 M9: how the graph and the tab strip interpret mouse-wheel / trackpad scroll.
    /// <c>Auto</c> matches the OS "natural / inverted" setting (macOS &amp; Linux; Windows is
    /// always standard); <c>Standard</c> / <c>Inverted</c> force it.</summary>
    public WheelScrollDirection WheelScrollDirection { get; init; } = WheelScrollDirection.Auto;

    /// <summary>When on (default), a brief splash animation covers startup while the shell and
    /// the focused repository's commit graph load out of sight (§6.2.2). Off = the shell window
    /// appears immediately and the graph fills in as it loads. Takes effect on the next launch.</summary>
    public bool ShowSplashScreen { get; init; } = true;

    /// <summary>§V3-B: when the repo tab strip overflows, wrap tabs onto extra app-band rows
    /// instead of horizontal-scrolling a single row. <see cref="MaxTabRows"/> caps the count.</summary>
    public bool EnableMultiLineTabs { get; init; }

    /// <summary>§V3-B: 2–5. Maximum app-band rows to fill with tabs before the ◀ ▶ paging
    /// arrows appear on the bottom row. Ignored unless <see cref="EnableMultiLineTabs"/>.</summary>
    public int MaxTabRows { get; init; } = 3;

    /// <summary>V5.5: <see cref="TabWidthMode.Dynamic"/> (default) sizes each repo tab to its
    /// label; <see cref="TabWidthMode.Fixed"/> gives every tab <see cref="FixedTabWidth"/> px.</summary>
    public TabWidthMode TabWidthMode { get; init; } = TabWidthMode.Dynamic;

    /// <summary>V5.5: per-tab width in pixels when <see cref="TabWidthMode"/> is
    /// <see cref="TabWidthMode.Fixed"/>. Clamped
    /// <see cref="MinFixedTabWidth"/>…<see cref="MaxFixedTabWidth"/>.</summary>
    public int FixedTabWidth { get; init; } = DefaultFixedTabWidth;

    public const int DefaultFixedTabWidth = 175;
    public const int MinFixedTabWidth = 120;
    public const int MaxFixedTabWidth = 400;

    /// <summary>Math.Clamp to the accepted <see cref="FixedTabWidth"/> range.</summary>
    public static int ClampFixedTabWidth(int value) => Math.Clamp(value, MinFixedTabWidth, MaxFixedTabWidth);

    // §V3-B: graph column layout (order, widths, visibility, the width pin) and the commit-walk
    // ceiling moved to a per-repository store — see Historia.Services.Settings.IRepoLayoutService.

    /// <summary>v8 M13: font size for the selected-commit detail panel — the message body and
    /// the changed-files list share it so they read as one block.</summary>
    public int CommitDetailFontSize { get; init; } = 14;

    /// <summary>v8 M13: flat path list vs. collapsible folder tree for a commit's changed files.</summary>
    public CommitFilesView CommitFilesView { get; init; } = CommitFilesView.Path;

    // ── Diff &amp; editor ──
    public int DiffContextLines { get; init; } = 3;
    public WhitespaceMode DiffWhitespace { get; init; } = WhitespaceMode.Show;
    public int TabWidth { get; init; } = 4;

    // ── Git &amp; repository ──
    /// <summary>Overrides discovery (§6.2.3).</summary>
    public string? GitExecutablePath { get; init; }

    /// <summary>On by default (§V3-9.3, Correction against §I-17 Q4). Restore opens only the
    /// focused repository; every other tab is a placeholder until first focused.</summary>
    public bool RestoreSessionTabs { get; init; } = true;
    public bool PruneOnFetch { get; init; } = true;
    public bool PullWithRebase { get; init; }

    /// <summary>v8.4: when on, the "Stash all" buttons (staging panel + shortcuts bar) prompt
    /// for a stash message instead of using the default <c>Stash of &lt;branch&gt;</c>.</summary>
    public bool UseCustomStashNames { get; init; }
    public int MaxRecentRepositories { get; init; } = 30;

    /// <summary>When on, a repository's enumerated state (local &amp; remote branches, tags,
    /// stashes, submodules, worktrees) is cached to the machine cache folder so the sidebar
    /// paints immediately on the next launch. The real enumeration still runs and corrects any
    /// staleness.</summary>
    public bool PersistRepoCache { get; init; } = true;

    /// <summary>
    /// §7.6: when on, the graph keeps only the fixed-size layout arrays in memory and pages
    /// per-commit metadata (message, author, dates) from a random-access cache file, and
    /// background-caches the whole history to disk. Read once at startup — a change takes
    /// effect on the next launch. Default on.
    /// </summary>
    public bool GraphMetadataFromDisk { get; init; } = true;

    /// <summary>
    /// v10.1.3: when on (default), a large repository (≥ 100k commits walked) that has no
    /// commit-graph file yet gets one written in the background (<c>git commit-graph write
    /// --reachable</c>) once its first full history walk and §7.6 spill have both finished. A
    /// commit-graph is what lets Git's own <c>--topo-order</c> walk (the CLI graph engine)
    /// expand lazily by generation number instead of touching the whole history up front.
    /// </summary>
    public bool WriteCommitGraphForLargeRepos { get; init; } = true;

    /// <summary>
    /// v10.1.3: how many rows the graph's silent background crawler (§V10.1.2) pre-loads into
    /// the resident layout before stopping, so scrolling stays responsive without loading the
    /// full history's worth of rows up front. Deeper history still loads on scroll and reaches
    /// disk in full via the §7.6 background spill regardless of this cap. <c>0</c> means
    /// uncapped — the crawler runs all the way to the resident-row ceiling
    /// (<see cref="GraphLimits.GetMaxResidentRows"/>). Default 0 (uncapped): the gate-tier fix
    /// (v10.1.3) that stopped the §7.6 spill from starving this crawler removed the main reason
    /// to bound it in the first place.
    /// </summary>
    public int SilentCrawlRowCap { get; init; } = 0;

    public const int MinSilentCrawlRowCap = 0;
    public const int MaxSilentCrawlRowCap = GraphLimits.MaxResidentRowsDisk;

    /// <summary>Math.Clamp to the accepted <see cref="SilentCrawlRowCap"/> range. Follows the
    /// house style for a bounded numeric setting.</summary>
    public static int ClampSilentCrawlRowCap(int value) => Math.Clamp(value, MinSilentCrawlRowCap, MaxSilentCrawlRowCap);

    /// <summary>Issue-key → URL rules applied to commit messages (§V2-17.6). Account-free.</summary>
    public IReadOnlyList<IssueAutolinkRule> IssueAutolinks { get; init; } = [];

    /// <summary>Word-wrap diff lines instead of horizontal scroll (§V2-17.4).</summary>
    public bool DiffWordWrap { get; init; }

    // ── External tools ──
    public string? ExternalDiffTool { get; init; }
    public string? ExternalMergeTool { get; init; }
    public string? ExternalTerminal { get; init; }

    // ── Terminal (V5 §13) ──
    /// <summary>Explicit shell override. Null → resolve per platform (V5 §4.1). A set-but-missing
    /// path is a <c>ShellNotFound</c> error, never a silent fallback.</summary>
    public string? TerminalShellPath { get; init; }

    /// <summary>Shell arguments, passed as a list. Default empty — no <c>-l</c> login flag.</summary>
    public IReadOnlyList<string> TerminalShellArgs { get; init; } = [];

    /// <summary>Null → the platform monospace stack (V5 §8).</summary>
    public string? TerminalFontFamily { get; init; }

    public int TerminalFontSize { get; init; } = 13;

    /// <summary>Ring-buffer scrollback per session. Clamped <see cref="MinTerminalScrollback"/>…
    /// <see cref="MaxTerminalScrollback"/>. Multiplies by open sessions — see the settings note.</summary>
    public int TerminalScrollbackLines { get; init; } = 5_000;

    public double TerminalDockHeight { get; init; } = 260;
    public bool TerminalDockOpen { get; init; }
    public TerminalCursorStyle TerminalCursorStyle { get; init; } = TerminalCursorStyle.Block;
    public bool TerminalCursorBlink { get; init; } = true;
    public TerminalBellStyle TerminalBellStyle { get; init; } = TerminalBellStyle.None;

    public const int MinTerminalScrollback = 500;
    public const int MaxTerminalScrollback = 100_000;

    /// <summary>Math.Clamp to the accepted <see cref="TerminalScrollbackLines"/> range. Follows
    /// the house style for a bounded numeric setting.</summary>
    public static int ClampTerminalScrollback(int value) => Math.Clamp(value, MinTerminalScrollback, MaxTerminalScrollback);
}
