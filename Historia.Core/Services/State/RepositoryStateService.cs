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
    private bool _keepConflictResolverOpen;
    private string? _selectedConflictPath;

    public event EventHandler<StatusChangedEventArgs>? StatusChanged;
    public event EventHandler<SelectionChangedEventArgs>? SelectedCommitChanged;
    public event EventHandler<RevealCommitEventArgs>? RevealCommitRequested;
    public event EventHandler<RefsChangedEventArgs>? RefsChanged;
    public event EventHandler<ProfileChangedEventArgs>? ActiveProfileChanged;
    public event EventHandler<OperationStateChangedEventArgs>? OperationStateChanged;
    public event EventHandler<OpenDiffChangedEventArgs>? OpenDiffChanged;
    public event EventHandler? ConflictSessionChanged;
    public event EventHandler<SelectedConflictPathChangedEventArgs>? SelectedConflictPathChanged;

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
        _refsCoalescer = new(() => _context.Lifetime.IsCancellationRequested);
        _statusCoalescer = new(() => _context.Lifetime.IsCancellationRequested);
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
    public bool KeepConflictResolverOpen { get { lock (_sync) return _keepConflictResolverOpen; } }
    public bool IsConflictSessionActive { get { lock (_sync) return ComputeIsConflictSessionActive(_status, _pendingOperation, _keepConflictResolverOpen); } }
    public string? SelectedConflictPath { get { lock (_sync) return _selectedConflictPath; } }

    private static bool ComputeIsConflictSessionActive(RepositoryStatus status, OperationState operation, bool keepOpen)
    {
        return status.Conflicted.Count > 0 ||
            (keepOpen && operation is OperationState.Merging or OperationState.Rebasing or OperationState.CherryPicking or OperationState.Reverting);
    }

    public void SetKeepConflictResolverOpen(bool keep)
    {
        bool flipped;
        bool nowActive;
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
        }

        if (flipped)
            HandleConflictSessionFlipped(nowActive);
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

    private void HandleConflictSessionFlipped(bool nowActive)
    {
        Raise(() => ConflictSessionChanged?.Invoke(this, EventArgs.Empty));

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

    public void SetSelectedCommit(ObjectId? id)
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
            // Idempotent: DiffRequest is a record, so an equal request raises nothing.
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

    public Task RefreshStatusAsync(CancellationToken ct = default)
        => _statusCoalescer.RunAsync(DoRefreshStatusAsync, ct);

    private async Task DoRefreshStatusAsync(Action markDataRead)
    {
        using var _perf = Historia.Diagnostics.PerfLog.Scope($"RepositoryStateService.RefreshStatusAsync({_context.DisplayName})");
        var status = await _gateway.GetStatusAsync(_context.Lifetime).ConfigureAwait(false);
        var info = await _gateway.GetRepositoryInfoAsync(_context.Lifetime).ConfigureAwait(false);
        markDataRead();
        var operation = DetectOperationState(_context.GitDirectory);

        bool operationChanged;
        bool openDiffCleared;
        bool conflictSessionFlipped;
        bool nowActive;
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
            HandleConflictSessionFlipped(nowActive);

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

    private async Task DoRefreshRefsAsync(Action markDataRead)
    {
        using var _perf = Historia.Diagnostics.PerfLog.Scope($"RepositoryStateService.RefreshRefsAsync({_context.DisplayName})");
        var branches = await _gateway.GetBranchesAsync(_context.Lifetime).ConfigureAwait(false);
        var remotes = await _gateway.GetRemotesAsync(_context.Lifetime).ConfigureAwait(false);

        markDataRead();

        bool branchesOrRemotesChanged;
        IReadOnlyList<TagInfo> existingTags;
        lock (_sync)
        {
            // BranchInfo / RemoteInfo are records — structural comparison.
            branchesOrRemotesChanged = !branches.SequenceEqual(_branches)
                                      || !remotes.SequenceEqual(_remotes);
            _branches = branches;
            _remotes = remotes;
            existingTags = _tags;
        }

        // publish branches and remotes first so the sidebar populates in ~57ms rather than waiting
        // seconds on thousands of tags to project.
        if (branchesOrRemotesChanged)
            Raise(() => RefsChanged?.Invoke(this, new RefsChangedEventArgs(branches, existingTags, remotes)));

        var tags = await _gateway.GetTagsAsync(_context.Lifetime).ConfigureAwait(false);

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

    private sealed class SingleFlightCoalescer
    {
        private readonly Func<bool>? _isCancellationRequested;
        private readonly object _gate = new();
        private Task? _inFlightPump;
        private TaskCompletionSource _currentTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource? _trailingTcs;
        private bool _hasReadData;

        public SingleFlightCoalescer(Func<bool>? isCancellationRequested = null)
        {
            _isCancellationRequested = isCancellationRequested;
        }

        public Task RunAsync(Func<Action, Task> work, CancellationToken ct)
        {
            Task taskToAwait;
            lock (_gate)
            {
                if (_inFlightPump == null || _currentTcs.Task.IsCompleted)
                {
                    // _currentTcs.Task.IsCompleted can be true even with _inFlightPump non-null:
                    // if `work` faults or completes synchronously, RunPumpAsync runs to
                    // completion (via lock reentrancy) inside the RunPumpAsync(work) call below,
                    // finishing and nulling _inFlightPump before that call returns — only for the
                    // assignment to then overwrite it with the now-already-completed task.
                    if (_inFlightPump != null && _trailingTcs != null)
                    {
                        taskToAwait = _trailingTcs.Task;
                    }
                    else
                    {
                        _currentTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        _trailingTcs = null;
                        _hasReadData = false;
                        taskToAwait = _currentTcs.Task;
                        _inFlightPump = RunPumpAsync(work);
                    }
                }
                else if (!_hasReadData)
                {
                    // Caller arriving before data is read joins the in-flight run.
                    taskToAwait = _currentTcs.Task;
                }
                else
                {
                    // Caller arriving after data is read queues a trailing run.
                    _trailingTcs ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    taskToAwait = _trailingTcs.Task;
                }
            }

            return TaskGate.WaitWithCancellationAsync(taskToAwait, ct);
        }

        private void MarkDataRead()
        {
            lock (_gate)
            {
                _hasReadData = true;
            }
        }

        private async Task RunPumpAsync(Func<Action, Task> work)
        {
            try
            {
                while (true)
                {
                    TaskCompletionSource current;
                    lock (_gate)
                    {
                        current = _currentTcs;
                    }

                    Exception? fault = null;
                    bool canceled = false;
                    try
                    {
                        await work(MarkDataRead).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        canceled = true;
                    }
                    catch (ObjectDisposedException) when (_isCancellationRequested?.Invoke() == true)
                    {
                        canceled = true;
                    }
                    catch (Exception ex)
                    {
                        fault = ex;
                    }

                    lock (_gate)
                    {
                        if (canceled)
                            current.TrySetCanceled();
                        else if (fault != null)
                            current.TrySetException(fault);
                        else
                            current.TrySetResult();

                        if (_trailingTcs is null)
                        {
                            _inFlightPump = null;
                            return;
                        }

                        _currentTcs = _trailingTcs;
                        _trailingTcs = null;
                        _hasReadData = false;
                    }
                }
            }
            finally
            {
                lock (_gate)
                {
                    _inFlightPump = null;
                }
            }
        }
    }
}
