namespace Historia.Services.Workspace;

/// <summary>
/// Per-repository-tab handle (§6.3, §6.6.3). Owns exactly one <see cref="Repository"/> and
/// serializes every access through a single-slot async gate (§6.3.1) — no LibGit2Sharp object
/// may escape <see cref="WithRepositoryAsync{T}"/>. Also owns the reactive state service, the
/// gateway, the file watcher, and a lifetime cancellation scope.
/// </summary>
public sealed class RepositoryContext : IAsyncDisposable
{
    private readonly SemaphoreSlim _repoGate = new(1, 1);
    private readonly Repository _repository;
    private readonly CancellationTokenSource _lifetimeCts = new();
    private int _selfOperationDepth;

    // Count of callers currently blocked in (or about to enter) the foreground gate queue.
    // WithRepositoryBackgroundAsync stands aside while this is non-zero so a user-driven walk,
    // status refresh, or diff never queues behind the §7.6 background history spill.
    private int _foregroundGateWaiters;

    // Count of callers currently blocked in (or about to enter) WithRepositoryBackgroundAsync.
    // WithRepositorySpillAsync stands aside while this is non-zero, in addition to standing
    // aside for genuine foreground work — see its doc comment for why a third tier exists.
    private int _backgroundGateWaiters;

    /// <summary>Longest a <see cref="WithRepositoryBackgroundAsync{T}"/> caller will keep yielding
    /// to foreground work before it forces its own turn — bounds background-spill starvation when
    /// the user is continuously interacting with the tab.</summary>
    private static readonly TimeSpan BackgroundGateMaxYield = TimeSpan.FromSeconds(3);

    // Signaled once the graph control's first paint for this context lands (success, failure,
    // or cancellation -- whichever comes first). WorkspaceService's InitializeContextAsync waits
    // on this (bounded by its own timeout) before it starts touching the repo itself, so the
    // graph's first walk -- what the user is actually staring at during cold open -- gets the
    // single-access gate uncontended instead of queueing behind identity/branches/status work.
    private readonly TaskCompletionSource _firstGraphPaint =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes once this context's first graph paint (or its failure) lands. Never
    /// faults or gets stuck on its own -- a caller with no graph control ever attaching (a
    /// headless open, a test) must apply its own timeout rather than await this alone.</summary>
    public Task FirstGraphPaint => _firstGraphPaint.Task;

    /// <summary>Idempotent -- safe to call once per attempt or not at all.</summary>
    public void MarkFirstGraphPaintDone() => _firstGraphPaint.TrySetResult();

    // §12.1: signalled once this tab is "fully loaded" -- identity resolved and the first full
    // state refresh (branches, graph, changes) has completed. IAutoFetchService waits on this
    // before its [InitialFetch] / [AutoFetch] loop touches the repo. Like FirstGraphPaint it
    // never faults; a headless open completes it as soon as InitializeContextAsync runs.
    private readonly TaskCompletionSource _firstFullLoad =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes once this context has fully loaded (§12.1). Never faults.</summary>
    public Task FirstFullLoad => _firstFullLoad.Task;

    /// <summary>Idempotent. Called by <c>WorkspaceService.InitializeContextAsync</c> when the
    /// first refresh settles (or the tab closes first).</summary>
    public void MarkFirstFullLoadDone() => _firstFullLoad.TrySetResult();

    public string Id { get; } = Guid.NewGuid().ToString("N");

    /// <summary>Working directory, or the git directory for a bare repo.</summary>
    public string RepositoryPath { get; }
    public string GitDirectory { get; }
    public bool IsBare { get; }
    public string DisplayName { get; }

    /// <summary>Cancelled when the tab closes; every in-flight operation observes it.</summary>
    public CancellationToken Lifetime { get; }

    public IGitGateway Gateway { get; }
    public IRepositoryStateService State { get; }
    internal FileWatcherService Watcher { get; }
    internal IWorkspaceService? Workspace { get; set; }

    /// <summary>
    /// Stable repo identity (§9.3.1), resolved once per open by <c>IRepoIdentityService</c>
    /// and cached here — it walks history, so it is never recomputed per status refresh.
    /// </summary>
    public RepoIdentityKey? IdentityKey { get; set; }

    /// <summary>The profile resolved for this tab (§9.4). Null in zero-profile mode.</summary>
    public GitProfile? ActiveProfile => State.ActiveProfile;

