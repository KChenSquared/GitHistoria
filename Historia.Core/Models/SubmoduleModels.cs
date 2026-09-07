namespace Historia.Models;

/// <summary>Submodule state (§8.6.1). Derived from <c>git submodule status --recursive</c>.</summary>
public sealed record SubmoduleInfo
{
    public required string Name { get; init; }
    public required string Path { get; init; }
    public required string Url { get; init; }
    public ObjectId? HeadCommitId { get; init; }
    public ObjectId? RegisteredCommitId { get; init; }
    public required SubmoduleStatus Status { get; init; }
    public string? BranchTracking { get; init; }
}

public enum SubmoduleStatus
{
    Clean,          // HEAD matches the registered commit
    Modified,       // HEAD differs (needs a commit in the parent)
    Uninitialized,  // directory empty (needs init + update)
    OutOfSync,      // URL/branch changed in .gitmodules, or conflicted
}

/// <summary>Worktree state (§8.7.1). Parsed from <c>git worktree list --porcelain</c>.</summary>
public sealed record WorktreeInfo
{
    public required string Path { get; init; }
    public required string Branch { get; init; }
    public ObjectId? HeadCommitId { get; init; }
    public required bool IsMainWorktree { get; init; }
    public required bool IsLocked { get; init; }
    public string? LockReason { get; init; }
    public bool IsPrunable { get; init; }
}
