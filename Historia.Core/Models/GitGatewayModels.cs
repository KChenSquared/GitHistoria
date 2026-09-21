namespace Historia.Models;

/// <summary>Request to create a commit (§6.9.2).</summary>
public sealed record CommitRequest
{
    public required string Message { get; init; }

    /// <summary>Author identity. Supplied explicitly from the active profile (§9.5.1).</summary>
    public required string AuthorName { get; init; }
    public required string AuthorEmail { get; init; }

    /// <summary>Committer identity. Defaults to the author when null.</summary>
    public string? CommitterName { get; init; }
    public string? CommitterEmail { get; init; }

    public bool AmendPreviousCommit { get; init; }
    public bool AllowEmptyCommit { get; init; }

    /// <summary>Sign the commit with the active profile's key (routes through the CLI, §6.4.1).</summary>
    public bool Sign { get; init; }
}

public sealed record GitCheckoutOptions
{
    /// <summary>Discard local changes that would block the checkout.</summary>
    public bool Force { get; init; }
    public bool CreateBranch { get; init; }
    public string? NewBranchName { get; init; }
}

public sealed record GitFetchOptions
{
    public bool Prune { get; init; }
    public bool Tags { get; init; }
    public bool All { get; init; }

    /// <summary>§12.1: this fetch is an auto-fetch, not a user-initiated one. Routing must not
    /// raise the switch-to-Local-config fallback (that prompt is user-ops only, §13.1); the
    /// caller swallows failures quietly.</summary>
    public bool Background { get; init; }

    public static GitFetchOptions From(AppSettings s, bool background = false) => new()
    {
        Prune = s.PruneOnFetch,
        Background = background,
    };
}

public sealed record GitPullOptions
{
    public bool Rebase { get; init; }
    public bool Prune { get; init; }

    /// <summary>Passes <c>--allow-unrelated-histories</c> — the explicit opt-in a user gives after
    /// a first pull hits <see cref="GitErrorKind.UnrelatedHistories"/>.</summary>
    public bool AllowUnrelatedHistories { get; init; }

    public static GitPullOptions From(AppSettings s) => new()
    {
        Rebase = s.PullWithRebase,
        Prune = s.PruneOnFetch,
    };
}

/// <summary>What to push and how (§6.13).</summary>
public sealed record PushSpec
{
    public required string RefSpec { get; init; }
    public bool SetUpstream { get; init; }

    /// <summary>Force push uses <c>--force-with-lease --force-if-includes</c> — never plain <c>--force</c> (§6.13).</summary>
    public bool ForceWithLease { get; init; }
    public bool PushTags { get; init; }
}

/// <summary>Divergence of a local branch from its upstream (§6.12).</summary>
public readonly record struct AheadBehind(int Ahead, int Behind);
