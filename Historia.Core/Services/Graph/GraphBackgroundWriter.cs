namespace Historia.Services.Graph;

/// <summary>
/// The one app-wide queue for heavy background graph jobs. Two kinds of work go through it —
/// see the second <c>&lt;para&gt;</c> below for how each is serialized so a multi-tab session
/// never fans out several concurrent <c>git commit-graph write</c> invocations, nor several
/// concurrent headless walks, at once:
/// <list type="bullet">
/// <item><b>The active tab's own commit-graph write</b> (<see cref="MaybeSchedule"/>) — called by
///'s background spill after every chunk it walks, as soon as the repo is known to be large
/// enough to be worth it.</item>
/// <item><b>Inactive tabs, one at a time</b> (<see cref="EnqueueInactiveTab"/>) — a headless
/// per-tab pipeline (commit-graph, then disk cache) that <c>GraphLayoutService</c> drives; this
/// class only owns the queue/serialization/gate discipline, not what the work actually does.</item>
/// </list>
/// Every job awaits <see cref="IBackgroundWorkGate"/> before it starts (never mid-write — see the
/// plan's documentation check on git's lockfile cleanup and
/// <see cref="Process.Kill()"/>/<c>TerminateProcess</c>) and runs at
/// <see cref="ProcessPriorityClass.BelowNormal"/> for the git process itself
/// (<see cref="GitCliRequest.LowerPriority"/>). Never calls <c>git maintenance start</c>/
/// <c>register</c> or changes git config.
/// <para>
/// Once a commit-graph write has actually started it is never cancelled: git only removes <c>objects/info/commit-graph.lock</c> /
/// <c>commit-graphs/commit-graph-chain.lock</c> from its own exit/signal handlers, never when the
/// process tree is torn down with <c>TerminateProcess</c>
/// (<see cref="GitCliProcessSetup.TryKill"/> calls <c>Process.Kill(entireProcessTree: true)</c>,
/// which on Windows is exactly that — verified in <c>GitCliService.RunAsync</c>: a request's
/// cancellation token is what triggers <c>TryKill</c>). So <see cref="WriteCommitGraphCoreAsync"/>
/// runs the git process itself with <see cref="CancellationToken.None"/>; a context's
/// <see cref="RepositoryContext.Lifetime"/> only gates whether a write is allowed to <i>start</i>
/// (the gate wait, and the wait for <see cref="_serial"/> below) — a closed tab still skips a
/// write that hasn't begun, but never kills one already running.
/// </para>
/// <para>
/// The inactive-tab queue (<see cref="EnqueueInactiveTab"/>) de-dupes by context: a
/// context already queued and not yet running is not queued a second time. It also runs behind
/// its own serializer, <see cref="_headlessSerial"/>, separate from <see cref="_serial"/> — a long
/// headless walk (pure C#, no git process) no longer makes the active tab's own below-normal
/// commit-graph write wait behind it. <see cref="_serial"/> is reserved for the actual
/// <c>git commit-graph write</c> process, wherever it's invoked from, so a below-normal
/// commit-graph write may now briefly overlap a lowest-priority headless walk's non-git work —
/// an accepted trade-off, since only one process-level write can ever run at a time either way.
/// </para>
/// <para>
/// A tab gaining focus stops its own queued or running inactive-tab job outright
/// (<see cref="CancelInactiveTabAsync"/>, called by <c>WorkspaceService.FocusInternal</c>) rather
/// than letting it keep running to completion — the headless pipeline and that tab's own
/// foreground spill both write to the same on-disk store, and only cancelling the headless side
/// (never mid-write; see the docs on the write path above) keeps the two from ever appending at
/// once. <see cref="GraphMetaWriter.AppendSpillBatch"/> and
/// <see cref="GraphMetaWriter.TryCommitReorder"/> are the second line of defence, re-checking the
/// on-disk header under a per-repo lock before every write.
/// </para>
/// </summary>
internal sealed class GraphBackgroundWriter(IGitCliService cli, ISettingsService settings, IBackgroundWorkGate? gate = null)
{
    internal const int MinCommitsToWrite = 30_000;

    // A commit-graph is refreshed (not just written once) when enough new commits have
    // landed since the app's own last write. Tracked in memory only, per process — a graph this
    // process didn't itself write (a fresh launch reopening a repo git already maintains, or one
    // `git gc` rewrote) is left alone entirely; only ever refreshing a graph this process wrote
    // avoids fighting whatever cadence git's own maintenance already uses.
    private const double StaleFraction = 0.10;
    private const int StaleAbsolute = 50_000;

