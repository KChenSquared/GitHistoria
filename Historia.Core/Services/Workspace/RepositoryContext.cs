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
    public CancellationToken Lifetime => _lifetimeCts.Token;

    public IGitGateway Gateway { get; }
    public IRepositoryStateService State { get; }
    internal FileWatcherService Watcher { get; }

    /// <summary>
    /// Stable repo identity (§9.3.1), resolved once per open by <c>IRepoIdentityService</c>
    /// and cached here — it walks history, so it is never recomputed per status refresh.
    /// </summary>
    public RepoIdentityKey? IdentityKey { get; set; }

    /// <summary>The profile resolved for this tab (§9.4). Null in zero-profile mode.</summary>
    public GitProfile? ActiveProfile => State.ActiveProfile;

    private RepositoryContext(Repository repository, IServiceProvider services)
    {
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

    /// <summary>
    /// Serializes access to the owned <see cref="Repository"/> and marshals the work to the
    /// thread pool (§6.3.1). Handlers must project into Historia's own records <b>inside</b> the
    /// callback and return only those — no <see cref="Commit"/>, <see cref="Tree"/>,
    /// <see cref="Blob"/>, <see cref="Patch"/>, or <see cref="Branch"/> may escape.
    /// </summary>
    public async Task<T> WithRepositoryAsync<T>(Func<Repository, T> work, CancellationToken ct = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetimeCts.Token);
        var waitStart = System.Diagnostics.Stopwatch.GetTimestamp();
        await _repoGate.WaitAsync(linked.Token).ConfigureAwait(false);
        var waitMs = System.Diagnostics.Stopwatch.GetElapsedTime(waitStart).TotalMilliseconds;
        if (waitMs > 5)
            Historia.Diagnostics.PerfLog.Mark($"repo gate wait {waitMs:F1}ms ({System.IO.Path.GetFileName(RepositoryPath)})");
        try
        {
            return await Task.Run(() => work(_repository), linked.Token).ConfigureAwait(false);
        }
        finally
        {
            _repoGate.Release();
        }
    }

    public Task WithRepositoryAsync(Action<Repository> work, CancellationToken ct = default)
        => WithRepositoryAsync(repo => { work(repo); return true; }, ct);

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
        using var _ = Historia.Diagnostics.PerfLog.Scope($"RepositoryContext.DisposeAsync({System.IO.Path.GetFileName(RepositoryPath)})");
        AppLog.Write(AppLogLevel.Info, "repo", $"Close({DisplayName})");
        try { _lifetimeCts.Cancel(); } catch { /* ignore */ }

        await Watcher.DisposeAsync().ConfigureAwait(false);
        if (State is IAsyncDisposable stateDisposable)
            await stateDisposable.DisposeAsync().ConfigureAwait(false);

        // Take the gate so no operation is mid-flight when the native handle is freed. A
        // libgit2 call already running can't be interrupted, so bound the wait: if it does not
        // finish, abandon the native handle to the finalizer rather than hang the tab close.
        // The still-running operation keeps the semaphore / CTS alive and releases them itself.
        var gateWaitStart = System.Diagnostics.Stopwatch.GetTimestamp();
        var gotGate = await _repoGate.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        Historia.Diagnostics.PerfLog.Mark(
            $"DisposeAsync repo gate {(gotGate ? "acquired" : "TIMED OUT")} after {System.Diagnostics.Stopwatch.GetElapsedTime(gateWaitStart).TotalMilliseconds:F1}ms ({System.IO.Path.GetFileName(RepositoryPath)})");
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
            _lifetimeCts.Dispose();
        }
    }
}
