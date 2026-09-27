
namespace Historia.Services.Git;

/// <summary>
/// Classifies a failed Git CLI invocation into a <see cref="GitErrorKind"/> by exit code plus
/// stderr pattern matching. <c>LC_ALL=C</c> guarantees English message text.
/// Unrecognized failures return <see cref="GitErrorKind.Unknown"/> — never a silent fallback.
/// </summary>
public static class GitErrorClassifier
{
    /// <summary>
    /// Unambiguous "the remote was reached and refused the ref" markers. These win over the
    /// ordered <see cref="Rules"/> below because a rejected push <i>also</i> prints the generic
    /// <c>error: failed to push some refs</c> line, and often a trailing <c>fatal: unable to
    /// access …</c> when git retries a second transport — both of which would otherwise match
    /// <see cref="GitErrorKind.NetworkUnreachable"/> first and hide the real, recoverable cause.
    /// </summary>
    private static readonly string[] StrongRejectionNeedles =
    [
        "! [rejected]",
        "updates were rejected",
        "non-fast-forward",
        "(fetch first)",
        "tip of your current branch is behind",
        "protected branch",
        "pre-receive hook declined",
        "push declined",
    ];

    private static readonly (GitErrorKind Kind, string[] Needles)[] Rules =
    [
        (GitErrorKind.HostKeyVerificationFailed, ["host key verification failed"]),
        (GitErrorKind.AuthenticationFailed, [
            "permission denied (publickey)",
            "authentication failed",
            "could not read username",
            "could not read password",
            "invalid username or password",
            "http basic: access denied",
            "error: 401",
            "error: 403",
            "remote: invalid credentials",
        ]),
        (GitErrorKind.NetworkUnreachable, [
            "could not resolve host",
            "connection refused",
            "connection timed out",
            "network is unreachable",
            "failed to connect to",
            "unable to access",
            "operation timed out",
            "temporary failure in name resolution",
        ]),
        // Weak catch-alls only — the unambiguous rejection markers live in
        // StrongRejectionNeedles and are checked before NetworkUnreachable. These two also
        // fire on a plain transport failure mid-push, so they stay *after* NetworkUnreachable.
        (GitErrorKind.RemoteRejected, [
            "failed to push some refs",
            "the remote end hung up",
        ]),
        (GitErrorKind.MergeConflict, [
            "automatic merge failed",
            "conflict (",
            "needs merge",
            "you have unmerged paths",
            "fix conflicts and then commit",
        ]),
        (GitErrorKind.UncommittedChanges, [
            "would be overwritten by",
            "please commit your changes or stash them",
            "cannot pull with rebase: you have unstaged changes",
            "your local changes to the following files would be overwritten",
        ]),
        // "unable to create" used to be a needle here too, but git also uses that exact phrase
        // for ordinary filesystem failures unrelated to a lock — e.g. "unable to create file
        // src/x.cs: Permission denied" during a checkout with a build holding the file open, or
        // into a directory the user cannot write. That sent an unrelated permission error to the
        // "another git process is running, check for a stale index.lock" message. index.lock and
        // the process-running message are both unambiguous on their own; keep only those.
        (GitErrorKind.LockedIndex, ["index.lock", "another git process seems to be running"]),
        (GitErrorKind.NotAGitRepository, ["not a git repository"]),
        (GitErrorKind.LfsMissing, ["git-lfs", "this repository is configured for git lfs but"]),
        (GitErrorKind.UnrelatedHistories, ["refusing to merge unrelated histories"]),
        (GitErrorKind.DetachedHeadOperation, ["you are not currently on a branch", "detached head"]),
        (GitErrorKind.HookFailed, ["hook declined", "hook failed", "cannot spawn hook", "pre-commit hook", "commit-msg hook"]),
        (GitErrorKind.InvalidRefName, ["is not a valid ref name", "cannot lock ref", "not a valid object name", "not a valid ref"]),
        (GitErrorKind.RefNotFound, [
            "unknown revision or path",
            "ambiguous argument",
            "couldn't find remote ref",
            "no such ref",
            "pathspec",
        ]),
    ];

    public static GitErrorKind Classify(int exitCode, string stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr))
            return exitCode == 0 ? GitErrorKind.None : GitErrorKind.Unknown;

        var haystack = stderr.ToLowerInvariant();

        foreach (var needle in StrongRejectionNeedles)
        {
            if (haystack.Contains(needle, StringComparison.Ordinal))
                return GitErrorKind.RemoteRejected;
        }

        foreach (var (kind, needles) in Rules)
        {
            foreach (var needle in needles)
            {
                if (haystack.Contains(needle, StringComparison.Ordinal))
                    return kind;
            }
        }

        return GitErrorKind.Unknown;
    }
}
