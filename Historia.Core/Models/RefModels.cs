namespace Historia.Models;

/// <summary>Local or remote branch (§6.12). Ahead/behind are computed lazily per visible row.</summary>
public sealed record BranchInfo
{
    /// <summary>Friendly name, e.g. "main" or "origin/main".</summary>
    public required string Name { get; init; }
    public required string CanonicalName { get; init; }
    public required bool IsRemote { get; init; }
    public required bool IsCurrent { get; init; }
    public required ObjectId? TipCommitId { get; init; }

    /// <summary>The owning remote's name for a remote-tracking branch (e.g. "origin", or
    /// "team/infra" — a remote name may itself contain a slash). Null for a local branch.
    /// The one reliable way to strip the remote prefix from <see cref="Name"/> (§V3-7.3, §V3-11.1).</summary>
    public string? RemoteName { get; init; }

    public string? UpstreamName { get; init; }

    /// <summary>Null until computed lazily for a visible row (§6.12).</summary>
    public int? AheadBy { get; init; }
    public int? BehindBy { get; init; }
}

public sealed record TagInfo
{
    public required string Name { get; init; }
    public required ObjectId TargetCommitId { get; init; }
    public required bool IsAnnotated { get; init; }
    public string? Message { get; init; }
    public string? TaggerName { get; init; }
    public DateTimeOffset? TaggerWhen { get; init; }
}

public sealed record RemoteInfo
{
    public required string Name { get; init; }
    public required string Url { get; init; }
    public string? PushUrl { get; init; }
}

/// <summary>Detached-HEAD is a first-class state (§6.12).</summary>
public sealed record RepositoryInfo
{
    public required string WorkingDirectory { get; init; }
    public required string GitDirectory { get; init; }
    public required string DisplayName { get; init; }
    public required bool IsBare { get; init; }
    public required bool IsHeadDetached { get; init; }
    public required bool IsHeadUnborn { get; init; }
    public required string? CurrentBranchName { get; init; }

    /// <summary>
    /// True when an executable <c>pre-commit</c> or <c>commit-msg</c> hook is present, so the
    /// commit must route through the CLI (§6.9.2). Checked per status refresh and cached.
    /// </summary>
    public bool HasCommitHooks { get; init; }
}