    private RepositoryContext(Repository repository, IServiceProvider services)
    {
        Lifetime = _lifetimeCts.Token;
        _repository = repository;
        IsBare = repository.Info.IsBare;
        GitDirectory = repository.Info.Path;
        RepositoryPath = repository.Info.WorkingDirectory ?? repository.Info.Path;
        DisplayName = DeriveDisplayName(RepositoryPath, GitDirectory);

        Gateway = new LoggingGitGateway(
            new GitGateway(
                this,
                services.GetRequiredService<LibGit2Service>(),
                services.GetRequiredService<IGitCliService>(),
                services.GetRequiredService<Historia.Services.Settings.IRepoLayoutService>(),
                services.GetRequiredService<Historia.Services.Profiles.ICredentialVaultService>()),
            RepositoryPath);

        State = new RepositoryStateService(
            this,
            services.GetService<IUiDispatcher>(),
            services.GetService<IRepoStateCache>());
        State.SeedFromCache(); // paint the sidebar from last-known state before the first refresh

        Workspace = services.GetService<IWorkspaceService>();

        Watcher = new FileWatcherService(
            this,
            State,
            services.GetRequiredService<ISettingsService>());
    }

    /// <summary>
    /// Opens a repository discovered from <paramref name="path"/>. Runs off the UI thread
    /// (<see cref="Repository"/> construction is I/O, §6.2.2). Throws
    /// <see cref="RepositoryNotFoundException"/> when no repository is found.
    /// </summary>
    public static RepositoryContext Open(string path, IServiceProvider services)
    {
        using var _ = Historia.Diagnostics.PerfLog.Scope($"RepositoryContext.Open({Path.GetFileName(path)})");
        using var _log = AppLog.Operation("repo", $"Open({Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))})");
        var discovered = Repository.Discover(path)
            ?? throw new RepositoryNotFoundException($"No Git repository found at or above '{path}'.");
        var repo = new Repository(discovered);
        try
        {
            var ctx = new RepositoryContext(repo, services);
            _log.Outcome(ctx.DisplayName);
            return ctx;
        }
        catch (Exception ex)
        {
            _log.Fail(ex);
            repo.Dispose();
            throw;
        }
    }

    private const double GateLogThresholdMs = 5.0;

    private volatile string? _activeGateHolder;
    private volatile string? _lastGateHolder;

    internal static long FormattedGateMarksCount;

    private static string ResolveCallerLabel(string? label, string callerMember, string callerFile)
    {
        if (!string.IsNullOrEmpty(label))
            return label;
        var fileName = string.IsNullOrEmpty(callerFile) ? "" : Path.GetFileNameWithoutExtension(callerFile);
        if (!string.IsNullOrEmpty(fileName) && !string.IsNullOrEmpty(callerMember))
            return $"{fileName}.{callerMember}";
        if (!string.IsNullOrEmpty(callerMember))
            return callerMember;
        return "unknown";
    }

