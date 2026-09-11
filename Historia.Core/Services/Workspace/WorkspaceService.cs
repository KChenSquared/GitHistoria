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
    private AtomicFileWriter? _sessionWriter;
    private AtomicFileWriter SessionWriter => _sessionWriter ??= new AtomicFileWriter(AppPaths.SessionFile);
    private string? _restoredFocusPath;

    // v8.2: after focusing the restored session's active repo, RestoreSessionAsync pauses
    // briefly before opening the rest of the session's tabs -- see RestoreSessionAsync for why.
    private static readonly TimeSpan FocusPriorityYield = TimeSpan.FromMilliseconds(60);

    // v8.2: a background (force-initialised, no graph control attached) tab waits at most this
    // long for a first-paint signal that will never come before its identity/refresh runs.
    private static readonly TimeSpan GraphPriorityTimeout = TimeSpan.FromSeconds(8);

    // §V10.1.2: the FOCUSED tab's graph control WILL paint — wait for that, not a short guess,
    // before its identity resolution + RefreshAllAsync take the repo gate. 8s was far too short
    // on a big repo (linux first paint alone is tens of seconds), so identity/refresh jumped the
    // gate mid-first-walk and stalled it for ~60s. This ceiling is only a stuck-guard.
    private static readonly TimeSpan GraphFirstPaintCeiling = TimeSpan.FromSeconds(120);

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
            lock (_sync) _foregroundInit = context;
            _ = InitializeContextAsync(context, foreground: true);
        }

        return context;
    }

    /// <summary>
    /// Resolves the repo identity key and the cascading active profile (§9.3.1, §9.4), then
    /// performs the first full state refresh. Runs off the open path so a large repo does not
    /// hold up the tab appearing.
    /// </summary>
    private async Task InitializeContextAsync(RepositoryContext context, bool foreground)
    {
        try
        {
            await InitializeContextCoreAsync(context, foreground).ConfigureAwait(false);
        }
        finally
        {
            if (foreground)
                lock (_sync)
                    if (ReferenceEquals(_foregroundInit, context))
                        _foregroundInit = null;
        }
    }

    private async Task InitializeContextCoreAsync(RepositoryContext context, bool foreground)
    {
        // Let the graph control's own first walk for this context claim the repo's single-access
        // gate uncontended. Without this, identity resolution below (and RefreshAllAsync after
        // it) starts within a few hundred ms of the tab opening and can hold that gate for
        // seconds on a large repo -- observed: 3+s of the graph's fingerprint+load work sitting
        // in the queue behind it, on a repo where the graph's own paint takes barely over a
        // second once it actually gets a turn. §V10.1.2: the focused tab's graph WILL paint, so
        // wait for that (large stuck-guard ceiling); a background force-init has no graph
        // attached, so only the short ceiling applies. Task.WhenAny never throws or propagates
        // either branch's outcome, so no try/catch is needed here -- a cancelled Lifetime is
        // caught by the identity-resolution try/catch right below instead.
        var ceiling = foreground ? GraphFirstPaintCeiling : GraphPriorityTimeout;
        await Task.WhenAny(context.FirstGraphPaint, Task.Delay(ceiling, context.Lifetime)).ConfigureAwait(false);

        try
        {
            var key = await _identity.ResolveAsync(context, context.Lifetime).ConfigureAwait(false);

            // Keep the binding's last-known path fresh — the rename case working as intended (§9.3.1).
            var existingBinding = _profiles.GetProfileForRepository(key);
            if (existingBinding is not null)
                await _profiles.BindProfileToRepositoryAsync(key, context.RepositoryPath, existingBinding.Id, context.Lifetime).ConfigureAwait(false);

            var profile = _profiles.ResolveActiveProfile(key);
            context.State.SetActiveProfile(profile);
            if (profile is not null)
            {
                await _profiles.TouchProfileAsync(profile.Id, context.Lifetime).ConfigureAwait(false);

                // §9.6: when the repo has no existing user.name/user.email, write the profile's
                // values. A mismatch with existing values is surfaced by the repo view as a
                // non-blocking banner rather than being overwritten here.
                if (!context.IsBare)
                {
                    var (name, email) = await context.Gateway.GetConfiguredIdentityAsync(context.Lifetime).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(email))
                        await _profiles.ApplyProfileToGitConfigAsync(profile, context.RepositoryPath, GitConfigScope.Local, context.Lifetime).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            context.MarkFirstFullLoadDone(); // §12.1: unblock any auto-fetch waiter on close
            return;
        }
        catch
        {
            // Identity/profile resolution failure must not stop the repo from opening.
        }

        try
        {
            await context.State.RefreshAllAsync(context.Lifetime).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Tab closed during first refresh.
        }
        finally
        {
            context.MarkFirstFullLoadDone(); // §12.1
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
        // repository address (security review 2026-09-07, finding 3).
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

        return await OpenAsync(targetDirectory, ct).ConfigureAwait(false);
    }

    public async Task CloseAsync(RepositoryContext context)
    {
        bool wasFocused;
        RepositoryContext? nextFocus = null;
        lock (_sync)
        {
            _open.Remove(context);
            _deferredInit.Remove(context);
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
        // ends in one, and GetFileName returns "" for a path ending in a separator — this used
        // to make a perfectly-resolved focusTarget print as empty here and look like a resolution
        // bug when it wasn't one.
        var focusTargetName = focusTarget is null
            ? "(none)"
            : System.IO.Path.GetFileName(focusTarget.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));
        Historia.Diagnostics.PerfLog.Mark($"RestoreSessionAsync focusTarget resolved ({paths.Count} paths, focus={focusTargetName})");

        // v8.2: kick off every existing path's RepositoryContext.Open concurrently right away,
        // instead of opening them one at a time as the loop below reaches each in its turn --
        // that used to make the focused tab's own open sit behind every earlier tab's own
        // (redundant, purely sequential) disk read, delaying the start of the one graph walk the
        // user is actually waiting to see. The loop still adds tabs to _open (and focuses/
        // initializes the right one) strictly in saved-session order -- reordering `paths` itself
        // previously shuffled the tab strip on every restore, so this leaves that order intact;
        // it just awaits work that's already in flight instead of starting it fresh at each
        // iteration. Once the focused tab is open, the loop also pauses briefly before opening
        // the rest -- each remaining tab only needs RepositoryContext.Open (no graph work --
        // InitializeContextAsync is deferred until it is actually focused, §V3-9.3), so the pause
        // costs them nothing that matters while giving the focused tab's graph walk first claim
        // on the CPU/disk it needs to paint as fast as possible.
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
                // entry left and is skipped, same as before.
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
            lock (_sync)
            {
                if (_bgInitQueue.Count == 0) { _bgInitPumpIdle = true; return; }
                foreground = _foregroundInit;
                next = _bgInitQueue[0];
            }

            // Let the focused tab finish loading first (its own graph paint + identity +
            // RefreshAllAsync). If it is itself the next queued item, don't wait on it here.
            if (foreground is not null && !ReferenceEquals(foreground, next))
            {
                try { await foreground.FirstFullLoad.WaitAsync(foreground.Lifetime).ConfigureAwait(false); }
                catch { /* focused tab closed before it finished — carry on */ }
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
        lock (_sync)
        {
            if (ReferenceEquals(_focused, context))
                return;
            old = _focused;
            _focused = context;
            needsInit = _deferredInit.Remove(context);
            if (needsInit)
            {
                // §V10.1.2: a freshly focused deferred tab jumps the background pump — the user
                // is looking at it now — and becomes the tab the pump waits on before resuming.
                _bgInitQueue.Remove(context);
                _foregroundInit = context;
            }
        }

        // §V3-9.3: a placeholder tab initialises through the ordinary open path on first focus.
        if (needsInit)
            _ = InitializeContextAsync(context, foreground: true);

        PersistSession(); // §V3-9.3: focus is recorded, not only tab-set changes.
        FocusedRepositoryChanged?.Invoke(this, new FocusedRepositoryChangedEventArgs(old, context));
    }

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
    /// http/https/ssh/git/file schemes, are otherwise unrestricted (security review 2026-09-07,
    /// finding 3).</summary>
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
