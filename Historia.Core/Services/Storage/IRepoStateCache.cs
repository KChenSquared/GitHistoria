
namespace Historia.Services.Storage;

/// <summary>
/// Last-known enumerated state of one repository — local &amp; remote branches, tags, stashes,
/// submodules, worktrees. Any list is null when that half has not been cached yet.
/// </summary>
public sealed record RepoStateSnapshot(
    IReadOnlyList<BranchInfo>? Branches,
    IReadOnlyList<TagInfo>? Tags,
    IReadOnlyList<RemoteInfo>? Remotes,
    IReadOnlyList<StashEntry>? Stashes,
    IReadOnlyList<SubmoduleInfo>? Submodules,
    IReadOnlyList<WorktreeInfo>? Worktrees);

/// <summary>
/// On-disk cache of a repository's enumerated state so the sidebar can paint immediately on the
/// next launch, before the real (libgit2 / <c>git</c>) enumeration completes. There is no
/// fingerprint: the always-following async refresh overwrites any staleness within a second.
/// A no-op when the "Store a local repository cache" setting is off (but <see cref="WipeAll"/>
/// still runs, so turning it off cleans up).
/// </summary>
public interface IRepoStateCache
{
    /// <summary>Merged snapshot for <paramref name="repoPath"/>, or null when nothing is cached.</summary>
    RepoStateSnapshot? TryLoad(string repoPath);

    void SaveRefs(string repoPath, IReadOnlyList<BranchInfo> branches, IReadOnlyList<TagInfo> tags, IReadOnlyList<RemoteInfo> remotes);

    void SavePowerTools(string repoPath, IReadOnlyList<StashEntry>? stashes, IReadOnlyList<SubmoduleInfo>? submodules, IReadOnlyList<WorktreeInfo>? worktrees);

    /// <summary>Deletes every persisted repo-state file. Returns the count removed.</summary>
    int WipeAll();
}
