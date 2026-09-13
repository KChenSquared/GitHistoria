namespace Historia.Services.State;

/// <inheritdoc cref="IRepositoryStateService"/>
public sealed class RepositoryStateService : IRepositoryStateService, IAsyncDisposable
{
    private readonly RepositoryContext _context;
    private readonly IUiDispatcher? _ui;
    private readonly IRepoStateCache? _repoCache;
    private readonly object _sync = new();

    private readonly IUiTimer? _statusCoalesceTimer;
    private RepositoryStatus? _pendingStatusRaise;

    private RepositoryInfo _repository;
    private RepositoryStatus _status = RepositoryStatus.Empty;
    private IReadOnlyList<BranchInfo> _branches = [];
    private IReadOnlyList<TagInfo> _tags = [];
    private IReadOnlyList<RemoteInfo> _remotes = [];
    private ObjectId? _selectedCommitId;
    private IReadOnlyList<string> _selectedPaths = [];
    private GitProfile? _activeProfile;
    private OperationState _pendingOperation;
    private DiffRequest? _openDiff;

    public event EventHandler<StatusChangedEventArgs>? StatusChanged;
    public event EventHandler<SelectionChangedEventArgs>? SelectedCommitChanged;
    public event EventHandler<RevealCommitEventArgs>? RevealCommitRequested;
    public event EventHandler<RefsChangedEventArgs>? RefsChanged;
    public event EventHandler<ProfileChangedEventArgs>? ActiveProfileChanged;
    public event EventHandler<OperationStateChangedEventArgs>? OperationStateChanged;
    public event EventHandler<OpenDiffChangedEventArgs>? OpenDiffChanged;

    public RepositoryStateService(RepositoryContext context, IUiDispatcher? ui = null, IRepoStateCache? repoCache = null)
    {
        _context = context;
        _ui = ui;
        _repoCache = repoCache;
        _repository = new RepositoryInfo
        {
            WorkingDirectory = context.RepositoryPath,
            GitDirectory = context.GitDirectory,
            DisplayName = context.DisplayName,
            IsBare = context.IsBare,
            IsHeadDetached = false,
            IsHeadUnborn = false,
            CurrentBranchName = null,
        };

        if (_ui is not null)
            _statusCoalesceTimer = _ui.CreateTimer(TimeSpan.FromMilliseconds(50), OnStatusCoalesceTick);
    }

    public RepositoryInfo Repository { get { lock (_sync) return _repository; } }
    public RepositoryStatus Status { get { lock (_sync) return _status; } }
    public IReadOnlyList<BranchInfo> Branches { get { lock (_sync) return _branches; } }
    public IReadOnlyList<TagInfo> Tags { get { lock (_sync) return _tags; } }
    public IReadOnlyList<RemoteInfo> Remotes { get { lock (_sync) return _remotes; } }
    public ObjectId? SelectedCommitId { get { lock (_sync) return _selectedCommitId; } }
    public IReadOnlyList<string> SelectedPaths { get { lock (_sync) return _selectedPaths; } }
    public DiffRequest? OpenDiff { get { lock (_sync) return _openDiff; } }
    public GitProfile? ActiveProfile { get { lock (_sync) return _activeProfile; } }
    public OperationState PendingOperation { get { lock (_sync) return _pendingOperation; } }

    public BranchInfo? CurrentBranch
    {
        get { lock (_sync) return _branches.FirstOrDefault(b => b.IsCurrent); }
    }

    public void SetSelectedCommit(ObjectId? id)
    {
        bool diffCleared;
        lock (_sync)
        {
            if (_selectedCommitId == id)
                return;
            _selectedCommitId = id;
            // §V2-8.2: selecting a different commit must not leave the previous commit's file
            // diff filling the center panel — the single most likely stale state in the feature.
            diffCleared = _openDiff is not null;
            _openDiff = null;
        }

        Raise(() => SelectedCommitChanged?.Invoke(this, new SelectionChangedEventArgs(id)));
        if (diffCleared)
            Raise(() => OpenDiffChanged?.Invoke(this, new OpenDiffChangedEventArgs(null)));
    }