    // Serializes only the actual `git commit-graph write` process — active tab or headless —
    // against every other one. Waiting on it is cancellable (a not-yet-started write can still be
    // skipped when a tab closes); the process run itself, once it starts, is not (see the class
    // doc comment).
    private readonly SemaphoreSlim _serial = new(1, 1);

    // Serializes the inactive-tab headless pipeline's own steps (one at a time, in enqueue order)
    // separately from _serial, so a long headless walk never blocks the active tab's
    // commit-graph write behind it.
    private readonly SemaphoreSlim _headlessSerial = new(1, 1);

    private readonly HashSet<string> _attemptedFresh = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (int RowCount, DateTime WrittenUtc)> _lastWrite =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _sync = new();

    private readonly Queue<(RepositoryContext Context, Func<CancellationToken, Task> Work, CancellationTokenSource Cts, TaskCompletionSource Done)> _inactiveQueue = new();

    // Contexts currently queued (enqueued but not yet dequeued for running) — backs the
    // de-duplication in EnqueueInactiveTab. A context is removed from this set the
    // moment its job is dequeued, so a fresh EnqueueInactiveTab call for the same context while
    // its previous job is actually running queues a new run rather than being dropped.
    private readonly HashSet<string> _queuedContextIds = new(StringComparer.Ordinal);

    // Every queued or currently-running inactive job's own
    // cancellation source and completion signal, keyed by context id. A context can briefly have
    // two entries at once — one finishing its run while a fresh call (the "queued" dedupe doesn't
    // apply to a running job) queues another — so each context id maps to a list; a job removes
    // its own entry when it exits (cancelled, failed, or finished), never anyone else's.
    private readonly Dictionary<string, List<(CancellationTokenSource Cts, TaskCompletionSource Done)>> _inactiveJobs =
        new(StringComparer.Ordinal);
    private bool _pumping;

    /// <summary>
    /// Fire-and-forget: schedules a background commit-graph write/refresh for the ACTIVE tab if
    /// it qualifies (≥ <see cref="MinCommitsToWrite"/> commits walked, the setting is on, git is
    /// available). Safe to call repeatedly — the background spill calls this after every
    /// chunk it walks, as soon as the running total is known, rather than waiting for the whole
    /// spill to finish.
    /// </summary>
    public void MaybeSchedule(RepositoryContext context, int commitsWalked)
    {
        if (!settings.Current.WriteCommitGraphForLargeRepos || !cli.IsGitAvailable)
            return;
        if (commitsWalked < MinCommitsToWrite)
            return;

        _ = Task.Run(() => WriteOrRefreshCommitGraphAsync(context, commitsWalked));
    }