    /// <summary>
    /// Serializes access to the owned <see cref="Repository"/> and marshals the work to the
    /// thread pool (§6.3.1). Handlers must project into Historia's own records <b>inside</b> the
    /// callback and return only those — no <see cref="Commit"/>, <see cref="Tree"/>,
    /// <see cref="Blob"/>, <see cref="Patch"/>, or <see cref="Branch"/> may escape.
    /// </summary>
    public async Task<T> WithRepositoryAsync<T>(
        Func<Repository, T> work,
        CancellationToken ct = default,
        string? label = null,
        [CallerMemberName] string callerMember = "",
        [CallerFilePath] string callerFile = "")
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, Lifetime);
        var waitStart = System.Diagnostics.Stopwatch.GetTimestamp();
        Interlocked.Increment(ref _foregroundGateWaiters);
        try
        {
            await _repoGate.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _foregroundGateWaiters);
        }
        var waitMs = System.Diagnostics.Stopwatch.GetElapsedTime(waitStart).TotalMilliseconds;
        var waitedBehind = _lastGateHolder ?? _activeGateHolder ?? "unknown";
        if (waitMs > GateLogThresholdMs && PerfLog.Enabled)
        {
            Interlocked.Increment(ref FormattedGateMarksCount);
            PerfLog.Mark($"repo gate wait {waitMs:F1}ms ({DisplayName}) behind {waitedBehind}");
        }

        var resolvedLabel = ResolveCallerLabel(label, callerMember, callerFile);
        var holderDesc = PerfLog.Enabled ? $"[foreground] {resolvedLabel}" : null;
        if (holderDesc is not null)
            _activeGateHolder = holderDesc;

        var holdStart = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            return await Task.Run(() => work(_repository), linked.Token).ConfigureAwait(false);
        }
        finally
        {
            var holdMs = System.Diagnostics.Stopwatch.GetElapsedTime(holdStart).TotalMilliseconds;
            if (holdMs > GateLogThresholdMs && PerfLog.Enabled)
            {
                Interlocked.Increment(ref FormattedGateMarksCount);
                PerfLog.Mark($"repo gate hold [foreground] {resolvedLabel} {holdMs:F1}ms ({DisplayName})");
            }
            if (holderDesc is not null)
            {
                _lastGateHolder = holderDesc;
                _activeGateHolder = null;
            }
            _repoGate.Release();
        }
    }

    public Task WithRepositoryAsync(
        Action<Repository> work,
        CancellationToken ct = default,
        string? label = null,
        [CallerMemberName] string callerMember = "",
        [CallerFilePath] string callerFile = "")
        => WithRepositoryAsync(repo => { work(repo); return true; }, ct, label, callerMember, callerFile);

    /// <summary>
    /// Like <see cref="WithRepositoryAsync{T}"/> but low priority: yields the gate to any
    /// foreground caller that is waiting (or arrives while this one is waiting), up to
    /// <see cref="BackgroundGateMaxYield"/>, before taking its own turn. Used by the §7.6
    /// background history spill so it never makes the user wait for a tab switch, scroll,
    /// status refresh, or diff on the same repo. Callers should still slice their work so one
    /// acquisition is short — this only reorders the queue, it cannot preempt a turn in flight.
    /// </summary>
    public async Task<T> WithRepositoryBackgroundAsync<T>(
        Func<Repository, T> work,
        CancellationToken ct = default,
        string? label = null,
        [CallerMemberName] string callerMember = "",
        [CallerFilePath] string callerFile = "")
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, Lifetime);
        var yieldStart = System.Diagnostics.Stopwatch.GetTimestamp();
        Interlocked.Increment(ref _backgroundGateWaiters);
        try
        {
            while (true)
            {
                while (Volatile.Read(ref _foregroundGateWaiters) > 0
                       && System.Diagnostics.Stopwatch.GetElapsedTime(yieldStart) < BackgroundGateMaxYield)
                    await Task.Delay(20, linked.Token).ConfigureAwait(false);

                await _repoGate.WaitAsync(linked.Token).ConfigureAwait(false);

                // A foreground caller queued between the check above and the acquire — hand it the
                // turn, unless we have already yielded for as long as we are willing to.
                if (Volatile.Read(ref _foregroundGateWaiters) > 0
                    && System.Diagnostics.Stopwatch.GetElapsedTime(yieldStart) < BackgroundGateMaxYield)
                {
                    _repoGate.Release();
                    await Task.Delay(20, linked.Token).ConfigureAwait(false);
                    continue;
                }
                break;
            }
        }
        finally
        {
            Interlocked.Decrement(ref _backgroundGateWaiters);
        }

        var waitMs = System.Diagnostics.Stopwatch.GetElapsedTime(yieldStart).TotalMilliseconds;
        var waitedBehind = _lastGateHolder ?? _activeGateHolder ?? "unknown";
        if (waitMs > GateLogThresholdMs && PerfLog.Enabled)
        {
            Interlocked.Increment(ref FormattedGateMarksCount);
            PerfLog.Mark($"repo gate wait {waitMs:F1}ms ({DisplayName}) behind {waitedBehind}");
        }

        var resolvedLabel = ResolveCallerLabel(label, callerMember, callerFile);
        var holderDesc = PerfLog.Enabled ? $"[background] {resolvedLabel}" : null;
        if (holderDesc is not null)
            _activeGateHolder = holderDesc;

        var holdStart = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            return await Task.Run(() => work(_repository), linked.Token).ConfigureAwait(false);
        }
        finally
        {
            var holdMs = System.Diagnostics.Stopwatch.GetElapsedTime(holdStart).TotalMilliseconds;
            if (holdMs > GateLogThresholdMs && PerfLog.Enabled)
            {
                Interlocked.Increment(ref FormattedGateMarksCount);
                PerfLog.Mark($"repo gate hold [background] {resolvedLabel} {holdMs:F1}ms ({DisplayName})");
            }
            if (holderDesc is not null)
            {
                _lastGateHolder = holderDesc;
                _activeGateHolder = null;
            }
            _repoGate.Release();
        }
    }

    public Task WithRepositoryBackgroundAsync(
        Action<Repository> work,
        CancellationToken ct = default,
        string? label = null,
        [CallerMemberName] string callerMember = "",
        [CallerFilePath] string callerFile = "")
        => WithRepositoryBackgroundAsync(repo => { work(repo); return true; }, ct, label, callerMember, callerFile);

    /// <summary>
    /// Like <see cref="WithRepositoryBackgroundAsync{T}"/> but lower priority still: yields to a
    /// foreground caller AND to a <see cref="WithRepositoryBackgroundAsync{T}"/> caller, up to
    /// <see cref="BackgroundGateMaxYield"/>, before taking its own turn. Used by the §7.6
    /// full-history disk spill, which otherwise sits at the same tier as (and can starve) the
    /// commit-graph's own silent crawl (also a <see cref="WithRepositoryBackgroundAsync{T}"/>
    /// caller) — both count as "background" to a genuine foreground request, but the spill has no
    /// user-visible urgency of its own and the crawl does (it is what grows the scrollbar while
    /// the graph loads in the background), so the spill is the one that must stand aside here.
    /// </summary>
    public async Task<T> WithRepositorySpillAsync<T>(
        Func<Repository, T> work,
        CancellationToken ct = default,
        string? label = null,
        [CallerMemberName] string callerMember = "",
        [CallerFilePath] string callerFile = "")
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, Lifetime);
        var yieldStart = System.Diagnostics.Stopwatch.GetTimestamp();
        while (true)
        {
            while ((Volatile.Read(ref _foregroundGateWaiters) > 0 || Volatile.Read(ref _backgroundGateWaiters) > 0)
                   && System.Diagnostics.Stopwatch.GetElapsedTime(yieldStart) < BackgroundGateMaxYield)
                await Task.Delay(20, linked.Token).ConfigureAwait(false);

            await _repoGate.WaitAsync(linked.Token).ConfigureAwait(false);

            if ((Volatile.Read(ref _foregroundGateWaiters) > 0 || Volatile.Read(ref _backgroundGateWaiters) > 0)
                && System.Diagnostics.Stopwatch.GetElapsedTime(yieldStart) < BackgroundGateMaxYield)
            {
                _repoGate.Release();
                await Task.Delay(20, linked.Token).ConfigureAwait(false);
                continue;
            }
            break;
        }

        var waitMs = System.Diagnostics.Stopwatch.GetElapsedTime(yieldStart).TotalMilliseconds;
        var waitedBehind = _lastGateHolder ?? _activeGateHolder ?? "unknown";
        if (waitMs > GateLogThresholdMs && PerfLog.Enabled)
        {
            Interlocked.Increment(ref FormattedGateMarksCount);
            PerfLog.Mark($"repo gate wait {waitMs:F1}ms ({DisplayName}) behind {waitedBehind}");
        }

        var resolvedLabel = ResolveCallerLabel(label, callerMember, callerFile);
        var holderDesc = PerfLog.Enabled ? $"[spill] {resolvedLabel}" : null;
        if (holderDesc is not null)
            _activeGateHolder = holderDesc;

        var holdStart = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            return await Task.Run(() => work(_repository), linked.Token).ConfigureAwait(false);
        }
        finally
        {
            var holdMs = System.Diagnostics.Stopwatch.GetElapsedTime(holdStart).TotalMilliseconds;
            if (holdMs > GateLogThresholdMs && PerfLog.Enabled)
            {
                Interlocked.Increment(ref FormattedGateMarksCount);
                PerfLog.Mark($"repo gate hold [spill] {resolvedLabel} {holdMs:F1}ms ({DisplayName})");
            }
            if (holderDesc is not null)
            {
                _lastGateHolder = holderDesc;
                _activeGateHolder = null;
            }
            _repoGate.Release();
        }
    }

    public Task WithRepositorySpillAsync(
        Action<Repository> work,
        CancellationToken ct = default,
        string? label = null,
        [CallerMemberName] string callerMember = "",
        [CallerFilePath] string callerFile = "")
        => WithRepositorySpillAsync(repo => { work(repo); return true; }, ct, label, callerMember, callerFile);

    /// <summary>
    /// Marks a scope as one of Historia's own Git operations so the watcher suppresses the
    /// self-triggered refresh storm and issues one explicit refresh on completion (§6.8.2).
    /// </summary>
    public IDisposable BeginSelfOperation()
    {
        Interlocked.Increment(ref _selfOperationDepth);
        return new SelfOperationScope(this);
    }

    public bool IsSelfOperationInProgress => Volatile.Read(ref _selfOperationDepth) > 0;

    private sealed class SelfOperationScope(RepositoryContext owner) : IDisposable
    {
        public void Dispose()
        {
            if (Interlocked.Decrement(ref owner._selfOperationDepth) == 0)
                _ = RefreshQuietlyAsync(owner);
        }

        // Fire-and-forget, so a fault here must be observed and swallowed here — otherwise it
        // surfaces later as an unobserved TaskScheduler exception, which App.axaml.cs reports to
        // the user as a fatal error for what can be a routine tab close mid-operation.
        private static async Task RefreshQuietlyAsync(RepositoryContext owner)
        {
            try
            {
                await owner.State.RefreshAllAsync(owner.Lifetime).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Tab closed during the operation.
            }
            catch
            {
                // A self-operation's trailing refresh failing must not surface as a fatal error.
            }
        }
    }

    private static string DeriveDisplayName(string workingDir, string gitDir)
    {
        var basis = workingDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.IsNullOrEmpty(basis))
            basis = gitDir;
        var name = Path.GetFileName(basis);
        return string.IsNullOrEmpty(name) ? basis : name;
    }

    public async ValueTask DisposeAsync()
    {
        using var perfScope = Historia.Diagnostics.PerfLog.Scope($"RepositoryContext.DisposeAsync({DisplayName})");
        AppLog.Write(AppLogLevel.Info, "repo", $"Close({DisplayName})");
        try { _lifetimeCts.Cancel(); } catch { /* ignore */ }

        await Watcher.DisposeAsync().ConfigureAwait(false);
        if (State is IAsyncDisposable stateDisposable)
            await stateDisposable.DisposeAsync().ConfigureAwait(false);

        // Take the gate so no operation is mid-flight when the native handle is freed. A
        // libgit2 call already running can't be interrupted, so bound the wait: if it does not
        // finish, return without hanging the tab close — but still reclaim the gate, the CTS,
        // and the native handle once that operation eventually does finish, via
        // DisposeWhenGateFreesAsync below, rather than leaking both for the rest of the
        // process (§findings: WithRepositoryAsync's finally only releases the semaphore;
        // nothing here ever disposed it or the Repository on this path).
        //
        // Note: _lifetimeCts is intentionally NOT disposed here. A parameterless
        // CancellationTokenSource holds no unmanaged resources or timers. Calling Cancel()
        // above gracefully signals all in-flight and trailing operations to cancel; calling
        // Dispose() would cause concurrent operations still referencing or linking against
        // Lifetime to throw ObjectDisposedException instead of observing cancellation.
        var gateWaitStart = System.Diagnostics.Stopwatch.GetTimestamp();
        var gotGate = await _repoGate.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        Historia.Diagnostics.PerfLog.Mark(
            $"DisposeAsync repo gate {(gotGate ? "acquired" : "TIMED OUT")} after {System.Diagnostics.Stopwatch.GetElapsedTime(gateWaitStart).TotalMilliseconds:F1}ms ({DisplayName})");
        if (gotGate)
        {
            try
            {
                _repository.Dispose();
            }
            finally
            {
                _repoGate.Release();
            }

            _repoGate.Dispose();
        }
        else
        {
            _ = DisposeWhenGateFreesAsync();
        }
    }

    /// <summary>Fire-and-forget tail for the DisposeAsync timeout path: waits — however long it
    /// takes — for the stuck operation to release the gate, then disposes the native handle and
    /// the semaphore. Never awaited by a caller, so any fault here must be swallowed rather than
    /// becoming an unobserved <see cref="TaskScheduler"/> exception.</summary>
    private async Task DisposeWhenGateFreesAsync()
    {
        try
        {
            await _repoGate.WaitAsync().ConfigureAwait(false);
            try { _repository.Dispose(); }
            finally { _repoGate.Release(); }
            _repoGate.Dispose();
        }
        catch
        {
            // Best-effort — there is no caller left to report a failure to by the time this runs.
        }
    }
}