    public void SetOpenDiff(DiffRequest? request)
    {
        lock (_sync)
        {
            // Idempotent: DiffRequest is a record, so an equal request raises nothing (§V2-8.2).
            if (_openDiff == request)
                return;
            _openDiff = request;
        }

        Raise(() => OpenDiffChanged?.Invoke(this, new OpenDiffChangedEventArgs(request)));
    }

    public void SetSelectedPaths(IReadOnlyList<string> paths)
    {
        lock (_sync)
            _selectedPaths = paths;
    }

    public void RequestRevealCommit(ObjectId id) =>
        Raise(() => RevealCommitRequested?.Invoke(this, new RevealCommitEventArgs(id)));

    public void SetActiveProfile(GitProfile? profile)
    {
        lock (_sync)
        {
            if (_activeProfile?.Id == profile?.Id)
                return;
            _activeProfile = profile;
        }

        Raise(() => ActiveProfileChanged?.Invoke(this, new ProfileChangedEventArgs(profile)));
    }

    public async Task RefreshStatusAsync(CancellationToken ct = default)
    {
        using var _perf = Historia.Diagnostics.PerfLog.Scope($"RepositoryStateService.RefreshStatusAsync({_context.DisplayName})");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _context.Lifetime);
        var status = await _context.Gateway.GetStatusAsync(linked.Token).ConfigureAwait(false);
        var info = await _context.Gateway.GetRepositoryInfoAsync(linked.Token).ConfigureAwait(false);
        var operation = DetectOperationState(_context.GitDirectory);

        bool operationChanged;
        bool openDiffCleared;
        lock (_sync)
        {
            _status = status;
            _repository = info;
            operationChanged = _pendingOperation != operation;
            _pendingOperation = operation;

            // §V2-8.1: the centre panel shows the open file diff in place of the commit graph.
            // A commit, an amend, a stage/unstage, or an external edit can drop the file a
            // working-tree diff was showing (or clean its whole side) with no explicit Close,
            // leaving a permanent "No changes" panel covering the graph — and switching repos
            // and back just re-shows that stale diff. Clear it here, on the path every refresh
            // takes, rather than in a staging panel that is only wired up while it is mounted.
            openDiffCleared = _openDiff is { } od && IsWorkingTreeDiffStale(od, status);
            if (openDiffCleared)
                _openDiff = null;
        }

        QueueStatusRaise(status);

        if (openDiffCleared)
            Raise(() => OpenDiffChanged?.Invoke(this, new OpenDiffChangedEventArgs(null)));

