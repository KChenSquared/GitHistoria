
namespace Historia.Services.Terminal;

/// <summary>What a terminal session is "for" (V5 §4.2).</summary>
public enum TerminalOriginKind
{
    /// <summary>The global entry point (app menu). Starts in the user's home directory.</summary>
    Home,

    /// <summary>A repository's working directory.</summary>
    Repository,
}

/// <summary>
/// The dictionary key for a live terminal session (V5 §17 open question 6). Deliberately a
/// separate value type rather than an <c>Equals</c>/<c>GetHashCode</c> override on
/// <see cref="TerminalOrigin"/>: the compiler-generated record equality members
/// (<c>EqualityContract</c> / <c>PrintMembers</c>) make a partial override easy to get subtly
/// wrong, and the mistake is silent. Equality is by kind + repo key only — never the working
/// directory, which changes under a session via <c>cd</c>.
/// </summary>
public readonly record struct TerminalOriginKey(TerminalOriginKind Kind, RepoIdentityKey? RepoKey);

/// <summary>
/// Identifies what a terminal session is "for" (V5 §4.2). One live session per
/// <see cref="Key"/> (V5 §2). The <see cref="WorkingDirectory"/> and <see cref="Title"/> are
/// display / start-up data, not identity — see <see cref="TerminalOriginKey"/>.
/// </summary>
public sealed record TerminalOrigin
{
    public required TerminalOriginKind Kind { get; init; }

    /// <summary>Null for <see cref="TerminalOriginKind.Home"/>; set for a repository.</summary>
    public RepoIdentityKey? RepoKey { get; init; }

    /// <summary>Resolved start directory (V5 §4.2). Display only after start-up.</summary>
    public required string WorkingDirectory { get; init; }

    /// <summary>Dock tab caption: "Home", or the repository's folder name.</summary>
    public required string Title { get; init; }

    /// <summary>The session-dictionary key. Ignores <see cref="WorkingDirectory"/> (the
    /// <c>cd</c> trap) and <see cref="Title"/>.</summary>
    public TerminalOriginKey Key => new(Kind, RepoKey);

    public static TerminalOrigin Home(string homeDirectory) => new()
    {
        Kind = TerminalOriginKind.Home,
        WorkingDirectory = homeDirectory,
        Title = "Home",
    };

    public static TerminalOrigin ForRepository(RepoIdentityKey key, string workingDirectory, string title) => new()
    {
        Kind = TerminalOriginKind.Repository,
        RepoKey = key,
        WorkingDirectory = workingDirectory,
        Title = title,
    };
}
