namespace Historia.Services.Graph;

/// <summary>
/// §4 of the linux cold-prime plan; expanded by the v10 graph memory footprint plan's Phase 4
/// into the one app-wide serial queue for heavy background graph jobs. Two kinds of work go
/// through it, both serialized against each other via <see cref="_serial"/> so a multi-tab
/// session never fans out several concurrent <c>git commit-graph write</c> invocations or
/// headless walks at once:
/// <list type="bullet">
/// <item><b>The active tab's own commit-graph write</b> (<see cref="MaybeSchedule"/>) — called by
/// §7.6's background spill after every chunk it walks, as soon as the repo is known to be large
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
/// <c>register</c> or changes git config — see the plan's "documentation checked" table.
/// </summary>
internal sealed class GraphBackgroundWriter(IGitCliService cli, ISettingsService settings, IBackgroundWorkGate? gate = null)
{
    internal const int MinCommitsToWrite = 30_000;

    // Phase 4: a commit-graph is refreshed (not just written once) when enough new commits have
    // landed since the app's own last write. Tracked in memory only, per process — a graph this
    // process didn't itself write (a fresh launch reopening a repo git already maintains, or one
    // `git gc` rewrote) is left alone entirely; only ever refreshing a graph this process wrote
    // avoids fighting whatever cadence git's own maintenance already uses.
    private const double StaleFraction = 0.10;
    private const int StaleAbsolute = 50_000;

    // App-wide serialization: only one heavy background graph job (a commit-graph write, or a
    // headless per-tab pipeline step) runs at a time.
    private readonly SemaphoreSlim _serial = new(1, 1);

    private readonly HashSet<string> _attemptedFresh = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (int RowCount, DateTime WrittenUtc)> _lastWrite =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _sync = new();

    private readonly Queue<(RepositoryContext Context, Func<CancellationToken, Task> Work)> _inactiveQueue = new();
    private bool _pumping;

    /// <summary>
    /// Fire-and-forget: schedules a background commit-graph write/refresh for the ACTIVE tab if
    /// it qualifies (≥ <see cref="MinCommitsToWrite"/> commits walked, the setting is on, git is
    /// available). Safe to call repeatedly — the §7.6 background spill calls this after every
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
    /// Phase 4: queues a headless job for an inactive tab. Jobs run one at a time, in the order
    /// enqueued, each behind the gate and the app-wide serial lock. A job's own cancellation
    /// (typically <paramref name="context"/>'s <c>Lifetime</c>, or a linked token the caller ties
    /// to "focus arrived") stops it at its own next checkpoint; one job failing or being
    /// cancelled never stalls the rest of the queue.
    /// </summary>
    public void EnqueueInactiveTab(RepositoryContext context, Func<CancellationToken, Task> headlessWork)
    {
        lock (_sync)
        {
            _inactiveQueue.Enqueue((context, headlessWork));
            if (!_pumping)
            {
                _pumping = true;
                _ = Task.Run(DrainInactiveQueueAsync);
            }
        }
    }

    private async Task DrainInactiveQueueAsync()
    {
        while (true)
        {
            (RepositoryContext Context, Func<CancellationToken, Task> Work)? item;
            lock (_sync)
            {
                if (_inactiveQueue.Count == 0) { _pumping = false; return; }
                item = _inactiveQueue.Dequeue();
            }
            var (context, work) = item.Value;
            try
            {
                if (gate is not null)
                    await gate.WaitUntilOpenAsync(context.Lifetime).ConfigureAwait(false);
                await _serial.WaitAsync(context.Lifetime).ConfigureAwait(false);
                try { await work(context.Lifetime).ConfigureAwait(false); }
                finally { _serial.Release(); }
            }
            catch (OperationCanceledException)
            {
                // This job's own context closed, or focus arrived and cancelled it — move on.
            }
            catch
            {
                // A headless job failing must not stall the rest of the queue.
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

            await _serial.WaitAsync(context.Lifetime).ConfigureAwait(false);
            try
            {
                await WriteCommitGraphCoreAsync(context, gitDir, rowCount).ConfigureAwait(false);
            }
            finally
            {
                _serial.Release();
            }
        }
        catch (OperationCanceledException)
        {
            // Tab closed mid-write — best effort, try again next launch.
        }
        catch
        {
            // Commit-graph maintenance is an optimisation, never a correctness dependency.
        }
    }

    /// <summary>Writes a missing commit-graph, or refreshes a stale one this process itself wrote
    /// earlier this session. Caller holds <see cref="_serial"/> and the gate is already open.
    /// Shared by the active-tab path and (once wired) the headless per-tab pipeline.</summary>
    public async Task WriteCommitGraphCoreAsync(RepositoryContext context, string gitDir, int rowCount)
    {
        // Re-check inside the lock: another repo sharing this gitdir (a second tab, a worktree),
        // or a concurrent call for the same one, may have already written/refreshed it while this
        // call waited for the gate/semaphore.
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
        }, context.Lifetime).ConfigureAwait(false);

        if (result.IsSuccess)
            lock (_sync) _lastWrite[gitDir] = (rowCount, DateTime.UtcNow);
        else
            Diagnostics.PerfLog.Mark($"GraphBackgroundWriter: write failed (exit={result.ExitCode}) for {context.DisplayName}");
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
