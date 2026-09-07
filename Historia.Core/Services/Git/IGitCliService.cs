
namespace Historia.Services.Git;

/// <summary>
/// The single entry point for every Git CLI invocation (§6.5). Enforces one uniform contract:
/// argument-list passing, no shell, deterministic flags, non-interactivity, concurrent stream
/// draining, process-tree cancellation, and failure classification into
/// <see cref="GitErrorKind"/>. Stateless; registered as a singleton.
/// </summary>
public interface IGitCliService
{
    /// <summary>Whether a usable <c>git.exe</c> was discovered (§6.2.3).</summary>
    bool IsGitAvailable { get; }

    /// <summary>Runs one invocation to completion, capturing stdout/stderr and a classification.</summary>
    Task<GitCliResult> RunAsync(GitCliRequest request, CancellationToken ct = default);

    /// <summary>
    /// Runs one invocation, reporting parsed progress from stderr and raw stdout lines as they
    /// stream. Used for clone / fetch / pull / push (§6.13).
    /// </summary>
    Task<GitCliResult> RunAsync(
        GitCliRequest request,
        IProgress<GitProgress>? progress,
        Action<string>? onStdoutLine,
        CancellationToken ct = default);
}
