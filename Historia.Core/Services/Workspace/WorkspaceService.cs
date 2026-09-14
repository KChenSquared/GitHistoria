namespace Historia.Services.Workspace;

/// <inheritdoc cref="IWorkspaceService"/>
public sealed class WorkspaceService : IWorkspaceService, IAsyncDisposable
{
    private readonly IServiceProvider _services;
    private readonly IGitCliService _cli;
    private readonly IRecentRepositoryService _recents;
    private readonly IProfileService _profiles;
    private readonly IRepoIdentityService _identity;

    private readonly List<RepositoryContext> _open = [];
    private readonly object _sync = new();
    private RepositoryContext? _focused;

    // §V3-9.3: restored-but-not-yet-focused tabs whose InitializeContextAsync has been deferred.
    // They get the ordinary open path on first focus, exactly once.
    private readonly HashSet<RepositoryContext> _deferredInit = [];

    // §V10.1.2: serial background-init pump. Auto-fetch (§12.1) force-initialises every restored
    // tab; doing that concurrently fanned out ~11 identity history-walks + status refreshes at
    // once and starved the tab the user is actually staring at (observed: linux first paint ~86s
    // on an 11-tab restore). These now drain one at a time, and not until the focused tab has
    // fully loaded. A newly focused deferred tab jumps the queue (FocusInternal).
    private readonly List<RepositoryContext> _bgInitQueue = [];
    private bool _bgInitPumpIdle = true;
    private RepositoryContext? _foregroundInit; // the focused/opened tab currently initialising

    // Per-attempt cancellation for whichever context is currently running
    // InitializeContextCoreAsync, linked to (but distinct from) RepositoryContext.Lifetime —
    // Lifetime only ever cancels on tab close, but a focus switch away from a still-loading tab
    // needs to interrupt its identity/refresh work without tearing the tab down. Entry removed by
    // the owning InitializeContextAsync's own finally once its attempt ends.
    private readonly Dictionary<RepositoryContext, CancellationTokenSource> _attemptCts = [];

    // Signalled (and replaced) every time _foregroundInit changes, so
    // DrainBackgroundInitQueueAsync's wait on the current foreground tab's FirstFullLoad can wake
    // immediately when that tab gets interrupted instead of waiting on a signal that, per the
    // interrupt contract below, will never come on its own.
    private TaskCompletionSource _foregroundChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private AtomicFileWriter? _sessionWriter;
    private AtomicFileWriter SessionWriter => _sessionWriter ??= new AtomicFileWriter(AppPaths.SessionFile);
    private string? _restoredFocusPath;

    // After focusing the restored session's active repo, RestoreSessionAsync pauses
    // briefly before opening the rest of the session's tabs -- see RestoreSessionAsync for why.
    private static readonly TimeSpan FocusPriorityYield = TimeSpan.FromMilliseconds(60);

    // A background (force-initialised, no graph control attached) tab waits at most this
    // long for a first-paint signal that will never come before its identity/refresh runs.
    private static readonly TimeSpan GraphPriorityTimeout = TimeSpan.FromSeconds(8);

    // §V10.1.2: the FOCUSED tab's graph control WILL paint — wait for that, not a short guess,
    // before its identity resolution + RefreshAllAsync take the repo gate. 8s was far too short
    // on a big repo (linux first paint alone is tens of seconds), so identity/refresh jumped the
    // gate mid-first-walk and stalled it for ~60s. This ceiling is only a stuck-guard.
    private static readonly TimeSpan GraphFirstPaintCeiling = TimeSpan.FromSeconds(120);

    /// <summary>
    /// Default query for repository tabs (§7.6, R5). Must match <c>CommitGraphControl._query</c>'s default.
    /// Used by clone precache and inactive-tab background caching so the store matches the tab's first walk.
    /// </summary>
    internal static readonly GraphQuery TabGraphQuery = new()
    {
        RefScope = GraphRefScope.AllRefs,
        IncludeRemoteBranches = true,
    };

    public WorkspaceService(
        IServiceProvider services,
        IGitCliService cli,
        IRecentRepositoryService recents,
        IProfileService profiles,
        IRepoIdentityService identity)
    {
        _services = services;
        _cli = cli;
        _recents = recents;
        _profiles = profiles;
        _identity = identity;
    }

    /// <summary>Sets <see cref="_foregroundInit"/> and wakes anything awaiting
    /// <see cref="_foregroundChanged"/> (the background pump). Caller must hold <see cref="_sync"/>.</summary>
    private void SetForegroundInit(RepositoryContext? context)
    {
        _foregroundInit = context;
        var prev = _foregroundChanged;
        _foregroundChanged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        prev.TrySetResult();
    }

    public event EventHandler<FocusedRepositoryChangedEventArgs>? FocusedRepositoryChanged;
    public event EventHandler<OpenRepositoriesChangedEventArgs>? OpenRepositoriesChanged;

    public IReadOnlyList<RepositoryContext> OpenRepositories
    {
        get { lock (_sync) return _open.ToList(); }
    }

    public RepositoryContext? Focused
    {
        get { lock (_sync) return _focused; }
    }