    /// <summary>
    /// Queues a headless job for an inactive tab. Jobs run one at a time, in the order
    /// enqueued, each behind the gate and <see cref="_headlessSerial"/>. A job's own cancellation
    /// (typically <paramref name="context"/>'s <c>Lifetime</c>, or a linked token the caller ties
    /// to "focus arrived") stops it at its own next checkpoint; one job failing or being
    /// cancelled never stalls the rest of the queue.
    /// <para>
    /// De-duplicated: a context that is already queued and not yet running is not
    /// queued again — a repeated focus-change churn that re-enqueues every inactive tab on every
    /// switch no longer piles up redundant jobs for the same context. A context whose job is
    /// currently <i>running</i> is not "queued", so a fresh call for it queues a new run.
    /// </para>
    /// </summary>
    public void EnqueueInactiveTab(RepositoryContext context, Func<CancellationToken, Task> headlessWork)
    {
        lock (_sync)
        {
            if (!_queuedContextIds.Add(context.Id))
                return; // already queued and not yet running — de-dupe.

            // This job's own cancellation is linked to the context's Lifetime (a tab
            // close still stops it) AND to CancelInactiveTabAsync below (focus arriving stops it
            // too) — neither owns the other; either can fire independently.
            var cts = CancellationTokenSource.CreateLinkedTokenSource(context.Lifetime);
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_inactiveJobs.TryGetValue(context.Id, out var jobs))
                _inactiveJobs[context.Id] = jobs = [];
            jobs.Add((cts, done));

            _inactiveQueue.Enqueue((context, headlessWork, cts, done));
            if (!_pumping)
            {
                _pumping = true;
                _ = Task.Run(DrainInactiveQueueAsync);
            }
        }
    }

    /// <summary>Cancels <paramref name="context"/>'s queued or running inactive job(s)
    /// and returns a task that completes once every one of them has actually exited. A no-op (a
    /// completed task) if there is none.</summary>
    public Task CancelInactiveTabAsync(RepositoryContext context)
    {
        List<(CancellationTokenSource Cts, TaskCompletionSource Done)>? jobs;
        lock (_sync)
            _inactiveJobs.TryGetValue(context.Id, out jobs);
        if (jobs is null || jobs.Count == 0)
            return Task.CompletedTask;

        // Snapshot before cancelling: a job's finally block removes its own entry from the live
        // list under _sync as soon as it exits, so iterating the live list while cancelling could
        // skip or double-handle an entry.
        (CancellationTokenSource Cts, TaskCompletionSource Done)[] snapshot;
        lock (_sync)
            snapshot = [.. jobs];
        foreach (var (cts, _) in snapshot)
            try { cts.Cancel(); } catch (ObjectDisposedException) { }
        return Task.WhenAll(snapshot.Select(j => j.Done.Task));
    }

    private async Task DrainInactiveQueueAsync()
    {
        while (true)
        {
            (RepositoryContext Context, Func<CancellationToken, Task> Work, CancellationTokenSource Cts, TaskCompletionSource Done)? item;
            lock (_sync)
            {
                if (_inactiveQueue.Count == 0) { _pumping = false; return; }
                item = _inactiveQueue.Dequeue();
                _queuedContextIds.Remove(item.Value.Context.Id);
            }
            var (context, work, cts, done) = item.Value;
            try
            {
                if (gate is not null)
                    await gate.WaitUntilOpenAsync(cts.Token).ConfigureAwait(false);
                // Its own serializer, separate from _serial — see the class doc comment. A long
                // headless walk here never makes the active tab's own commit-graph write (which
                // only ever needs _serial, and briefly) wait behind it.
                await _headlessSerial.WaitAsync(cts.Token).ConfigureAwait(false);
                try { await work(cts.Token).ConfigureAwait(false); }
                finally { _headlessSerial.Release(); }
            }
            catch (OperationCanceledException)
            {
                // This job's own context closed, or focus arrived and cancelled it — move on.
            }
            catch
            {
                // A headless job failing must not stall the rest of the queue.
            }
            finally
            {
                lock (_sync)
                {
                    if (_inactiveJobs.TryGetValue(context.Id, out var jobs))
                    {
                        jobs.RemoveAll(j => ReferenceEquals(j.Cts, cts));
                        if (jobs.Count == 0)
                            _inactiveJobs.Remove(context.Id);
                    }
                }
                done.TrySetResult();
                try { cts.Dispose(); } catch { /* best effort */ }
            }
        }
    }

    private async Task WriteOrRefreshCommitGraphAsync(RepositoryContext context, int rowCount)
    {
        try
        {
            var gitDir = context.GitDirectory;
            var hasBefore = HasCommitGraph(gitDir, out var mtimeBefore);
            if (!hasBefore)
            {
                lock (_sync) { if (!_attemptedFresh.Add(gitDir)) return; }
            }
            else if (!ShouldRefresh(gitDir, mtimeBefore, rowCount))
            {
                return;
            }

            if (gate is not null)
                await gate.WaitUntilOpenAsync(context.Lifetime).ConfigureAwait(false);

            await WriteCommitGraphCoreAsync(context, gitDir, rowCount).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Tab closed while waiting for the gate or for _serial — the write itself never
            // started, so there's nothing to clean up (a write already running is never
            // cancelled; see WriteCommitGraphCoreAsync).
        }
        catch
        {
            // Commit-graph maintenance is an optimisation, never a correctness dependency.
        }
    }

    /// <summary>Writes a missing commit-graph, or refreshes a stale one this process itself wrote
    /// earlier this session. The gate is assumed already open. Shared by the active-tab path and
    /// (once wired) the headless per-tab pipeline.
    /// <para>
    /// Waits on <see cref="_serial"/> with <paramref name="context"/>'s <c>Lifetime</c> — a tab
    /// that closes before its turn simply skips the write. Once that wait succeeds the actual
    /// <c>git commit-graph write</c> process runs with <see cref="CancellationToken.None"/>: git
    /// only clears its lock files from its own exit/signal handlers, never when the process tree
    /// is torn down with <c>TerminateProcess</c> (see the class doc comment), so a
    /// write already in flight must be left to finish rather than killed when the tab closes.
    /// </para>
    /// </summary>
    public async Task WriteCommitGraphCoreAsync(RepositoryContext context, string gitDir, int rowCount)
    {
        await _serial.WaitAsync(context.Lifetime).ConfigureAwait(false);
        try
        {
            // Re-check inside the lock: another repo sharing this gitdir (a second tab, a
            // worktree), or a concurrent call for the same one, may have already
            // written/refreshed it while this call waited for the gate/semaphore.
            var has = HasCommitGraph(gitDir, out var mtime);
            if (has && !ShouldRefresh(gitDir, mtime, rowCount))
                return;

            var args = has
                ? new[] { "commit-graph", "write", "--reachable", "--split", $"--expire-time={DateTimeOffset.UtcNow.AddHours(-1):O}" }
                : ["commit-graph", "write", "--reachable"];

            using var _ = Diagnostics.PerfLog.Scope($"GraphBackgroundWriter.write({context.DisplayName})");
            var result = await cli.RunAsync(new GitCliRequest
            {
                Arguments = args,
                WorkingDirectory = context.RepositoryPath,
                Timeout = Timeout.InfiniteTimeSpan,
                LowerPriority = true,
            }, CancellationToken.None).ConfigureAwait(false);

            if (result.IsSuccess)
                lock (_sync) _lastWrite[gitDir] = (rowCount, DateTime.UtcNow);
            else
                Diagnostics.PerfLog.Mark($"GraphBackgroundWriter: write failed (exit={result.ExitCode}) for {context.DisplayName}");
        }
        finally
        {
            _serial.Release();
        }
    }

    /// <summary>
    /// Writes a commit-graph for a newly cloned repository ahead of the background gate.
    /// Skips the 30k-commit threshold. If cancelled before acquiring the serial lock, the write is
    /// skipped. Once the git process starts, it runs with <see cref="CancellationToken.None"/> to avoid leaving
    /// stale lock files. Adds gitdir to <see cref="_attemptedFresh"/> and does NOT touch <see cref="_lastWrite"/>.
    /// </summary>
    public async Task WriteForCloneAsync(RepositoryContext context, CancellationToken ct)
    {
        if (!settings.Current.WriteCommitGraphForLargeRepos || !cli.IsGitAvailable)
            return;

        var gitDir = context.GitDirectory;
        await _serial.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            lock (_sync) _attemptedFresh.Add(gitDir);

            var args = new[] { "commit-graph", "write", "--reachable" };

            using var _ = Diagnostics.PerfLog.Scope($"GraphBackgroundWriter.writeForClone({context.DisplayName})");
            var result = await cli.RunAsync(new GitCliRequest
            {
                Arguments = args,
                WorkingDirectory = context.RepositoryPath,
                Timeout = Timeout.InfiniteTimeSpan,
                LowerPriority = true,
            }, CancellationToken.None).ConfigureAwait(false);

            if (!result.IsSuccess)
                Diagnostics.PerfLog.Mark($"GraphBackgroundWriter: clone write failed (exit={result.ExitCode}) for {context.DisplayName}");
        }
        finally
        {
            _serial.Release();
        }
    }

    /// <summary>
    /// Records that a commit-graph write finished and was sized to <paramref name="rowCount"/> rows,
    /// so subsequent staleness checks start from the actual cloned size.
    /// </summary>
    public void RecordWrite(string gitDir, int rowCount)
    {
        lock (_sync)
        {
            _lastWrite[gitDir] = (rowCount, DateTime.UtcNow);
        }
    }

    /// <summary>
    /// Fire-and-forget refresh of
    /// <paramref name="context"/>'s commit-graph after a successful auto-fetch. Unlike
    /// <see cref="MaybeSchedule"/>'s own-write tracking (<see cref="ShouldRefresh"/>), this
    /// refreshes ANY existing commit-graph regardless of who wrote it — git documents
    /// <c>commit-graph write --split</c> as a safe, idempotent no-op when nothing new needs to be
    /// added, so refreshing a graph this process never wrote (git's own maintenance, another tab,
    /// a previous session) is safe here specifically because the write is always incremental
    /// (<c>--split</c>) and time-bounded (<c>--expire-time</c>), never a from-scratch rewrite. A
    /// repo with no commit-graph yet only gets a first (non-split) write when a <c>.meta</c> header
    /// already knows it has at least <see cref="MinCommitsToWrite"/> commits — cheap, no repo walk.
    /// Same gate / <see cref="_serial"/> / below-normal-priority / never-killed discipline as every
    /// other write (see the class doc comment).
    /// </summary>
    public void RefreshAfterFetch(RepositoryContext context)
    {
        if (!settings.Current.WriteCommitGraphForLargeRepos || !cli.IsGitAvailable)
            return;

        _ = Task.Run(() => RefreshAfterFetchCoreAsync(context));
    }

    private async Task RefreshAfterFetchCoreAsync(RepositoryContext context)
    {
        try
        {
            var gitDir = context.GitDirectory;
            var has = HasCommitGraph(gitDir, out _);
            if (!has && !RepoKnownLarge(context.RepositoryPath))
                return;

            if (gate is not null)
                await gate.WaitUntilOpenAsync(context.Lifetime).ConfigureAwait(false);

            await _serial.WaitAsync(context.Lifetime).ConfigureAwait(false);
            try
            {
                // Re-check under the lock: another writer may have created the graph while this
                // call waited for the gate/semaphore.
                has = HasCommitGraph(gitDir, out _);
                var args = has
                    ? new[] { "commit-graph", "write", "--reachable", "--split", $"--expire-time={DateTimeOffset.UtcNow.AddHours(-1):O}" }
                    : ["commit-graph", "write", "--reachable"];

                using var scope = Diagnostics.PerfLog.Scope($"GraphBackgroundWriter.refreshAfterFetch({context.DisplayName})");
                await cli.RunAsync(new GitCliRequest
                {
                    Arguments = args,
                    WorkingDirectory = context.RepositoryPath,
                    Timeout = Timeout.InfiniteTimeSpan,
                    LowerPriority = true,
                }, CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                _serial.Release();
            }
        }
        catch (OperationCanceledException)
        {
            // Tab closed while waiting for the gate/_serial — the write never started.
        }
        catch
        {
            // Commit-graph maintenance is an optimisation, never a correctness dependency.
        }
    }

    /// <summary>Cheap, header-only check: does the on-disk graph-cache header for
    /// <paramref name="repoPath"/> (there is only ever one physical <c>.meta.hdr</c> per repo,
    /// whichever remote-branch flag last wrote it) know it already has at least
    /// <see cref="MinCommitsToWrite"/> commits? Tries both remote-branch flags since neither is
    /// known up front here — at most one can ever match the single on-disk header.</summary>
    private static bool RepoKnownLarge(string repoPath)
    {
        foreach (var includeRemoteBranches in new[] { true, false })
        {
            var probe = GraphMetaStore.ProbeExisting(repoPath, includeRemoteBranches);
            if (probe.Ok)
                return probe.RowCount >= MinCommitsToWrite;
        }
        return false;
    }

    /// <summary>A commit-graph this process never wrote (or one whose mtime moved since we last
    /// wrote it — someone else, e.g. `git gc`, keeps it fresh on their own) is left alone. One we
    /// did write is refreshed once at least <see cref="StaleAbsolute"/> commits, or
    /// <see cref="StaleFraction"/> of its size, have landed since.</summary>
    private bool ShouldRefresh(string gitDir, DateTime graphMtimeUtc, int currentRowCount)
    {
        lock (_sync)
        {
            if (!_lastWrite.TryGetValue(gitDir, out var last))
                return false;
            if (graphMtimeUtc > last.WrittenUtc.AddSeconds(1))
                return false;
            var delta = currentRowCount - last.RowCount;
            return delta >= StaleAbsolute || (last.RowCount > 0 && (double)delta / last.RowCount >= StaleFraction);
        }
    }

    internal static bool HasCommitGraph(string gitDirectory) => HasCommitGraph(gitDirectory, out _);

    private static bool HasCommitGraph(string gitDirectory, out DateTime mtimeUtc)
    {
        mtimeUtc = default;
        try
        {
            var flat = Path.Combine(gitDirectory, "objects", "info", "commit-graph");
            if (File.Exists(flat))
            {
                mtimeUtc = File.GetLastWriteTimeUtc(flat);
                return true;
            }
            var chain = Path.Combine(gitDirectory, "objects", "info", "commit-graphs");
            if (Directory.Exists(chain))
            {
                mtimeUtc = Directory.GetLastWriteTimeUtc(chain);
                return true;
            }
            return false;
        }
        catch
        {
            return false;
        }
    }
}
