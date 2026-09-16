namespace Historia.Services.State;

public sealed record StatusChangedEventArgs(RepositoryStatus Status);
public sealed record SelectionChangedEventArgs(ObjectId? CommitId);
public sealed record RevealCommitEventArgs(ObjectId CommitId);
public sealed record RefsChangedEventArgs(
    IReadOnlyList<BranchInfo> Branches,
    IReadOnlyList<TagInfo> Tags,
    IReadOnlyList<RemoteInfo> Remotes);
public sealed record ProfileChangedEventArgs(GitProfile? Profile);
public sealed record OperationStateChangedEventArgs(OperationState State);
public sealed record OpenDiffChangedEventArgs(DiffRequest? Request);

/// <summary>
/// The contract between every producer (Git services, watcher, profile service) and every
/// consumer (WPF controls and Blazor components) for a single repository (§6.7). The
/// graph and power tools plug into this same surface unchanged.
/// <para>
/// Threading contract: all mutation happens on a background thread; every event is raised on
/// the WPF dispatcher captured at construction. Consumers must unsubscribe on dispose.
/// </para>
/// </summary>
public interface IRepositoryStateService
{
    // ── Current state: always non-blocking reads of cached values ──
    RepositoryInfo Repository { get; }
    RepositoryStatus Status { get; }
    BranchInfo? CurrentBranch { get; }
    IReadOnlyList<BranchInfo> Branches { get; }
    IReadOnlyList<TagInfo> Tags { get; }
    IReadOnlyList<RemoteInfo> Remotes { get; }
    ObjectId? SelectedCommitId { get; }
    IReadOnlyList<string> SelectedPaths { get; }
    GitProfile? ActiveProfile { get; }
    OperationState PendingOperation { get; }

    /// <summary>The diff currently occupying the center panel, or null when the graph is shown (§V2-8.2).</summary>
    DiffRequest? OpenDiff { get; }

    // ── Mutations: safe to call from any thread ──
    void SetSelectedCommit(ObjectId? id);
    void SetSelectedPaths(IReadOnlyList<string> paths);

    /// <summary>
    /// Asks the graph to bring <paramref name="id"/> into view and centre it vertically
    /// (§V3-B). Selection is a separate concern — call <see cref="SetSelectedCommit"/> too.
    /// A no-op consumer is fine; the graph is the only listener.
    /// </summary>
    void RequestRevealCommit(ObjectId id);

    /// <summary>Opens or closes the center diff. Idempotent; safe to call from any thread (§V2-8.2).</summary>
    void SetOpenDiff(DiffRequest? request);
    void SetActiveProfile(GitProfile? profile);
    Task RefreshStatusAsync(CancellationToken ct = default);
    Task RefreshRefsAsync(CancellationToken ct = default);
    Task RefreshAllAsync(CancellationToken ct = default);

    /// <summary>Populate branches/tags/remotes synchronously from the on-disk repo-state cache
    /// (if enabled and present) so the sidebar paints before the first async refresh. No event
    /// is raised — consumers read the current lists when they bind.</summary>
    void SeedFromCache();

    // ── Events: raised on the UI dispatcher ──
    event EventHandler<StatusChangedEventArgs>? StatusChanged;
    event EventHandler<SelectionChangedEventArgs>? SelectedCommitChanged;

    /// <summary>Raised on the UI dispatcher when something asks the graph to scroll a commit into view.</summary>
    event EventHandler<RevealCommitEventArgs>? RevealCommitRequested;
    event EventHandler<RefsChangedEventArgs>? RefsChanged;
    event EventHandler<ProfileChangedEventArgs>? ActiveProfileChanged;
    event EventHandler<OperationStateChangedEventArgs>? OperationStateChanged;

    /// <summary>Raised on the UI dispatcher, like every other event on this service (§V2-8.2).</summary>
    event EventHandler<OpenDiffChangedEventArgs>? OpenDiffChanged;
}