    public async Task<RepositoryContext> OpenAsync(string path, CancellationToken ct = default)
    {
        var discovered = await Task.Run(() => Repository.Discover(path), ct).ConfigureAwait(false)
            ?? throw new HistoriaGitException(GitErrorKind.NotAGitRepository,
                $"No Git repository was found at or above '{path}'.");

        // Duplicate-open guard: focus the existing tab rather than racing on the index lock (§6.6.3).
        var normalizedGitDir = NormalizeDir(discovered);
        RepositoryContext? existing;
        lock (_sync)
        {
            existing = _open.FirstOrDefault(c =>
                string.Equals(NormalizeDir(c.GitDirectory), normalizedGitDir, StringComparison.OrdinalIgnoreCase));
        }
        if (existing is not null)
        {
            // §I-6.7 / rule 18: never raise an event while holding _sync. FocusInternal fires
            // FocusedRepositoryChanged, whose handlers marshal to the UI thread — doing that
            // under the lock deadlocks against any UI-thread reader of OpenRepositories/Focused.
            FocusInternal(existing);
            return existing;
        }

        return await OpenCoreAsync(path, focus: true, initialize: true, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Shared open path. <paramref name="initialize"/> is false only for a session-restored tab
    /// that is not the focused one — its <see cref="InitializeContextAsync"/> is deferred until
    /// it is first focused, so a 15-tab restore costs one repository open, not fifteen (§V3-9.3).
    /// <paramref name="recordRecent"/> is false for session restore: re-opening the previous
    /// tab-set on launch must not reshuffle (or restamp) the Recent list — that list only moves
    /// when the user explicitly opens or clones a repo.
    /// </summary>
    private async Task<RepositoryContext> OpenCoreAsync(
        string path, bool focus, bool initialize, CancellationToken ct,
        Task<RepositoryContext>? prefetched = null, bool recordRecent = true)
    {
        var context = await (prefetched ?? Task.Run(() => RepositoryContext.Open(path, _services), ct))
            .ConfigureAwait(false);

        lock (_sync)
        {
            _open.Add(context);
            if (!initialize)
                _deferredInit.Add(context);
        }

        if (focus)
            FocusInternal(context);
        RaiseOpenChanged();

        if (recordRecent)
            await _recents.AddAsync(context.RepositoryPath, context.DisplayName, ct).ConfigureAwait(false);
        if (initialize)
        {
            lock (_sync) SetForegroundInit(context);
            _ = InitializeContextAsync(context, foreground: true);
        }

        return context;
    }

    /// <summary>Outcome of one <see cref="InitializeContextCoreAsync"/> attempt, used to
    /// decide whether <see cref="RepositoryContext.MarkFirstFullLoadDone"/> should fire.</summary>
    private enum InitOutcome
    {
        /// <summary>Identity resolution and the first refresh both ran to completion.</summary>
        Completed,

        /// <summary>Cancelled because focus moved away mid-attempt (see <see cref="FocusInternal"/>) —
        /// the tab is still open and still needs a full load, so FirstFullLoad must stay unset;
        /// the attempt is requeued and will be retried.</summary>
        Interrupted,

        /// <summary>Cancelled because the tab itself was closed (RepositoryContext.Lifetime)
        /// — nothing left to retry; FirstFullLoad is still marked done so any waiter unblocks.</summary>
        Closed,
    }

    /// <summary>
    /// Resolves the repo identity key and the cascading active profile (§9.3.1, §9.4), then
    /// performs the first full state refresh. Runs off the open path so a large repo does not
    /// hold up the tab appearing.
    /// </summary>
    private async Task InitializeContextAsync(RepositoryContext context, bool foreground)
    {
        // An attempt-scoped token, linked to (not the same as) context.Lifetime, so
        // FocusInternal can cancel just this attempt — interrupting identity/refresh work for a
        // tab that lost focus — without tearing down the tab itself (only a real close does that).
        var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(context.Lifetime);
        lock (_sync) _attemptCts[context] = attemptCts;

        try
        {
            var outcome = await InitializeContextCoreAsync(context, foreground, attemptCts.Token).ConfigureAwait(false);
            if (outcome != InitOutcome.Interrupted)
                context.MarkFirstFullLoadDone(); // §12.1: unblock any auto-fetch waiter
        }
        finally
        {
            lock (_sync)
            {
                if (_attemptCts.TryGetValue(context, out var current) && ReferenceEquals(current, attemptCts))
                    _attemptCts.Remove(context);
                if (foreground && ReferenceEquals(_foregroundInit, context))
                    SetForegroundInit(null);
            }
            attemptCts.Dispose();
        }
    }

    private async Task<InitOutcome> InitializeContextCoreAsync(RepositoryContext context, bool foreground, CancellationToken attemptToken)
    {
        // Let the graph control's own first walk for this context claim the repo's single-access
        // gate uncontended. Without this, identity resolution below (and RefreshAllAsync after
        // it) starts within a few hundred ms of the tab opening and can hold that gate for
        // seconds on a large repo -- observed: 3+s of the graph's fingerprint+load work sitting
        // in the queue behind it, on a repo where the graph's own paint takes barely over a
        // second once it actually gets a turn. §V10.1.2: the focused tab's graph WILL paint, so
        // wait for that (large stuck-guard ceiling); a background force-init has no graph
        // attached, so only the short ceiling applies. Task.WhenAny never throws or propagates
        // either branch's outcome, so no try/catch is needed here.
        var ceiling = foreground ? GraphFirstPaintCeiling : GraphPriorityTimeout;
        await Task.WhenAny(context.FirstGraphPaint, Task.Delay(ceiling, attemptToken)).ConfigureAwait(false);

        // Identity resolution and the first refs/status refresh don't depend on each
        // other (refresh never reads the active profile identity sets) — run them concurrently
        // so the changed/staged-files panel (which just reacts to RefreshAllAsync's StatusChanged)
        // starts populating right after first paint instead of waiting on identity to finish
        // first. Both use attemptToken so a focus-loss interrupt cancels both at once.
        var identityTask = ResolveIdentityAsync(context, attemptToken);
        var refreshTask = context.State.RefreshAllAsync(attemptToken);

        try
        {
            // identityTask never throws (it swallows everything itself, including cancellation),
            // so only refreshTask can fault this — an OperationCanceledException here means the
            // tab closed or its attempt was interrupted mid-refresh.
            await Task.WhenAll(identityTask, refreshTask).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Classified below.
        }

        if (attemptToken.IsCancellationRequested)
            return context.Lifetime.IsCancellationRequested ? InitOutcome.Closed : InitOutcome.Interrupted;
        return InitOutcome.Completed;
    }

    /// <summary>Repo→profile identity resolution and the cascading active-profile bind/touch/git-config
    /// write (§9.3.1, §9.4, §9.6). Never throws — a failure here must not stop the repo from
    /// opening, and cancellation is classified by the caller against <see cref="InitOutcome"/>.</summary>
    private async Task ResolveIdentityAsync(RepositoryContext context, CancellationToken ct)
    {
        try
        {
            var key = await _identity.ResolveAsync(context, ct).ConfigureAwait(false);

            // Keep the binding's last-known path fresh — the rename case working as intended (§9.3.1).
            var existingBinding = _profiles.GetProfileForRepository(key);
            if (existingBinding is not null)
                await _profiles.BindProfileToRepositoryAsync(key, context.RepositoryPath, existingBinding.Id, ct).ConfigureAwait(false);

            var profile = _profiles.ResolveActiveProfile(key);
            context.State.SetActiveProfile(profile);
            if (profile is not null)
            {
                await _profiles.TouchProfileAsync(profile.Id, ct).ConfigureAwait(false);

                // §9.6: when the repo has no existing user.name/user.email, write the profile's
                // values. A mismatch with existing values is surfaced by the repo view as a
                // non-blocking banner rather than being overwritten here.
                if (!context.IsBare)
                {
                    var (name, email) = await context.Gateway.GetConfiguredIdentityAsync(ct).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(email))
                    {
                        // Check the attempt token before starting the git-config write, then run
                        // the write itself with CancellationToken.None to prevent a focus switch
                        // from killing the git process and leaving .git/config.lock behind.
                        // The write keeps its own 15 s per-call timeout (ProfileService.SetConfigAsync),
                        // so an uncancellable write still can't hang forever.
                        ct.ThrowIfCancellationRequested();
                        await _profiles.ApplyProfileToGitConfigAsync(profile, context.RepositoryPath, GitConfigScope.Local, CancellationToken.None).ConfigureAwait(false);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Classified by the caller.
        }
        catch
        {
            // Identity/profile resolution failure must not stop the repo from opening.
        }
    }

    public async Task<RepositoryContext> InitAsync(string path, string? initialBranch, CancellationToken ct = default)
    {
        Directory.CreateDirectory(path);

        if (_cli.IsGitAvailable)
        {
            var args = new List<string> { "init" };
            if (!string.IsNullOrWhiteSpace(initialBranch))
            {
                args.Add("-b");
                args.Add(initialBranch);
            }

            args.Add(path);
            var result = await _cli.RunAsync(new GitCliRequest
            {
                Arguments = args,
                WorkingDirectory = Path.GetDirectoryName(path.TrimEnd(Path.DirectorySeparatorChar)) ?? path,
            }, ct).ConfigureAwait(false);

            if (!result.IsSuccess)
                throw HistoriaGitException.FromCli(result, "Initialize repository");
        }
        else
        {
            await Task.Run(() => Repository.Init(path), ct).ConfigureAwait(false);
        }

        return await OpenAsync(path, ct).ConfigureAwait(false);
    }

    public async Task<RepositoryContext> CloneAsync(
        string url,
        string targetDirectory,
        CloneOptions options,
        IProgress<GitProgress>? progress,
        CancellationToken ct = default)
    {
        if (!_cli.IsGitAvailable)
            throw new HistoriaGitException(GitErrorKind.Unknown, "Git is required to clone. Install it or set its path in Settings.");

        ValidateCloneUrl(url);

        var createdTarget = !Directory.Exists(targetDirectory)
                            || !Directory.EnumerateFileSystemEntries(targetDirectory).Any();
        var parent = Path.GetDirectoryName(targetDirectory.TrimEnd(Path.DirectorySeparatorChar))
                     ?? Directory.GetCurrentDirectory();
        Directory.CreateDirectory(parent);

        var args = new List<string> { "clone", "--progress" };
        if (options.Recursive) args.Add("--recurse-submodules");
        if (options.Depth is { } d and > 0)
        {
            args.Add("--depth");
            args.Add(d.ToString());
        }
        if (options.Sparse) args.Add("--sparse");

        // "--" marks the end of options: without it, a pasted URL beginning with '-' (e.g.
        // "--upload-pack=<command>") would be parsed by Git as another option rather than the
        // repository address.
        args.Add("--");
        args.Add(url);
        args.Add(targetDirectory);

        var result = await _cli.RunAsync(new GitCliRequest
        {
            Arguments = args,
            WorkingDirectory = parent,
            Timeout = Timeout.InfiniteTimeSpan,
            StreamOutput = true,
        }, progress, onStdoutLine: null, ct).ConfigureAwait(false);

        if (result.ErrorKind == GitErrorKind.Cancelled || ct.IsCancellationRequested)
        {
            // Delete the partial target only if Historia created it — never a pre-existing dir (§6.6.2).
            if (createdTarget)
                TryDeleteDirectory(targetDirectory);
            throw new OperationCanceledException(ct);
        }

        if (!result.IsSuccess)
        {
            if (createdTarget)
                TryDeleteDirectory(targetDirectory);
            throw HistoriaGitException.FromCli(result, "Clone");
        }

        var context = await Task.Run(() => RepositoryContext.Open(targetDirectory, _services)).ConfigureAwait(false);
        if (_services.GetService(typeof(Historia.Services.Graph.IGraphLayoutService))
            is Historia.Services.Graph.IGraphLayoutService graphSvc)
        {
            try
            {
                await graphSvc.PrecacheClonedRepositoryAsync(context, TabGraphQuery, progress, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Cancellation during pre-cache keeps the clone and opens the tab (R5).
            }
            catch (Exception ex)
            {
                Historia.Diagnostics.PerfLog.Mark($"PrecacheClonedRepositoryAsync failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        return await OpenCoreAsync(
            targetDirectory, focus: true, initialize: true, CancellationToken.None, prefetched: Task.FromResult(context)).ConfigureAwait(false);
    }

    public async Task CloseAsync(RepositoryContext context)
    {
        bool wasFocused;
        RepositoryContext? nextFocus = null;
        lock (_sync)
        {
            _open.Remove(context);
            _deferredInit.Remove(context);
            _bgInitQueue.Remove(context); // Don't leave a closed tab queued for retry.
            wasFocused = ReferenceEquals(_focused, context);
            if (wasFocused)
            {
                _focused = null;
                nextFocus = _open.LastOrDefault();
            }
        }

        (_services.GetService(typeof(Historia.Services.Graph.IGraphLayoutService)) as Historia.Services.Graph.IGraphLayoutService)
            ?.Forget(context);

        // V5 §3.3: close the terminal session bound to this tab so its shell releases the
        // working-directory handle before the context is disposed. Keyed by the tab's own id,
        // so a second tab open on the same repository keeps its shell running.
        if (_services.GetService(typeof(Historia.Services.Terminal.ITerminalSessionService))
               is Historia.Services.Terminal.ITerminalSessionService terminals)
        {
            try { await terminals.CloseForTabAsync(context.Id).ConfigureAwait(false); }
            catch { /* teardown must not be blocked by a terminal close */ }
        }

        await context.DisposeAsync().ConfigureAwait(false);
        RaiseOpenChanged();

        if (wasFocused)
        {
            if (nextFocus is not null)
                FocusInternal(nextFocus);
            else
                FocusedRepositoryChanged?.Invoke(this, new FocusedRepositoryChangedEventArgs(context, null));
        }
    }

    public void Focus(RepositoryContext context) => FocusInternal(context);

    public void Move(RepositoryContext context, int newIndex)
    {
        lock (_sync)
        {
            var from = _open.IndexOf(context);
            if (from < 0)
                return;
            newIndex = Math.Clamp(newIndex, 0, _open.Count - 1);
            if (newIndex == from)
                return;
            _open.RemoveAt(from);
            _open.Insert(newIndex, context);
        }

        // §I-6.7 / rule 18: raise outside the lock. RaiseOpenChanged re-persists the session, so
        // the dragged order is what a later RestoreSessionAsync replays.
        RaiseOpenChanged();
    }

    public async Task RestoreSessionAsync(CancellationToken ct = default)
    {
        Historia.Diagnostics.PerfLog.Mark("RestoreSessionAsync start");
        var file = AppPaths.SessionFile;
        if (!System.IO.File.Exists(file))
            return;

        List<string> paths = [];
        string? focusedPath = null;
        try
        {
            var node = JsonNode.Parse(await System.IO.File.ReadAllTextAsync(file, ct).ConfigureAwait(false));
            if (node is JsonArray arr) // v1: bare string[]
            {
                paths = arr.Select(n => n?.GetValue<string>()).Where(p => p is not null).Cast<string>().ToList();
            }
            else if (node is JsonObject obj) // v2: { schemaVersion, paths, focusedPath }
            {
                if (obj["paths"] is JsonArray p)
                    paths = p.Select(n => n?.GetValue<string>()).Where(s => s is not null).Cast<string>().ToList();
                focusedPath = obj["focusedPath"]?.GetValue<string>();
            }
        }
        catch
        {
            return;
        }

        var existing = paths.FirstOrDefault(p => System.IO.Directory.Exists(p)
            && string.Equals(p, focusedPath, StringComparison.OrdinalIgnoreCase));
        var focusTarget = existing ?? paths.FirstOrDefault(System.IO.Directory.Exists);
        _restoredFocusPath = focusTarget;
        // Trim a trailing separator before GetFileName: RepositoryPath (the source of both
        // "paths" and "focusedPath" once persisted) is libgit2's WorkingDirectory, which always
        // ends in one, and GetFileName returns "" for a path ending in a separator.
        var focusTargetName = focusTarget is null
            ? "(none)"
            : System.IO.Path.GetFileName(focusTarget.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));
        Historia.Diagnostics.PerfLog.Mark($"RestoreSessionAsync focusTarget resolved ({paths.Count} paths, focus={focusTargetName})");

        // Kick off every existing path's RepositoryContext.Open concurrently right away.
        // The loop adds tabs to _open (and focuses/initializes the right one) strictly in
        // saved-session order. Once the focused tab is open, the loop pauses briefly before
        // opening the rest — each remaining tab only needs RepositoryContext.Open (no graph
        // work — InitializeContextAsync is deferred until it is actually focused, §V3-9.3),
        // giving the focused tab's graph walk first claim on the CPU/disk it needs to paint as fast
        // as possible.
        var prefetches = new Dictionary<string, Task<RepositoryContext>>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in paths)
        {
            if (System.IO.Directory.Exists(p) && !prefetches.ContainsKey(p))
                prefetches[p] = Task.Run(() => RepositoryContext.Open(p, _services), ct);
        }

        try
        {
            foreach (var path in paths)
            {
                // Consume this path's prefetch exactly once -- a duplicate path later in the session
                // file (or one whose directory was already gone when the map above was built) has no
                // entry left and is skipped.
                if (!prefetches.Remove(path, out var prefetch))
                    continue;
                var isFocus = string.Equals(path, focusTarget, StringComparison.OrdinalIgnoreCase);
                try
                {
                    await OpenCoreAsync(path, focus: isFocus, initialize: isFocus, ct, prefetched: prefetch, recordRecent: false)
                        .ConfigureAwait(false);
                }
                catch { /* skip a repo that no longer opens */ }

                if (isFocus)
                {
                    Historia.Diagnostics.PerfLog.Mark("RestoreSessionAsync focus tab open+focused");
                    try { await Task.Delay(FocusPriorityYield, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }
                }
            }
        }
        finally
        {
            // Any prefetched repo that wasn't consumed (due to cancellation, duplicates, or
            // error) must be disposed so its native libgit2 handles and file watchers don't leak.
            foreach (var unconsumed in prefetches.Values)
            {
                _ = unconsumed.ContinueWith(async t =>
                {
                    if (t.Status == TaskStatus.RanToCompletion && t.Result is { } ctx)
                    {
                        try { await ctx.DisposeAsync().ConfigureAwait(false); }
                        catch { /* best effort cleanup */ }
                    }
                }, TaskScheduler.Default);
            }
        }
    }

    private void PersistSession()
    {
        try
        {
            string[] paths;
            string? focusedPath;
            lock (_sync)
            {
                paths = _open.Select(c => c.RepositoryPath).ToArray();
                focusedPath = _focused?.RepositoryPath;
            }

            var payload = new JsonObject
            {
                ["schemaVersion"] = 2,
                ["paths"] = new JsonArray([.. paths.Select(p => (JsonNode)p!)]),
                ["focusedPath"] = focusedPath,
            };
            SessionWriter.QueueWrite(payload.ToJsonString());
        }
        catch
        {
            // Session persistence is best-effort.
        }
    }

    /// <summary>§12.1 (AF4): force a still-deferred session-restore tab through the ordinary
    /// initialize path, instead of waiting for its first focus — auto-fetch needs every open
    /// repo fully loaded before its loop starts. §V10.1.2: enqueued onto the serial background
    /// pump rather than fired immediately, so a restore does not fan every tab's identity walk +
    /// status refresh out at once. No-op for a tab that is already initialising.</summary>
    public void EnsureInitialized(RepositoryContext context)
    {
        lock (_sync)
        {
            if (!_deferredInit.Remove(context))
                return;
            _bgInitQueue.Add(context);
            if (_bgInitPumpIdle)
            {
                _bgInitPumpIdle = false;
                _ = Task.Run(DrainBackgroundInitQueueAsync);
            }
        }
    }

    /// <summary>Initialises the queued background tabs one at a time, and never before the
    /// focused tab has fully loaded — that tab is what the user is waiting on.</summary>
    private async Task DrainBackgroundInitQueueAsync()
    {
        while (true)
        {
            RepositoryContext? foreground, next;
            Task foregroundChanged;
            lock (_sync)
            {
                if (_bgInitQueue.Count == 0) { _bgInitPumpIdle = true; return; }
                foreground = _foregroundInit;
                next = _bgInitQueue[0];
                foregroundChanged = _foregroundChanged.Task;
            }

            // Let the focused tab finish loading first (its own graph paint + identity +
            // RefreshAllAsync). If it is itself the next queued item, don't wait on it here.
            // Also race against _foregroundChanged — if `foreground` gets interrupted
            // (FocusInternal), its FirstFullLoad will never complete on its own anymore, so
            // without this the pump would wait forever on exactly the tab it just displaced.
            if (foreground is not null && !ReferenceEquals(foreground, next))
            {
                try { await Task.WhenAny(foreground.FirstFullLoad.WaitAsync(foreground.Lifetime), foregroundChanged).ConfigureAwait(false); }
                catch { /* focused tab closed before it finished — carry on */ }
                continue; // re-snapshot: foreground/queue-front may have changed while we waited
            }

            lock (_sync)
            {
                if (_bgInitQueue.Count == 0) { _bgInitPumpIdle = true; return; }
                next = _bgInitQueue[0];
                _bgInitQueue.RemoveAt(0);
                if (!_open.Contains(next))
                    continue; // closed while queued
            }

            try { await InitializeContextAsync(next, foreground: false).ConfigureAwait(false); }
            catch { /* one tab's init failing must not stall the rest */ }
        }
    }

    private void FocusInternal(RepositoryContext context)
    {
        RepositoryContext? old;
        bool needsInit;
        CancellationTokenSource? toCancel = null;
        lock (_sync)
        {
            if (ReferenceEquals(_focused, context))
                return;
            old = _focused;
            _focused = context;

            // Interrupt the outgoing tab's in-flight identity/refresh work, if any, so
            // the tab the user is now looking at doesn't queue behind it. A tab whose FirstFullLoad
            // already completed has nothing to interrupt. The interrupted attempt is requeued at
            // the front of the background pump (ahead of never-started tabs) to resume once
            // priority frees up — RefreshAllAsync is read-only, and identity resolution's only
            // write (seeding git config from the active profile, §9.6) checks the attempt token
            // before starting and then runs uncancelled, so re-running from scratch
            // is fine: either the write never started, or it already completed.
            if (old is not null && !old.FirstFullLoad.IsCompleted && _attemptCts.TryGetValue(old, out var oldCts))
            {
                toCancel = oldCts;
                if (!_bgInitQueue.Contains(old))
                    _bgInitQueue.Insert(0, old);
                if (_bgInitPumpIdle)
                {
                    _bgInitPumpIdle = false;
                    _ = Task.Run(DrainBackgroundInitQueueAsync);
                }
            }

            // Promote the newly focused context to immediate init if it still needs one — a
            // never-started deferred placeholder, a previously interrupted tab sitting in
            // _bgInitQueue, or one the pump is already actively running (relabel it as the
            // foreground target rather than starting a second concurrent attempt against it).
            needsInit = false;
            if (!context.FirstFullLoad.IsCompleted)
            {
                var wasQueued = _deferredInit.Remove(context) | _bgInitQueue.Remove(context);
                if (wasQueued)
                {
                    needsInit = true;
                    SetForegroundInit(context);
                }
                else if (_attemptCts.ContainsKey(context))
                {
                    SetForegroundInit(context); // already running — just becomes the priority tab
                }
            }
        }

        // §I-6.7 / rule 18: never run cancellation callbacks while holding _sync.
        toCancel?.Cancel();

        // Stop the newly focused context's own inactive-tab headless job (queued or
        // running) before its foreground graph work can start — that job and this tab's own
        // background spill both write to the same on-disk store, so the focused tab must never
        // start writing while its headless pass might still be running. Fire-and-forget: the
        // graph service's own writer serializes against it internally regardless.
        if (_services.GetService(typeof(Historia.Services.Graph.IGraphLayoutService))
            is Historia.Services.Graph.IGraphLayoutService graphSvcForFocus)
            _ = graphSvcForFocus.CancelInactiveTabCachingAsync(context);

        // §V3-9.3: a placeholder (or previously interrupted) tab initialises through the ordinary
        // open path on first/renewed focus.
        if (needsInit)
            _ = InitializeContextAsync(context, foreground: true);

        PersistSession(); // §V3-9.3: focus is recorded, not only tab-set changes.
        FocusedRepositoryChanged?.Invoke(this, new FocusedRepositoryChangedEventArgs(old, context));

        // Release the outgoing tab's graph memory and, once the newly focused tab is done loading,
        // hand the freed heap back to the OS.
        if (old is not null)
            _ = HibernateAndReclaimAsync(old, context);
        else
            _ = QueueInitialInactiveTabPipelineAsync(context);
    }

    // An aggressive, blocking, compacting GC.Collect after every tab switch pays a
    // full-heap pause and (LargeObjectHeapCompactionMode.CompactOnce) a LOH compaction even when
    // the outgoing tab held only a few MB, and Aggressive also decommits, so the next tab pays to
    // re-commit. Only worth it once the heap has actually grown enough to matter.
    private const long CompactHeapThresholdBytes = 1L * 1024 * 1024 * 1024; // 1 GiB
    private const long CompactLohThresholdBytes = 256L * 1024 * 1024; // 256 MiB

    /// <summary>Whether the managed heap (or the large-object heap alone) is big
    /// enough to justify the blocking, compacting GC in <see cref="HibernateAndReclaimAsync"/>.
    /// A small heap gains nothing from the pause and just pays the decommit-then-recommit cost on
    /// the next tab switch.</summary>
    internal static bool ShouldCompactAfterHibernate(long heapBytes, long lohBytes) =>
        heapBytes >= CompactHeapThresholdBytes || lohBytes >= CompactLohThresholdBytes;

    /// <summary>Releases everything the outgoing tab's graph held in memory (cancels its
    /// background spill, disposes its live walk, drops its resident commit list). Once
    /// the newly focused tab's background-write gate opens (so the pause never lands inside its
    /// own first paint) and focus has been stable for 2s (so a burst of switches triggers one
    /// collection, not several), runs a compacting GC to actually shrink the working set — below
    /// ~90% memory load the GC prefers background collections that free live objects without
    /// returning the heap size to the OS. Skipped when neither the managed heap nor
    /// the LOH has grown past its threshold — see <see cref="ShouldCompactAfterHibernate"/>.</summary>
    private async Task HibernateAndReclaimAsync(RepositoryContext old, RepositoryContext newContext)
    {
        if (_services.GetService(typeof(Historia.Services.Graph.IGraphLayoutService))
            is Historia.Services.Graph.IGraphLayoutService graphSvc)
        {
            try { await graphSvc.Hibernate(old).ConfigureAwait(false); }
            catch { /* best effort — memory release must never surface as a fatal error */ }
        }

        if (_services.GetService(typeof(IBackgroundWorkGate)) is IBackgroundWorkGate gate)
        {
            try { await gate.WaitUntilOpenAsync(newContext.Lifetime).ConfigureAwait(false); }
            catch { return; } // the new tab closed before it finished loading
        }

        try { await Task.Delay(TimeSpan.FromSeconds(2), newContext.Lifetime).ConfigureAwait(false); }
        catch { return; } // focus moved again (or the tab closed) during the debounce

        lock (_sync)
        {
            if (!ReferenceEquals(_focused, newContext))
                return; // focus moved again right at the debounce boundary
        }

        // Gate the blocking, compacting collection on actual heap growth. Verified
        // against this repo's own PerfLog.Memory (Historia.Core/Diagnostics/PerfLog.cs): .NET's
        // GCMemoryInfo.GenerationInfo is a GCGenerationInfo[] indexed by generation, where index 3
        // is the large object heap (index 4 is the pinned object heap; absent on runtimes too old
        // to report it, hence the length check) and HeapSizeBytes is the managed heap's total size
        // as of the end of the last GC.
        var info = GC.GetGCMemoryInfo();
        var gen = info.GenerationInfo;
        var lohBytes = gen.Length > 3 ? gen[3].SizeAfterBytes : 0L;
        if (!ShouldCompactAfterHibernate(info.HeapSizeBytes, lohBytes))
        {
            static long Mb(long bytes) => bytes / (1024 * 1024);
            Historia.Diagnostics.PerfLog.Mark(
                $"tab-switch GC compaction skipped (heap={Mb(info.HeapSizeBytes)}MB, loh={Mb(lohBytes)}MB)");
        }
        else
        {
            Historia.Diagnostics.PerfLog.Memory("before tab-switch GC compaction");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            sw.Stop();
            Historia.Diagnostics.PerfLog.Memory("after tab-switch GC compaction");
            Historia.Diagnostics.PerfLog.Mark($"tab-switch GC compaction: {sw.Elapsed.TotalMilliseconds:F1}ms");
        }

        // Once the active tab is settled (gate open, focus stable, its own reclaim done),
        // queue every OTHER open tab for the headless commit-graph + disk-cache pipeline.
        // GraphBackgroundWriter runs them one at a time, behind the same gate, so this never
        // contends with the active tab's own work.
        _ = RunInactiveTabPipelineAsync(newContext);
    }

    /// <summary>Queues every open tab except <paramref name="activeContext"/> for
    /// <c>IGraphLayoutService.RunHeadlessCacheAsync</c>. Waits for the background-init pump to be
    /// idle first — a session restore's own identity/status work takes priority over this purely
    /// optional caching. A job is stopped both by its own tab closing
    /// (<c>RepositoryContext.Lifetime</c>) AND by that tab gaining focus
    /// (<see cref="FocusInternal"/> calls <c>IGraphLayoutService.CancelInactiveTabCachingAsync</c>
    /// for the newly focused context before its own foreground spill can start) — the headless
    /// pipeline and a focused tab's own background spill both write to the same on-disk store, so
    /// letting a headless pass for the tab the user just switched to keep running would race it.
    /// <c>GraphMetaWriter.AppendSpillBatch</c>/<c>TryCommitReorder</c> re-check the on-disk header
    /// under a per-repo lock as a second line of defence regardless.</summary>
    private async Task RunInactiveTabPipelineAsync(RepositoryContext activeContext)
    {
        try
        {
            if (_services.GetService(typeof(Historia.Services.Graph.IGraphLayoutService))
                is not Historia.Services.Graph.IGraphLayoutService graphSvc)
                return;

            if (!await WaitForBackgroundInitIdleAsync(activeContext.Lifetime).ConfigureAwait(false))
                return;

            List<RepositoryContext> targets;
            lock (_sync)
            {
                if (!ReferenceEquals(_focused, activeContext))
                    return; // focus moved again while this waited
                targets = [.. _open.Where(c => !ReferenceEquals(c, activeContext))];
            }

            foreach (var context in targets)
                graphSvc.EnqueueInactiveTabCaching(context, TabGraphQuery);
        }
        catch (OperationCanceledException)
        {
            // The active tab closed while this waited — nothing left to queue for.
        }
        catch (Exception ex)
        {
            // Headless caching is a pure optimisation; never let it surface as a fatal error.
            Historia.Diagnostics.PerfLog.Mark($"RunInactiveTabPipelineAsync failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Cold-launch path: the first focus has no outgoing tab to hibernate, but the
    /// inactive tabs still need their headless disk-cache + commit-graph pipeline. Waits for
    /// the same preconditions <see cref="HibernateAndReclaimAsync"/> does (gate open, bg-init
    /// idle, focus stable) — minus the GC compaction (nothing to reclaim on a fresh start)
    /// and minus the 2s debounce (no focus churn to absorb on the initial tab).</summary>
    private async Task QueueInitialInactiveTabPipelineAsync(RepositoryContext context)
    {
        try
        {
            if (_services.GetService(typeof(IBackgroundWorkGate)) is IBackgroundWorkGate gate)
            {
                try { await gate.WaitUntilOpenAsync(context.Lifetime).ConfigureAwait(false); }
                catch { return; }
            }

            if (!await WaitForBackgroundInitIdleAsync(context.Lifetime).ConfigureAwait(false))
                return;

            lock (_sync)
            {
                if (!ReferenceEquals(_focused, context))
                    return;
            }

            _ = RunInactiveTabPipelineAsync(context);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Historia.Diagnostics.PerfLog.Mark(
                $"QueueInitialInactiveTabPipelineAsync failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Waits until <see cref="_bgInitPumpIdle"/> is true, <paramref name="ct"/> is
    /// cancelled, or <see cref="BackgroundInitIdleWaitCap"/> elapses. Polling rather than a
    /// signal: this is a low-urgency background optimisation, not on any user-visible
    /// latency path, so a short delay loop is simpler and no less correct than threading a new
    /// completion source through every one of the pump's several mutation points. Capped rather
    /// than unconditional: this is a best-effort caching pass, not a correctness dependency, and
    /// must never wait forever on an internal signal — the next focus change tries again.</summary>
    private async Task<bool> WaitForBackgroundInitIdleAsync(CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + BackgroundInitIdleWaitCap;
        while (true)
        {
            bool idle;
            lock (_sync) idle = _bgInitPumpIdle;
            if (idle)
                return true;
            if (DateTime.UtcNow >= deadline)
                return false;
            try { await Task.Delay(250, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return false; }
        }
    }

    private static readonly TimeSpan BackgroundInitIdleWaitCap = TimeSpan.FromSeconds(30);

    private void RaiseOpenChanged()
    {
        PersistSession();
        OpenRepositoriesChanged?.Invoke(this, new OpenRepositoriesChangedEventArgs(OpenRepositories));
    }

    // Schemes Historia will pass to `git clone`. Deliberately excludes any custom transport —
    // notably "ext::", a Git remote-helper syntax that runs an arbitrary shell command — and any
    // scheme this list doesn't recognise.
    private static readonly HashSet<string> AllowedCloneSchemes =
        new(StringComparer.OrdinalIgnoreCase) { "http", "https", "ssh", "git", "file" };

    /// <summary>Rejects a pasted clone URL that would let Git parse it as something other than a
    /// repository address — a leading '-' (option injection) or a "&lt;transport&gt;::" prefix
    /// (an arbitrary Git remote helper, e.g. "ext::sh -c ..."). Local paths, and the
    /// http/https/ssh/git/file schemes, are otherwise unrestricted.</summary>
    private static void ValidateCloneUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            throw new HistoriaGitException(GitErrorKind.Unknown, "A clone URL is required.");

        var trimmed = url.Trim();

        if (trimmed.StartsWith('-'))
            throw new HistoriaGitException(GitErrorKind.Unknown, "The clone URL cannot start with '-'.");

        if (trimmed.Contains("::", StringComparison.Ordinal))
            throw new HistoriaGitException(GitErrorKind.Unknown,
                "This URL uses a Git remote-helper transport, which Historia does not allow for Clone.");

        var schemeEnd = trimmed.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd >= 0 && !AllowedCloneSchemes.Contains(trimmed[..schemeEnd]))
            throw new HistoriaGitException(GitErrorKind.Unknown, $"Unsupported clone URL scheme '{trimmed[..schemeEnd]}'.");
    }

    private static string NormalizeDir(string path)
    {
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); }
        catch { return path; }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort.
        }
    }

    public async ValueTask DisposeAsync()
    {
        Historia.Diagnostics.PerfLog.Mark("WorkspaceService.DisposeAsync start");

        if (_sessionWriter is not null)
            await _sessionWriter.DisposeAsync().ConfigureAwait(false);
        Historia.Diagnostics.PerfLog.Mark("WorkspaceService.DisposeAsync session writer flushed");

        List<RepositoryContext> toDispose;
        lock (_sync)
        {
            toDispose = _open.ToList();
            _open.Clear();
            _focused = null;
        }

        foreach (var context in toDispose)
            await context.DisposeAsync().ConfigureAwait(false);
        Historia.Diagnostics.PerfLog.Mark($"WorkspaceService.DisposeAsync done ({toDispose.Count} repos)");
    }
}