        if (operationChanged)
            Raise(() => OperationStateChanged?.Invoke(this, new OperationStateChangedEventArgs(operation)));
    }

    /// <summary>
    /// True when <paramref name="request"/> is an index/working-tree diff whose subject is no
    /// longer part of that view — the file left the list (committed, staged, unstaged, reverted)
    /// or, for a whole-source diff, the list is now empty. Commit/Comparison diffs are never
    /// stale this way: they are pinned to explicit object ids, not to working-tree state.
    /// </summary>
    private static bool IsWorkingTreeDiffStale(DiffRequest request, RepositoryStatus status)
    {
        IReadOnlyList<FileStatusEntry> list;
        switch (request.Source)
        {
            case DiffSource.StagedChanges:
                list = status.Staged;
                break;
            case DiffSource.UnstagedChanges:
                list = [.. status.Unstaged, .. status.Untracked, .. status.Conflicted];
                break;
            default:
                return false; // Commit / Comparison diffs are pinned to object ids, never stale here
        }

        if (request.Path is null)
            return list.Count == 0;

        return !list.Any(f => f.Path == request.Path
            || (f.OldPath is not null && f.OldPath == request.Path));
    }

    public async Task RefreshRefsAsync(CancellationToken ct = default)
    {
        using var _perf = Historia.Diagnostics.PerfLog.Scope($"RepositoryStateService.RefreshRefsAsync({_context.DisplayName})");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _context.Lifetime);
        var branches = await _context.Gateway.GetBranchesAsync(linked.Token).ConfigureAwait(false);
        var tags = await _context.Gateway.GetTagsAsync(linked.Token).ConfigureAwait(false);
        var remotes = await _context.Gateway.GetRemotesAsync(linked.Token).ConfigureAwait(false);

        bool changed;
        lock (_sync)
        {
            // BranchInfo / TagInfo / RemoteInfo are records — structural comparison.
            changed = !branches.SequenceEqual(_branches)
                      || !tags.SequenceEqual(_tags)
                      || !remotes.SequenceEqual(_remotes);
            _branches = branches;
            _tags = tags;
            _remotes = remotes;
        }

        _repoCache?.SaveRefs(_context.RepositoryPath, branches, tags, remotes);

        // An unchanged ref set raises nothing: a spurious watcher-driven refs refresh would
        // otherwise push consumers (the graph control especially) into a full re-walk for no
        // reason. StatusChanged has its own coalescing; this is the RefsChanged equivalent.
        if (changed)
            Raise(() => RefsChanged?.Invoke(this, new RefsChangedEventArgs(branches, tags, remotes)));
    }

    public void SeedFromCache()
    {
        if (_repoCache?.TryLoad(_context.RepositoryPath) is not { } snap)
            return;
        lock (_sync)
        {
            if (snap.Branches is { Count: > 0 } b) _branches = b;
            if (snap.Tags is { } t) _tags = t;
            if (snap.Remotes is { } r) _remotes = r;
        }
    }

    public async Task RefreshAllAsync(CancellationToken ct = default)
    {
        await RefreshRefsAsync(ct).ConfigureAwait(false);
        await RefreshStatusAsync(ct).ConfigureAwait(false);
    }

    // ── Status-raise coalescing on a 50 ms trailing window (§6.7) ──

    private void QueueStatusRaise(RepositoryStatus status)
    {
        if (_ui is null || _statusCoalesceTimer is null)
        {
            StatusChanged?.Invoke(this, new StatusChangedEventArgs(status));
            return;
        }

        _ui.Post(() =>
        {
            _pendingStatusRaise = status;
            _statusCoalesceTimer.Stop();
            _statusCoalesceTimer.Start();
        });
    }

    private void OnStatusCoalesceTick()
    {
        _statusCoalesceTimer!.Stop();
        var status = _pendingStatusRaise;
        _pendingStatusRaise = null;
        if (status is not null)
            StatusChanged?.Invoke(this, new StatusChangedEventArgs(status));
    }

    private void Raise(Action action)
    {
        if (_ui is null || _ui.CheckAccess())
            action();
        else
            _ui.Post(action);
    }

    /// <summary>Derives <see cref="OperationState"/> from on-disk sequencer markers (§8.9.2).</summary>
    internal static OperationState DetectOperationState(string gitDir)
    {
        if (System.IO.Directory.Exists(System.IO.Path.Combine(gitDir, "rebase-merge")) ||
            System.IO.Directory.Exists(System.IO.Path.Combine(gitDir, "rebase-apply")))
            return OperationState.Rebasing;
        if (System.IO.File.Exists(System.IO.Path.Combine(gitDir, "CHERRY_PICK_HEAD")))
            return OperationState.CherryPicking;
        if (System.IO.File.Exists(System.IO.Path.Combine(gitDir, "REVERT_HEAD")))
            return OperationState.Reverting;
        if (System.IO.File.Exists(System.IO.Path.Combine(gitDir, "MERGE_HEAD")))
            return OperationState.Merging;
        if (System.IO.File.Exists(System.IO.Path.Combine(gitDir, "BISECT_LOG")))
            return OperationState.Bisecting;
        return OperationState.None;
    }

    public ValueTask DisposeAsync()
    {
        _statusCoalesceTimer?.Dispose();
        return ValueTask.CompletedTask;
    }
}
