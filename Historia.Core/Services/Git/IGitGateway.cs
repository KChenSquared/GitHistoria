namespace Historia.Services.Git;

/// <summary>
/// The only Git surface the UI and higher-level services talk to (§6.4.2). Decides per
/// operation whether to use LibGit2Sharp (fast, in-process, no network) or the Git CLI
/// (canonical semantics, credential-aware, network-capable) per the routing table (§6.4.1).
/// One instance per repository tab, owned by <c>RepositoryContext</c>.
/// </summary>
public interface IGitGateway
{
    string RepositoryPath { get; }

    // ── Reads ──
    Task<RepositoryInfo> GetRepositoryInfoAsync(CancellationToken ct = default);
    Task<RepositoryStatus> GetStatusAsync(CancellationToken ct = default);

    /// <summary>
    /// Paged linear commit history (§I-6.4.2). Retained as the paged-history API surface — the
    /// underlying <c>LibGit2Service.ProjectCommits</c> is still covered by tests.
    /// </summary>
    Task<IReadOnlyList<CommitSummary>> GetCommitsAsync(CommitQuery query, CancellationToken ct = default);
    Task<CommitDetail> GetCommitDetailAsync(ObjectId id, CancellationToken ct = default);
    Task<IReadOnlyList<FileStatusEntry>> GetCommitFilesAsync(ObjectId id, int parentIndex, CancellationToken ct = default);
    Task<IReadOnlyList<BranchInfo>> GetBranchesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<TagInfo>> GetTagsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<RemoteInfo>> GetRemotesAsync(CancellationToken ct = default);
    Task<FileDiff> GetFileDiffAsync(DiffRequest request, CancellationToken ct = default);

    /// <summary>Ahead/behind of a local branch versus its upstream, computed lazily per visible row (§6.12).</summary>
    Task<AheadBehind?> GetAheadBehindAsync(string branchCanonicalName, CancellationToken ct = default);

    /// <summary>The repository's currently configured <c>user.name</c>/<c>user.email</c>, read fresh (§9.5.1, §9.6).</summary>
    Task<(string? Name, string? Email)> GetConfiguredIdentityAsync(CancellationToken ct = default);

    /// <summary>The user's <b>global</b> <c>~/.gitconfig</c> <c>user.name</c>/<c>user.email</c>, read
    /// fresh via <c>git config --global --get</c> (§9.6). Used as the conflict-banner baseline when no
    /// primary profile is explicitly set.</summary>
    Task<(string? Name, string? Email)> GetGlobalIdentityAsync(CancellationToken ct = default);

    /// <summary>The effective <c>commit.gpgsign</c> (local → global), read fresh. Used under
    /// <i>Local config</i> where Historia manages no signing settings of its own (§14.4).</summary>
    Task<bool> GetGpgSignConfiguredAsync(CancellationToken ct = default);

    // ── Working tree mutations ──
    Task StageAsync(IReadOnlyList<string> paths, CancellationToken ct = default);
    Task UnstageAsync(IReadOnlyList<string> paths, CancellationToken ct = default);
    Task DiscardAsync(IReadOnlyList<string> paths, CancellationToken ct = default);
    Task<ObjectId> CommitAsync(CommitRequest request, CancellationToken ct = default);

    /// <summary>Merges <paramref name="refish"/> into the current branch (CLI-routed, §6.4.1). Conflicts surface as <see cref="Models.GitErrorKind.MergeConflict"/>.</summary>
    Task MergeAsync(string refish, bool noFastForward, CancellationToken ct = default);

    /// <summary>Moves the current branch to <paramref name="target"/> (CLI-routed). <c>Hard</c> is destructive.</summary>
    Task ResetAsync(string target, GitResetMode mode, CancellationToken ct = default);

    /// <summary>Commits reachable from <paramref name="target"/>..HEAD — i.e. how many commits
    /// would leave the branch if it were reset to <paramref name="target"/> — via
    /// <c>git rev-list --count</c> (CLI-routed, not libgit2's <c>CalculateHistoryDivergence</c>).</summary>
    Task<int> CountCommitsLeavingAsync(string target, CancellationToken ct = default);

    // ── Ref mutations ──
    Task CheckoutAsync(string refName, GitCheckoutOptions options, CancellationToken ct = default);
    Task<BranchInfo> CreateBranchAsync(string name, string startPoint, bool checkout, CancellationToken ct = default);
    Task DeleteBranchAsync(string name, bool force, CancellationToken ct = default);
    Task RenameBranchAsync(string oldName, string newName, CancellationToken ct = default);
    Task SetUpstreamAsync(string branchName, string upstreamRef, CancellationToken ct = default);
    Task<TagInfo> CreateTagAsync(string name, string target, string? message, CancellationToken ct = default);
    Task DeleteTagAsync(string name, CancellationToken ct = default);

    // ── Remotes ──
    Task AddRemoteAsync(string name, string url, CancellationToken ct = default);
    Task RemoveRemoteAsync(string name, CancellationToken ct = default);
    Task SetRemoteUrlAsync(string name, string url, CancellationToken ct = default);

    // ── Network (always CLI; never background in SSH-Only mode) ──
    Task FetchAsync(string remote, GitFetchOptions options, IProgress<GitProgress>? progress, CancellationToken ct = default);
    Task PullAsync(string remote, string branch, GitPullOptions options, IProgress<GitProgress>? progress, CancellationToken ct = default);
    Task PushAsync(string remote, PushSpec spec, IProgress<GitProgress>? progress, CancellationToken ct = default);
}
