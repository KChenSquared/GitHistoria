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
    private long _stashStamp;
    private GitObjectId? _selectedCommitId;
    private IReadOnlyList<string> _selectedPaths = [];
    private GitProfile? _activeProfile;
    private OperationState _pendingOperation;
    private DiffRequest? _openDiff;
    private bool _keepConflictResolverOpen;
    private string? _selectedConflictPath;
    private bool _conflictResolverClosed;
    private string? _pendingCommitMessage;

    public event EventHandler<StatusChangedEventArgs>? StatusChanged;
    public event EventHandler<SelectionChangedEventArgs>? SelectedCommitChanged;
    public event EventHandler<RevealCommitEventArgs>? RevealCommitRequested;
    public event EventHandler<CommitMessageProposedEventArgs>? CommitMessageProposed;
    public event EventHandler<RefsChangedEventArgs>? RefsChanged;
    public event EventHandler<ProfileChangedEventArgs>? ActiveProfileChanged;
    public event EventHandler<OperationStateChangedEventArgs>? OperationStateChanged;
    public event EventHandler<OpenDiffChangedEventArgs>? OpenDiffChanged;
    public event EventHandler? ConflictSessionChanged;
    public event EventHandler<SelectedConflictPathChangedEventArgs>? SelectedConflictPathChanged;
    public event EventHandler? ConflictResolverClosedChanged;

    private readonly IGitGateway _gateway;
    private readonly SingleFlightCoalescer _refsCoalescer;
    private readonly SingleFlightCoalescer _statusCoalescer;

    public RepositoryStateService(
        RepositoryContext context,
        IUiDispatcher? ui = null,
        IRepoStateCache? repoCache = null,
        IGitGateway? gateway = null)
    {
        _context = context;
        _gateway = gateway ?? context.Gateway;
        _refsCoalescer = new(() => _context.Lifetime);
        _statusCoalescer = new(() => _context.Lifetime);
        _ui = ui;
        _repoCache = repoCache;
        _stashStamp = RefStateToken.StashStamp(context.GitDirectory);
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
    public long StashStamp { get { lock (_sync) return _stashStamp; } }
    public GitObjectId? SelectedCommitId { get { lock (_sync) return _selectedCommitId; } }
    public IReadOnlyList<string> SelectedPaths { get { lock (_sync) return _selectedPaths; } }
    public DiffRequest? OpenDiff { get { lock (_sync) return _openDiff; } }
    public GitProfile? ActiveProfile { get { lock (_sync) return _activeProfile; } }
    public OperationState PendingOperation { get { lock (_sync) return _pendingOperation; } }
    public bool KeepConflictResolverOpen { get { lock (_sync) return _keepConflictResolverOpen; } }
    public bool IsConflictSessionActive { get { lock (_sync) return ComputeIsConflictSessionActive(_status, _pendingOperation, _keepConflictResolverOpen); } }
    public string? SelectedConflictPath { get { lock (_sync) return _selectedConflictPath; } }
    public bool IsConflictResolverClosed { get { lock (_sync) return _conflictResolverClosed; } }

    private static bool ComputeIsConflictSessionActive(RepositoryStatus status, OperationState operation, bool keepOpen)
    {
        return status.Conflicted.Count > 0 ||
            (keepOpen && operation is OperationState.Merging or OperationState.Rebasing or OperationState.CherryPicking or OperationState.Reverting);
    }

    public void SetKeepConflictResolverOpen(bool keep)
    {
        bool flipped;
        bool nowActive;
        bool closedCleared = false;
        lock (_sync)
        {
            if (_keepConflictResolverOpen == keep)
                return;
            bool wasActive = ComputeIsConflictSessionActive(_status, _pendingOperation, _keepConflictResolverOpen);
            _keepConflictResolverOpen = keep;
            nowActive = ComputeIsConflictSessionActive(_status, _pendingOperation, _keepConflictResolverOpen);
            flipped = wasActive != nowActive;
            if (flipped && !nowActive)
            {
                _selectedConflictPath = null;
            }
            if (flipped)
            {
                closedCleared = _conflictResolverClosed;
                _conflictResolverClosed = false;
            }
        }

        if (flipped)
            HandleConflictSessionFlipped(nowActive, closedCleared);
    }

    public void SetConflictResolverClosed(bool closed)
    {
        lock (_sync)
        {
            if (_conflictResolverClosed == closed)
                return;
            _conflictResolverClosed = closed;
        }

        Raise(() => ConflictResolverClosedChanged?.Invoke(this, EventArgs.Empty));
    }

    public void SetSelectedConflictPath(string? path)
    {
        bool changed;
        lock (_sync)
        {
            if (string.Equals(_selectedConflictPath, path, StringComparison.Ordinal))
                return;
            _selectedConflictPath = path;
            changed = true;
        }

        if (changed)
            Raise(() => SelectedConflictPathChanged?.Invoke(this, new SelectedConflictPathChangedEventArgs(path)));
    }

    private void HandleConflictSessionFlipped(bool nowActive, bool closedCleared)
    {
        Raise(() => ConflictSessionChanged?.Invoke(this, EventArgs.Empty));
        if (closedCleared)
            Raise(() => ConflictResolverClosedChanged?.Invoke(this, EventArgs.Empty));

        if (nowActive)
        {
            SetSelectedCommit(null);
        }
        else
        {
            Raise(() => SelectedConflictPathChanged?.Invoke(this, new SelectedConflictPathChangedEventArgs(null)));
        }
    }

    public BranchInfo? CurrentBranch
    {
        get { lock (_sync) return _branches.FirstOrDefault(b => b.IsCurrent); }
    }

    public void SetSelectedCommit(GitObjectId? id)
    {
        bool diffCleared;
        lock (_sync)
        {
            if (_selectedCommitId == id)
                return;
            _selectedCommitId = id;
            // selecting a different commit must not leave the previous commit's file
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
            // Idempotent: DiffRequest is a record, so an equal request raises nothing — unless it
            // carries a focus line, which is raised again so the open viewer scrolls back to it.
            if (_openDiff == request && request?.FocusLine is null)
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

    public void RequestRevealCommit(GitObjectId id) =>
        Raise(() => RevealCommitRequested?.Invoke(this, new RevealCommitEventArgs(id)));

    public string? PendingCommitMessage
    {
        get
        {
            lock (_sync)
                return _pendingCommitMessage;
        }
    }

    public void ProposeCommitMessage(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        lock (_sync)
            _pendingCommitMessage = message;

        Raise(() => CommitMessageProposed?.Invoke(this, new CommitMessageProposedEventArgs(message)));
    }

    public void ClearPendingCommitMessage()
    {
        lock (_sync)
            _pendingCommitMessage = null;
    }

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

    public Task RefreshStatusAsync(CancellationToken ct = default)
        => _statusCoalescer.RunAsync(DoRefreshStatusAsync, ct);

    private async Task DoRefreshStatusAsync(Action markDataRead, CancellationToken ct)
    {
        using var _perf = Historia.Diagnostics.PerfLog.Scope($"RepositoryStateService.RefreshStatusAsync({_context.DisplayName})");
        var status = await _gateway.GetStatusAsync(ct).ConfigureAwait(false);
        var info = await _gateway.GetRepositoryInfoAsync(ct).ConfigureAwait(false);
        markDataRead();
        var operation = DetectOperationState(_context.GitDirectory);

        bool operationChanged;
        bool openDiffCleared;
        bool conflictSessionFlipped;
        bool nowActive;
        bool closedCleared = false;
        lock (_sync)
        {
            bool wasActive = ComputeIsConflictSessionActive(_status, _pendingOperation, _keepConflictResolverOpen);

            bool isOperationInProgress = operation is OperationState.Merging or OperationState.Rebasing or OperationState.CherryPicking or OperationState.Reverting;
            if (status.Conflicted.Count > 0 && isOperationInProgress)
            {
                _keepConflictResolverOpen = true;
            }
            else if (!isOperationInProgress)
            {
                _keepConflictResolverOpen = false;
            }

            // A rebase or cherry-pick that stops on its next commit keeps the session active, so
            // conflicts reappearing after all were resolved reopen a closed resolver too.
            bool conflictsAppeared = _status.Conflicted.Count == 0 && status.Conflicted.Count > 0;

            _status = status;
            _repository = info;
            operationChanged = _pendingOperation != operation;
            _pendingOperation = operation;

            nowActive = ComputeIsConflictSessionActive(_status, _pendingOperation, _keepConflictResolverOpen);
            conflictSessionFlipped = wasActive != nowActive;
            if (conflictSessionFlipped && !nowActive)
            {
                _selectedConflictPath = null;
            }
            if (conflictSessionFlipped || conflictsAppeared)
            {
                closedCleared = _conflictResolverClosed;
                _conflictResolverClosed = false;
            }

            // the centre panel shows the open file diff in place of the commit graph.
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

        if (conflictSessionFlipped)
            HandleConflictSessionFlipped(nowActive, closedCleared);
        else if (closedCleared)
            Raise(() => ConflictResolverClosedChanged?.Invoke(this, EventArgs.Empty));

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

    public Task RefreshRefsAsync(CancellationToken ct = default)
        => _refsCoalescer.RunAsync(DoRefreshRefsAsync, ct);

    private async Task DoRefreshRefsAsync(Action markDataRead, CancellationToken ct)
    {
        using var _perf = Historia.Diagnostics.PerfLog.Scope($"RepositoryStateService.RefreshRefsAsync({_context.DisplayName})");
        var stamp = RefStateToken.StashStamp(_context.GitDirectory);
        var branches = await _gateway.GetBranchesAsync(ct).ConfigureAwait(false);
        var remotes = await _gateway.GetRemotesAsync(ct).ConfigureAwait(false);

        markDataRead();

        bool branchesOrRemotesChanged;
        bool stashChanged;
        IReadOnlyList<TagInfo> existingTags;
        lock (_sync)
        {
            // BranchInfo / RemoteInfo are records — structural comparison.
            branchesOrRemotesChanged = !branches.SequenceEqual(_branches)
                                      || !remotes.SequenceEqual(_remotes);
            stashChanged = stamp != _stashStamp;
            _stashStamp = stamp;
            _branches = branches;
            _remotes = remotes;
            existingTags = _tags;
        }

        // publish branches and remotes first so the sidebar populates in ~57ms rather than waiting
        // seconds on thousands of tags to project.
        if (branchesOrRemotesChanged || stashChanged)
            Raise(() => RefsChanged?.Invoke(this, new RefsChangedEventArgs(branches, existingTags, remotes) { StashStamp = stamp }));

        var tags = await _gateway.GetTagsAsync(ct).ConfigureAwait(false);

        bool tagsChanged;
        lock (_sync)
        {
            // TagInfo is a record — structural comparison.
            tagsChanged = !tags.SequenceEqual(_tags);
            _tags = tags;
        }

        // Keep _repoCache.SaveRefs a single write after tags complete — do not write a half-populated ref snapshot.
        _repoCache?.SaveRefs(_context.RepositoryPath, branches, tags, remotes);

        // Raise second time if tags changed.
        if (tagsChanged)
            Raise(() => RefsChanged?.Invoke(this, new RefsChangedEventArgs(branches, tags, remotes) { StashStamp = stamp }));
    }

    public void SeedFromCache() => SeedFrom(_repoCache?.TryLoad(_context.RepositoryPath));

    /// <summary>Seeds from a snapshot the caller already read (<see cref="RepositoryContext.OpenAsync"/>
    /// reads it alongside discovery). No event: nothing has bound to this state yet.</summary>
    internal void SeedFrom(RepoStateSnapshot? snap)
    {
        if (snap is null)
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

    // ── Status-raise coalescing on a 50 ms trailing window ──

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

    /// <summary>Derives <see cref="OperationState"/> from on-disk sequencer markers.</summary>
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

    /// <summary>
    /// One refresh at a time per kind. A caller arriving before the running refresh has read its
    /// data joins it; one arriving after queues a single trailing run. A run's work gets a token
    /// that cancels when the tab closes or when every caller waiting on that run has given up
    /// (their tokens cancelled) — a tab the user switched away from stops paying for a refresh
    /// nobody is waiting for. A caller with a non-cancellable token keeps its run alive.
    /// </summary>
    private sealed class SingleFlightCoalescer(Func<CancellationToken> lifetime)
    {
        private readonly object _gate = new();
        private Run? _current;  // running
        private Run? _trailing; // queued behind _current

        private sealed class Run
        {
            public readonly TaskCompletionSource Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public readonly CancellationTokenSource Abandoned = new();
            public int Waiters;      // guarded by the coalescer's _gate
            public bool HasReadData; // guarded by the coalescer's _gate
        }

        public Task RunAsync(Func<Action, CancellationToken, Task> work, CancellationToken ct)
        {
            // A caller that already gave up never starts (or joins) work.
            if (ct.IsCancellationRequested)
                return Task.FromCanceled(ct);

            Run run;
            var start = false;
            lock (_gate)
            {
                if (_current is null)
                {
                    _current = run = new Run();
                    start = true;
                }
                else if (!_current.HasReadData && !_current.Abandoned.IsCancellationRequested)
                {
                    run = _current;
                }
                else
                {
                    if (_trailing is { Abandoned.IsCancellationRequested: true })
                        _trailing = null; // everyone waiting on it left; it will never start
                    run = _trailing ??= new Run();
                }
                run.Waiters++;
            }

            if (start)
                _ = PumpAsync(work);
            return WaitAsync(run, ct);
        }

        private async Task WaitAsync(Run run, CancellationToken ct)
        {
            try
            {
                await TaskGate.WaitWithCancellationAsync(run.Done.Task, ct).ConfigureAwait(false);
            }
            finally
            {
                bool abandon;
                lock (_gate)
                {
                    run.Waiters--;
                    abandon = run.Waiters == 0 && !run.Done.Task.IsCompleted;
                }
                // Outside the lock: cancelling runs the work's cancellation callbacks.
                if (abandon)
                {
                    try { run.Abandoned.Cancel(); }
                    catch (ObjectDisposedException) { }
                }
            }
        }

        private async Task PumpAsync(Func<Action, CancellationToken, Task> work)
        {
            while (true)
            {
                Run run;
                lock (_gate)
                    run = _current!;

                var (canceled, fault) = await ExecuteAsync(run, work).ConfigureAwait(false);

                lock (_gate)
                {
                    if (canceled)
                        run.Done.TrySetCanceled();
                    else if (fault is not null)
                        run.Done.TrySetException(fault);
                    else
                        run.Done.TrySetResult();

                    _current = _trailing;
                    _trailing = null;
                    if (_current is null)
                        return;
                }
            }
        }

        private async Task<(bool Canceled, Exception? Fault)> ExecuteAsync(Run run, Func<Action, CancellationToken, Task> work)
        {
            CancellationTokenSource? linked = null;
            try
            {
                var tabLifetime = lifetime();
                if (run.Abandoned.IsCancellationRequested || tabLifetime.IsCancellationRequested)
                    return (true, null);
                linked = CancellationTokenSource.CreateLinkedTokenSource(tabLifetime, run.Abandoned.Token);
                await work(() => { lock (_gate) run.HasReadData = true; }, linked.Token).ConfigureAwait(false);
                return (false, null);
            }
            catch (OperationCanceledException)
            {
                return (true, null);
            }
            catch (ObjectDisposedException) when (lifetime().IsCancellationRequested)
            {
                // The tab's context was disposed under the run.
                return (true, null);
            }
            catch (Exception) when (linked?.IsCancellationRequested == true)
            {
                // A git call killed by the cancellation can surface as a failure; it is still a
                // cancellation, not a fault to report.
                return (true, null);
            }
            catch (Exception ex)
            {
                return (false, ex);
            }
            finally
            {
                linked?.Dispose();
            }
        }
    }
}
