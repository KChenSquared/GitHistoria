namespace Historia.Models;

/// <summary>What the user chose in Settings &gt; Appearance (§11.6).</summary>
public enum ThemeMode
{
    Dark,
    Light,
    System,

    /// <summary>§V6-G9: a user-authored theme selected from the Themes panel. The active
    /// theme's id is <c>AppSettings.CustomThemeId</c>; it overlays a Dark or Light base.</summary>
    Custom,
}

/// <summary>
/// What is actually rendered, after resolving <see cref="ThemeMode.System"/> against the OS
/// and applying the High Contrast override. Deliberately a different type from
/// <see cref="ThemeMode"/> (§11.6). Resolution order: HighContrast &gt; System-resolved OS
/// theme &gt; explicit Dark/Light.
/// </summary>
public enum EffectiveTheme
{
    Dark,
    Light,
    HighContrast,
}

public enum DiffViewMode
{
    Unified,
    SideBySide,
}

/// <summary>
/// How wide each repository tab in the app band is (V5.5).
/// <see cref="Dynamic"/> — the default — sizes every tab to its own label, so a short repo
/// name gets a short tab; a long name grows the tab only up to a character cap, then
/// ellipsises. <see cref="Fixed"/> gives every tab the same <c>FixedTabWidth</c> pixels, the
/// pre-V5.5 behaviour.
/// </summary>
public enum TabWidthMode
{
    Dynamic,
    Fixed,
}

public enum WhitespaceMode
{
    Show,
    IgnoreTrailing,
    IgnoreAll,
}

/// <summary>v8 M13: how the selected-commit detail panel lists changed files.</summary>
public enum CommitFilesView
{
    /// <summary>Flat list of full paths (the original behaviour).</summary>
    Path,

    /// <summary>Collapsible folder tree.</summary>
    Tree,
}

/// <summary>v8 M9: how the custom wheel handlers (graph body, tab strip) read scroll input.
/// <see cref="Auto"/> follows the OS "natural / inverted" setting via
/// <c>IScrollDirectionProbe</c> (macOS &amp; Linux; Windows is always standard).</summary>
public enum WheelScrollDirection
{
    Auto,
    Standard,
    Inverted,
}

/// <summary>Scope for writing git configuration values (§9.8).</summary>
public enum GitConfigScope
{
    /// <summary>Writes to the repository's local <c>.git/config</c>.</summary>
    Local,

    /// <summary>Writes to the user's global <c>~/.gitconfig</c>.</summary>
    Global,

    /// <summary>Writes to the system-wide git config (rarely used).</summary>
    System,
}

/// <summary>Result of a profile deletion operation (§14.5).</summary>
public enum ProfileDeleteResult
{
    /// <summary>A non-global profile was removed; the default profile was unaffected.</summary>
    Deleted,

    /// <summary>A non-global profile that was the current default was removed;
    /// <see cref="AppSettings.DefaultProfileId"/> is now null (global is the automatic default).</summary>
    DeletedClearedDefault,

    /// <summary>The reserved global profile cannot be deleted; nothing changed.</summary>
    CannotDeleteGlobal,

    NotFound,
}

/// <summary>
/// Closed set of Git CLI failure classifications (§6.5.3). Raw stderr is never surfaced
/// unmodified; every kind maps to a user-facing message and, where applicable, a remedy.
/// <see cref="Unknown"/> is deliberately part of the set — implementing code must not invent
/// silent fallbacks for unrecognized failures.
/// </summary>
public enum GitErrorKind
{
    None,
    AuthenticationFailed,
    HostKeyVerificationFailed,
    NetworkUnreachable,
    RemoteRejected,
    MergeConflict,
    UncommittedChanges,
    LockedIndex,
    NotAGitRepository,
    RefNotFound,
    InvalidRefName,
    DetachedHeadOperation,
    HookFailed,
    LfsMissing,
    UnrelatedHistories,
    Cancelled,
    Unknown,
}

/// <summary>Kind of change for a working-tree file entry (§6.8.1).</summary>
public enum FileChangeKind
{
    Added,
    Modified,
    Deleted,
    Renamed,
    Copied,
    TypeChanged,
    Untracked,
    Ignored,
    Conflicted,
}

/// <summary>
/// In-progress multi-step Git operation, derived from on-disk markers on every status
/// refresh (§6.7, §8.9.2).
/// </summary>
public enum OperationState
{
    None,
    Merging,
    Rebasing,
    CherryPicking,
    Reverting,
    Bisecting,
}

/// <summary>Kind of change for a diffed file (§6.10.1).</summary>
public enum DiffKind
{
    Added,
    Modified,
    Deleted,
    Renamed,
    Copied,
    TypeChanged,
}

/// <summary>Kind of decoration (ref pointer) attached to a commit (§6.11, §7.2).</summary>
public enum DecorationType
{
    LocalBranch,
    RemoteBranch,
    Tag,
    Head,
    Stash,
}

/// <summary>Which refs a history/graph query includes (§6.4.2).</summary>
public enum CommitRefScope
{
    AllRefs,
    CurrentBranch,
}

/// <summary><c>git reset</c> mode (§7.6 context menu). <see cref="Hard"/> discards working-tree changes.</summary>
public enum GitResetMode
{
    Soft,
    Mixed,
    Hard,
}
