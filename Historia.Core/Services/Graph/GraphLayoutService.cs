namespace Historia.Services.Graph;

/// <inheritdoc cref="IGraphLayoutService"/>
public sealed class GraphLayoutService : IGraphLayoutService
{
    private const int WalkOverscan = 2_000;

    /// <summary>Safety net for <see cref="MaybeStartBackgroundSpill"/>: how long the background
    /// full-history walk waits for <see cref="RepositoryContext.FirstGraphPaint"/> before it
    /// starts anyway. A graph control always completes that signal (paint, failure, or cancel)
    /// in well under this; the grace only bites a headless open where nothing ever raises it.
    /// Mirrors <c>WorkspaceService.GraphPriorityTimeout</c>.</summary>
    private static readonly TimeSpan SpillStartGrace = TimeSpan.FromSeconds(8);

    /// <summary>Rows per chunk of the background full-history walk (§7.6). Each chunk is a
    /// separate low-priority repo-gate acquisition that *resumes* the primed revwalk (cheap —
    /// no tip reseed), so this bounds how long a foreground caller that has already started
    /// waiting can be stuck behind the spill to roughly one chunk's pull.</summary>
    private const int SpillWalkChunkRows = 25_000;

    private readonly ISettingsService? _settings;

    /// <summary>§7.6: page per-commit metadata from a random-access file instead of keeping
    /// it resident. Read once — the setting takes effect on the next launch.</summary>
    private readonly bool _metaFromDisk;

    public GraphLayoutService() { }
    public GraphLayoutService(ISettingsService settings)
    {
        _settings = settings;
        // Disk-backed metadata pages from the graph cache folder.
        _metaFromDisk = settings.Current.GraphMetadataFromDisk;
    }

    private sealed class RepoCache
    {
        public required GraphRefScope Scope { get; set; }
        public required bool IncludeRemotes { get; set; }
        public List<RawCommit> Commits { get; } = [];
        public Dictionary<ObjectId, IReadOnlyList<GraphDecorationInput>> Decorations { get; set; } = [];
        public bool Exhausted { get; set; }

        // ── §7.6 disk-backed metadata ──
        public long Fingerprint { get; set; }
        public int SpilledRows { get; set; }
        public bool MetaComplete { get; set; }   // the whole history is on disk
        public bool BackgroundSpillRunning { get; set; }
        public GraphMetaWriter? MetaWriter { get; set; }
        public DiskCommitStore? MetaStore { get; set; }
        public readonly object SpillSync = new();
        public readonly CancellationTokenSource Cts = new();

        // ── resumable topological walk (v10.1.2) ──
        // A live LibGit2Sharp revwalk enumerator, primed once (the multi-second tip-priority
        // cost on a big repo) and then pulled forward in chunks by EnsureWalkedAsync — the
        // quick phase, each scroll Extend, the silent crawler and the §7.6 background spill all
        // continue this ONE walk instead of restarting from the tips every time. It is a
        // libgit2-backed object, so it is ONLY ever touched inside RepositoryContext's gate
        // (WalkFrom points at the next row it will yield). Disposed on reset / Forget /
        // exhaustion / a fingerprint change.
        public IEnumerator<Commit>? LiveWalk { get; set; }
        public long LiveWalkFingerprint { get; set; }
        public int LiveWalkPos { get; set; } // rows already pulled from LiveWalk

        /// <summary>Dispose the revwalk enumerator and forget it. Only safe on the repo gate
        /// (i.e. from inside a WithRepository* callback) or after Cts has been cancelled and the
        /// spill task has observed it — never while another thread may be mid-MoveNext.</summary>
        public void DisposeLiveWalk()
        {
            var w = LiveWalk;
            DropLiveWalk();
            try { w?.Dispose(); } catch { /* best effort */ }
        }

        /// <summary>Forget the enumerator without disposing it — for off-gate callers (Forget /
        /// reset). The orphaned CommitEnumerator frees its git_revwalk from its finalizer, so
        /// this trades a brief native leak for never racing a Dispose against an in-flight walk.</summary>
        public void DropLiveWalk()
        {
            LiveWalk = null;
            LiveWalkFingerprint = 0;
            LiveWalkPos = 0;
        }
    }

    /// <summary>
    /// Per-repo decoration cache keyed by a cheap fingerprint of every ref's name+target.
    /// Survives <see cref="Forget"/> (which the RefsChanged path calls) so an unchanged
    /// ref set never re-pays the multi-second <see cref="BuildDecorations"/> scan.
    /// </summary>
    private sealed record DecoCache(long Fingerprint, Dictionary<ObjectId, IReadOnlyList<GraphDecorationInput>> Decos);

    private readonly DagLayoutEngine _engine = new();
    private readonly GraphWalkCache _walkCache = new();
    private readonly ConditionalWeakTable<RepositoryContext, RepoCache> _caches = [];
    // Keyed by repository path, not context: survives tab close/reopen and context re-creation,
    // and decorations are a function of the repo's refs, not the context object.
    private readonly Dictionary<string, DecoCache> _decoByPath = new(StringComparer.OrdinalIgnoreCase);
    private const int DecoCacheCap = 24;
    private readonly object _sync = new();

    public async Task<GraphLayoutResult> ComputeLayoutAsync(
        RepositoryContext context, GraphQuery query, int maxRows = 10_000, CancellationToken ct = default)
    {
        Diagnostics.PerfLog.Mark($"ComputeLayout enter (maxRows={maxRows})");
        var cache = GetOrResetCache(context, query, force: false);
        if (cache.Commits.Count > 0 && cache.Fingerprint != 0)
        {
            var currentFp = await context.WithRepositoryAsync(repo =>
                ComputeFingerprint(repo, query, out _), ct).ConfigureAwait(false);
            if (currentFp != cache.Fingerprint)
                cache = GetOrResetCache(context, query, force: true);
        }
        await EnsureWalkedAsync(context, query, cache, maxRows + WalkOverscan, ct).ConfigureAwait(false);
        Diagnostics.PerfLog.Mark($"ComputeLayout walked ({cache.Commits.Count} commits cached)");

        var spill = SpillEnabled(query);

        // Snapshot the rows the engine needs under the cache lock: the background spill task
        // (§7.6) can be mutating cache.Commits (AddRange / metadata stub) on another thread.
        List<RawCommit> input;
        lock (cache)
            input = cache.Commits.GetRange(0, Math.Min(cache.Commits.Count, maxRows + WalkOverscan));

        // §V10.1: the trunk pre-pass needs a full-history ancestry oracle for the checked-out
        // branch. Keep one repo handle open across the (synchronous, few-ms) engine run and
        // answer straight from LibGit2 — at most one call per candidate tip, memoised.
        GraphLayoutResult result;
        using (Diagnostics.PerfLog.Scope("engine.Compute"))
            result = await context.WithRepositoryAsync(repo =>
            {
                var memo = new Dictionary<(ObjectId, ObjectId), bool>();
                bool IsAncestor(ObjectId a, ObjectId b)
                {
                    if (a.Equals(b)) return true;
                    var key = (a, b);
                    if (memo.TryGetValue(key, out var hit)) return hit;
                    var ca = repo.Lookup<Commit>(a);
                    var cb = repo.Lookup<Commit>(b);
                    var r = ca is not null && cb is not null
                        && repo.ObjectDatabase.CalculateHistoryDivergence(ca, cb).AheadBy is 0;
                    memo[key] = r;
                    return r;
                }
                return _engine.Compute(input, cache.Decorations, maxRows, query.MaxLanes, ct,
                    retainCommits: !spill, isAncestor: IsAncestor);
            }, ct).ConfigureAwait(false);

        result = result with { Fingerprint = cache.Fingerprint };

        if (!spill)
            return result;
        var swapped = SpillAndSwap(context, cache, query.IncludeRemoteBranches, result);
        MaybeStartBackgroundSpill(context, cache, query);
        return swapped with { Fingerprint = cache.Fingerprint };
    }

    /// <inheritdoc/>
    public GraphLayoutResult? TryOptimisticSnapshot(RepositoryContext context, GraphQuery query, int maxRows)
    {
        // Only the very first paint for this context can benefit — once an in-memory walk
        // exists (this session's own earlier walk, or the real ComputeLayoutAsync already
        // finished), it is already at least as fresh as a stale on-disk snapshot.
        lock (_sync)
            if (_caches.TryGetValue(context, out var existing) && existing.Commits.Count > 0)
                return null;

        var loaded = _walkCache.TryLoadStale(context.RepositoryPath, query, metadataOnDisk: false);
        if (loaded is null)
            return null;

        Diagnostics.PerfLog.Mark($"optimistic snapshot HIT: {loaded.Commits.Count} commits (stale, unverified)");
        return _engine.Compute(loaded.Commits, loaded.Decorations, maxRows, query.MaxLanes, CancellationToken.None, retainCommits: true);
    }

    public async Task<GraphLayoutResult> ExtendLayoutAsync(
        RepositoryContext context, GraphLayoutResult existing, int additionalRows, CancellationToken ct = default)
    {
        RepoCache? cache;
        lock (_sync)
            _caches.TryGetValue(context, out cache);
        if (cache is null)
            return existing;

        var want = existing.State.Rows.Count + additionalRows + WalkOverscan;
        await EnsureWalkedAsync(context, new GraphQuery { RefScope = cache.Scope, IncludeRemoteBranches = cache.IncludeRemotes },
            cache, want, ct).ConfigureAwait(false);

        // Extend mutates existing.State in place; run it to completion (a handful of ms for
        // <=5k rows) rather than risk a cancellation leaving that shared state half-updated.
        // Read the consumed-row count and cut the slice under the SAME lock the extend runs
        // under: a concurrent ExtendLayoutAsync on this layout (e.g. the initial compute's
        // phase-2 extend racing a reveal-driven one) may have advanced State.Rows since the
        // snapshot above, and feeding _engine.Extend an overlapping slice re-places commits it
        // already consumed — duplicate rows and a corrupted RowByCommitId.
        GraphLayoutResult result;
        lock (existing.State)
        {
            var consumed = existing.State.Rows.Count;
            List<RawCommit> next;
            lock (cache) // guard against a concurrent background-spill AddRange (§7.6)
            {
                if (cache.Commits.Count <= consumed)
                    return existing;
                // Only what this extend consumes (+ overscan) — the background spill can have grown
                // cache.Commits to the hard cap, and copying all of it here would be pointless.
                var take = Math.Min(cache.Commits.Count - consumed, additionalRows + WalkOverscan);
                next = cache.Commits.GetRange(consumed, take);
            }
            result = _engine.Extend(existing, next, cache.Decorations, additionalRows, CancellationToken.None);
        }

        var extQuery = new GraphQuery { RefScope = cache.Scope, IncludeRemoteBranches = cache.IncludeRemotes };
        if (!SpillEnabled(extQuery))
            return result with { Fingerprint = cache.Fingerprint };
        var swapped = SpillAndSwap(context, cache, cache.IncludeRemotes, result);
        MaybeStartBackgroundSpill(context, cache, extQuery); // resume if it stalled/hadn't started
        return swapped with { Fingerprint = cache.Fingerprint };
    }

    /// <summary>§7.6: metadata is disk-backed only for the plain all-refs query the walk cache
    /// already supports; a path filter / single branch / stashes keep the in-memory store.</summary>
    private bool SpillEnabled(GraphQuery query) => _metaFromDisk && GraphWalkCache.Supports(query);

    /// <summary>
    /// §7.6: append the freshly laid-out rows' metadata to the on-disk store, point
    /// <see cref="GraphLayout.Commits"/> at the paged reader, and release the resident strings
    /// for the spilled rows. On any IO failure the layout falls back to an in-memory array
    /// rebuilt from the still-resident walk records — correctness never depends on the file.
    /// </summary>
    private GraphLayoutResult SpillAndSwap(
        RepositoryContext context, RepoCache cache, bool includeRemotes, GraphLayoutResult result)
    {
        var rowCount = result.Layout.RowCount;
        var repoPath = context.RepositoryPath;

        try
        {
            lock (cache.SpillSync)
            {
                // Warm launch: a matching store from a previous session already holds these rows.
                if (cache is { SpilledRows: 0, MetaStore: null } && cache.Fingerprint != 0
                    && GraphMetaStore.TryOpen(repoPath, cache.Fingerprint, includeRemotes, rowCount) is { } warm
                    && warm.Count >= rowCount)
                {
                    cache.MetaStore = warm;
                    cache.SpilledRows = warm.Count;
                }

                if (cache.Fingerprint != 0 && rowCount > cache.SpilledRows)
                    SpillRange(cache, repoPath, includeRemotes, cache.SpilledRows, rowCount, result.Layout.IsComplete);
                else
                    cache.MetaStore?.SetCount(Math.Max(cache.MetaStore.Count, rowCount));

                if (cache.MetaStore is null && cache.Fingerprint != 0)
                    cache.MetaStore = GraphMetaStore.TryOpen(repoPath, cache.Fingerprint, includeRemotes, rowCount);

                if (cache.MetaStore is not null && cache.MetaStore.Count >= rowCount)
                {
                    result.Layout.Commits = cache.MetaStore;
                    StubResidentMetadata(cache, rowCount);
                    return result;
                }
            }
        }
        catch { /* fall through to the in-memory rebuild */ }

        // Disk store unavailable — hand back an array built from the resident walk records.
        // (If those were loaded id/parents-only, a Recompute re-walks and re-spills.)
        CommitMetadata[] arr;
        lock (cache)
        {
            arr = new CommitMetadata[Math.Min(rowCount, cache.Commits.Count)];
            for (var i = 0; i < arr.Length; i++)
                arr[i] = cache.Commits[i].Metadata;
        }
        result.Layout.Commits = new ArrayCommitStore(arr);
        return result;
    }

    /// <summary>Appends rows <paramref name="from"/>..<paramref name="to"/> of
    /// <c>cache.Commits</c> to the on-disk store and advances <c>SpilledRows</c>. Caller holds
    /// <c>cache.SpillSync</c>.</summary>
    private static void SpillRange(
        RepoCache cache, string repoPath, bool includeRemotes, int from, int to, bool exhausted)
    {
        if (to <= from)
            return;
        List<RawCommit> snap;
        lock (cache)
        {
            to = Math.Min(to, cache.Commits.Count);
            if (to <= from)
                return;
            snap = cache.Commits.GetRange(from, to - from);
        }
        cache.MetaWriter ??= new GraphMetaWriter(repoPath, cache.Fingerprint, includeRemotes);
        if (from == 0)
        {
            cache.MetaWriter.Reset();
            cache.MetaStore?.Dispose();
            cache.MetaStore = null;
        }
        cache.MetaWriter.Append(snap, 0, snap.Count);
        cache.MetaWriter.Commit(to, exhausted);
        cache.SpilledRows = to;
        cache.MetaComplete = exhausted;
        cache.MetaStore?.SetCount(to);
    }

    /// <summary>
    /// §7.6: once the visible window is spilled, keep walking + spilling the rest of the
    /// history in the background so the whole repo is cached on disk without the user
    /// scrolling. Cancelled by <see cref="Forget"/> when the ref set changes or the tab closes.
    /// <para>The heavy full-history walk does not start until this context's first graph paint
    /// has landed (<see cref="SpillStartGrace"/> safety net) — on a large repo it can run for
    /// tens of seconds and it serializes through the same single-access repo gate the quick +
    /// extend paint needs, so racing it against first paint is what makes a cold open of a big
    /// repo take far longer than the "small first window" design should ever cost.</para>
    /// </summary>
    private void MaybeStartBackgroundSpill(RepositoryContext context, RepoCache cache, GraphQuery query)
    {
        const int BatchRows = 25_000;
        var cap = GraphLimits.GetMaxResidentRows(_metaFromDisk);

        lock (cache.SpillSync)
        {
            // Note: cache.Exhausted (the walk finished) is NOT a reason to skip — that is exactly
            // when the remaining rows are already in cache.Commits and just need spilling.
            if (!SpillEnabled(query) || cache.Fingerprint == 0 || cache.BackgroundSpillRunning
                || cache.MetaComplete || cache.SpilledRows >= cap)
                return;
            int have;
            lock (cache) have = Math.Min(cache.Commits.Count, cap);
            if (cache.SpilledRows >= have && cache.Exhausted)
                return; // everything walked so far is already on disk and there is no more
            cache.BackgroundSpillRunning = true;
        }

        var ct = cache.Cts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                // Hold off until the user's first paint for this context is on screen. The walk
                // below takes the repo gate for a long stretch on a big repo; starting it now
                // would queue the quick-phase compute and the phase-2 extend behind it.
                await Task.WhenAny(context.FirstGraphPaint, Task.Delay(SpillStartGrace, ct)).ConfigureAwait(false);
                if (ct.IsCancellationRequested)
                    return;

                // Walk to the ceiling in chunks, each a low-priority gate turn that *resumes*
                // the primed revwalk (EnsureWalkedAsync continues cache.LiveWalk — no tip
                // reseed, no BuildDecorations), yielding the gate between chunks so a tab
                // switch / scroll / status refresh on this repo waits at most one chunk's pull.
                var ceiling = cap + WalkOverscan;
                while (true)
                {
                    int target;
                    lock (cache) target = Math.Min(cache.Commits.Count + SpillWalkChunkRows, ceiling);
                    await EnsureWalkedAsync(context, query, cache, target, ct, background: true)
                        .ConfigureAwait(false);
                    bool walkDone;
                    lock (cache) walkDone = cache.Exhausted || cache.Commits.Count >= cap;
                    if (walkDone)
                        break;
                    await Task.Delay(150, ct).ConfigureAwait(false); // let foreground gate work in
                }

                while (!ct.IsCancellationRequested)
                {
                    int from, to;
                    bool done;
                    lock (cache.SpillSync)
                    {
                        var total = Math.Min(cache.Commits.Count, cap);
                        from = cache.SpilledRows;
                        to = Math.Min(total, from + BatchRows);
                        done = to >= total;
                        if (to > from)
                            SpillRange(cache, context.RepositoryPath, cache.IncludeRemotes, from, to,
                                exhausted: done && cache.Exhausted);
                        if (to > 0)
                            StubResidentMetadata(cache, to);
                    }
                    if (to <= from || done)
                        break;
                    await Task.Delay(150, ct).ConfigureAwait(false); // let UI git ops interleave
                }
            }
            catch (OperationCanceledException) { }
            catch { /* best-effort background caching */ }
            finally
            {
                cache.BackgroundSpillRunning = false;
                // On exhaustion PumpCommits already disposed the walk on the gate; on an
                // external cancel Forget/reset already DropLiveWalk'd it. This is just a
                // belt-and-braces no-op for those paths — a walk that only reached the resident
                // ceiling stays primed for later foreground Extends and is freed at tab close.
                if (ct.IsCancellationRequested || cache.Exhausted || cache.MetaComplete)
                    cache.DisposeLiveWalk();
            }
        }, ct);
    }

    /// <summary>Replace the resident <see cref="CommitMetadata"/> <b>strings</b> for rows already
    /// spilled to disk with a stub — the paged store serves the real text. Id, parent count and
    /// the two timestamps are kept: they are 16 bytes/row and <see cref="DagLayoutEngine"/>'s
    /// tip-priority pre-pass sorts candidate tips by <see cref="CommitMetadata.CommitWhen"/>
    /// (§V10.1). Zeroing it here made every re-layout after the first spill fall back to SHA
    /// order and pin the trunk to the wrong lane — see
    /// <c>GraphSpillTrunkStabilityTests</c>.</summary>
    private static void StubResidentMetadata(RepoCache cache, int uptoRow)
    {
        lock (cache)
        {
            var n = Math.Min(uptoRow, cache.Commits.Count);
            for (var i = 0; i < n; i++)
            {
                var rc = cache.Commits[i];
                if (rc.Metadata.MessageShort.Length == 0 && rc.Metadata.AuthorName.Length == 0)
                    continue; // already stubbed
                cache.Commits[i] = rc with
                {
                    Metadata = new CommitMetadata
                    {
                        Id = rc.Id,
                        MessageShort = "",
                        AuthorName = "",
                        AuthorEmail = "",
                        AuthorWhen = rc.Metadata.AuthorWhen,
                        CommitWhen = rc.Metadata.CommitWhen,
                        ParentCount = rc.Metadata.ParentCount,
                    },
                };
            }
        }
    }

    public async Task<GraphLayoutResult> RecomputeAsync(
        RepositoryContext context, GraphQuery query, GraphLayoutResult previous,
        InvalidationReason reason, int maxRows = 10_000, CancellationToken ct = default)
    {
        if (reason == InvalidationReason.RefsChanged && previous.Fingerprint != 0)
        {
            var currentFp = await context.WithRepositoryAsync(repo =>
                ComputeFingerprint(repo, query, out _), ct).ConfigureAwait(false);
            if (currentFp == previous.Fingerprint)
                return previous;
        }

        // Every invalidation reason recomputes the whole window: lane assignment is a
        // left-to-right dependency chain, so a change at row k can alter every later row (§7.3).
        Forget(context);
        return await ComputeLayoutAsync(context, query, maxRows, ct).ConfigureAwait(false);
    }

    public void Forget(RepositoryContext context)
    {
        lock (_sync)
        {
            if (_caches.TryGetValue(context, out var cache))
            {
                try { cache.Cts.Cancel(); } catch (ObjectDisposedException) { }
                cache.Cts.Dispose();
                cache.MetaStore?.Dispose();
                cache.DropLiveWalk(); // off-gate: don't Dispose a walk another thread may hold
            }
            _caches.Remove(context);
        }
    }

    public int ClearPersistentCache()
    {
        lock (_sync)
            _decoByPath.Clear();
        // Any DiskCommitStore still open for a focused tab keeps its file handle; WipeAll may
        // not remove those until the tab closes, but the next open re-walks regardless.
        return _walkCache.WipeAll();
    }

    private RepoCache GetOrResetCache(RepositoryContext context, GraphQuery query, bool force)
    {
        lock (_sync)
        {
            if (!force && _caches.TryGetValue(context, out var existing)
                && existing.Scope == query.RefScope && existing.IncludeRemotes == query.IncludeRemoteBranches)
                return existing;

            // A forced reset (stale fingerprint) discards a cache that may have a background
            // spill task and an open on-disk store still live — tear those down explicitly,
            // the same as Forget(), or the old task keeps writing to a now-orphaned RepoCache
            // and its DiskCommitStore file handle never closes.
            if (_caches.TryGetValue(context, out var stale))
            {
                try { stale.Cts.Cancel(); } catch (ObjectDisposedException) { }
                stale.Cts.Dispose();
                stale.MetaStore?.Dispose();
                stale.DropLiveWalk(); // off-gate: don't Dispose a walk another thread may hold
            }

            _caches.Remove(context);
            var fresh = new RepoCache { Scope = query.RefScope, IncludeRemotes = query.IncludeRemoteBranches };
            _caches.Add(context, fresh);
            return fresh;
        }
    }

    private async Task EnsureWalkedAsync(
        RepositoryContext context, GraphQuery query, RepoCache cache, int wantCount, CancellationToken ct,
        bool background = false)
    {
        if (cache.Exhausted || cache.Commits.Count >= wantCount)
            return;

        DecoCache? deco;
        lock (_sync)
            _decoByPath.TryGetValue(context.RepositoryPath, out deco);

        Diagnostics.PerfLog.Mark($"EnsureWalked need {wantCount}, have {cache.Commits.Count}, decoFp={deco?.Fingerprint ?? 0}");
        var repoPath = context.RepositoryPath;
        Task<WalkResult> WithRepo(Func<Repository, WalkResult> work) => background
            ? context.WithRepositoryBackgroundAsync(work, ct)
            : context.WithRepositoryAsync(work, ct);
        var walked = await WithRepo(repo =>
        {
            var fp = ComputeFingerprint(repo, query, out var allRefs);
            // Read fresh on the gate — a concurrent EnsureWalkedAsync (foreground extend vs the
            // background spill) serialises here and may have appended since the outer check.
            var have = cache.Commits.Count;
            if (have >= wantCount || cache.Exhausted)
                return new WalkResult([], cache.Decorations, cache.Exhausted, fp, DecosFresh: false);

            // Fresh cache, nothing primed: try the on-disk walk before touching the revwalk at
            // all. When the paged metadata store opens for this fingerprint, load id + parents
            // only — the strings come from the store, not a resident copy of the whole history.
            if (have == 0 && cache.LiveWalk is null)
            {
                var probe = _metaFromDisk
                    ? GraphMetaStore.Probe(repoPath, fp, query.IncludeRemoteBranches)
                    : default;
                var store = probe.Ok
                    ? GraphMetaStore.TryOpen(repoPath, fp, query.IncludeRemoteBranches, wantCount)
                    : null;
                // Load id/parents only when the store already holds the WHOLE history — otherwise
                // the background spill still needs real metadata in cache.Commits to fill the gap.
                var lite = store is not null && probe.Exhausted;
                if (_walkCache.TryLoad(repoPath, query, wantCount, fp, metadataOnDisk: lite) is { } hit)
                {
                    Diagnostics.PerfLog.Mark($"walk cache HIT: {hit.Commits.Count} commits, exhausted={hit.Exhausted}, lite={lite}");
                    return new WalkResult(hit.Commits, hit.Decorations, hit.Exhausted, fp, DecosFresh: true, FromDisk: true, MetaStore: store, MetaExhausted: probe.Exhausted);
                }
                store?.Dispose(); // TryLoad missed — don't leak the handle
            }

            // Continue the ONE primed topological walk for this fingerprint — no tip reseed, no
            // BuildDecorations. This is what makes an Extend / silent-crawl / spill chunk cheap
            // on a big repo after the first (unavoidably expensive) prime.
            if (cache.LiveWalk is { } live && cache.LiveWalkFingerprint == fp)
            {
                var need = wantCount - have;
                var delta = new List<RawCommit>(Math.Min(need, 8192));
                var exhaustedNow = PumpCommits(live, delta, skip: 0, want: need, cache, ct);
                if (exhaustedNow)
                    cache.DisposeLiveWalk();
                Diagnostics.PerfLog.Mark($"walk resumed +{delta.Count} (pos {cache.LiveWalkPos}, exhausted={exhaustedNow})");
                return new WalkResult(delta, cache.Decorations, exhaustedNow, fp, DecosFresh: false);
            }

            // No usable primed walk (first walk, or the ref set changed): prime a fresh one.
            cache.DisposeLiveWalk();
            return StartWalk(repo, query, wantCount, have, deco, allRefs, fp, cache, ct);
        }).ConfigureAwait(false);

        lock (cache)
        {
            cache.Commits.AddRange(walked.Commits);
            cache.Decorations = walked.Decorations;
            cache.Exhausted = walked.Exhausted;
            if (walked.Fingerprint != 0)
                cache.Fingerprint = walked.Fingerprint; // §7.6: keyed store needs it at spill time
            if (walked.MetaStore is not null && cache.MetaStore is null)
            {
                cache.MetaStore = walked.MetaStore;
                cache.SpilledRows = walked.MetaStore.Count; // these rows are already on disk
                cache.MetaComplete = walked.MetaExhausted;
                cache.MetaWriter ??= new GraphMetaWriter(repoPath, cache.Fingerprint, cache.IncludeRemotes);
            }
        }

        if (walked.DecosFresh && walked.Fingerprint != 0)
        {
            lock (_sync)
            {
                if (_decoByPath.Count >= DecoCacheCap && !_decoByPath.ContainsKey(repoPath))
                    _decoByPath.Remove(_decoByPath.Keys.First());
                _decoByPath[repoPath] = new DecoCache(walked.Fingerprint, walked.Decorations);
            }
        }

        // Persist a freshly-walked window for the next launch (best-effort, off the gate). Only
        // the bounded foreground walks do this — a background spill chunk's rows already reach
        // disk via the §7.6 MetaStore, and snapshotting cache.Commits per chunk would be O(n²).
        if (!background && !walked.FromDisk && walked.Fingerprint != 0 && GraphWalkCache.Supports(query))
        {
            List<RawCommit> commits;
            Dictionary<ObjectId, IReadOnlyList<GraphDecorationInput>> decos;
            bool exhausted;
            long fp;
            lock (cache)
            {
                commits = [.. cache.Commits];
                decos = cache.Decorations;
                exhausted = cache.Exhausted;
                fp = cache.Fingerprint;
            }
            _ = Task.Run(() => _walkCache.Save(repoPath, query, fp, exhausted, commits, decos));
        }
    }

    /// <summary>Pull up to <paramref name="want"/> commits from an already-positioned revwalk
    /// enumerator into <paramref name="into"/>, discarding the first <paramref name="skip"/>
    /// still-unconsumed rows (used once, when a partial on-disk cache seeded
    /// <c>cache.Commits</c> and the fresh walk has to re-skip them). Returns true when the walk
    /// ran out. Cooperative: a cancelled <paramref name="ct"/> stops early and leaves the
    /// enumerator valid for the next call. Caller holds the repo gate.</summary>
    private static bool PumpCommits(
        IEnumerator<Commit> walk, List<RawCommit> into, int skip, int want, RepoCache cache, CancellationToken ct)
    {
        var collected = 0;
        while (collected < want)
        {
            if (ct.IsCancellationRequested)
                return false;
            if (!walk.MoveNext())
                return true;
            var pos = cache.LiveWalkPos++;
            if (pos < skip)
                continue;
            var commit = walk.Current;
            // Enumerate Parents once — each access re-parses the commit object in libgit2.
            var parentIds = commit.Parents.Select(p => p.Id).ToList();
            into.Add(new RawCommit(
                commit.Id,
                parentIds,
                new CommitMetadata
                {
                    Id = commit.Id,
                    MessageShort = commit.MessageShort,
                    AuthorName = commit.Author.Name,
                    AuthorEmail = commit.Author.Email,
                    AuthorWhen = commit.Author.When,
                    CommitWhen = commit.Committer.When,
                    ParentCount = parentIds.Count,
                }));
            collected++;
        }
        return false;
    }

    private readonly record struct WalkResult(
        List<RawCommit> Commits,
        Dictionary<ObjectId, IReadOnlyList<GraphDecorationInput>> Decorations,
        bool Exhausted,
        long Fingerprint,
        bool DecosFresh,
        bool FromDisk = false,
        DiskCommitStore? MetaStore = null,
        bool MetaExhausted = false);

    /// <summary>
    /// Stable (cross-process) fingerprint of everything that changes the walk output: every
    /// ref's name+target, HEAD's resolved target, and the query knobs (scope, remote-branch and
    /// stash inclusion, path filter, selected refs, and — only when stashes are in scope — the
    /// stash list). FNV-1a over sorted input, so it does not depend on ref enumeration order or
    /// per-process <see cref="string.GetHashCode()"/> randomisation.
    /// </summary>
    private static long ComputeFingerprint(Repository repo, GraphQuery query, out List<Reference> allRefs)
    {
        allRefs = repo.Refs.ToList();

        var h = StableHash.Fnv64OffsetBasis;
        void Feed(string s) => h = StableHash.Fnv1a64Step(h, s);

        foreach (var r in allRefs.OrderBy(x => x.CanonicalName, StringComparer.Ordinal))
        {
            Feed(r.CanonicalName);
            Feed(r.TargetIdentifier ?? "");
        }

        Feed("HEAD");
        Feed(repo.Refs.Head?.ResolveToDirectReference()?.TargetIdentifier ?? "");

        Feed("query");
        Feed(query.RefScope.ToString());
        Feed(query.IncludeRemoteBranches ? "R1" : "R0");
        Feed(query.IncludeStashes ? "S1" : "S0");
        Feed(query.PathFilter ?? "");
        if (query.RefScope == GraphRefScope.Selected && query.SelectedRefs is { } sel)
            foreach (var s in sel.OrderBy(x => x, StringComparer.Ordinal))
                Feed(s);

        if (query.IncludeStashes)
        {
            var n = 0;
            foreach (var st in repo.Stashes) { Feed(st.WorkTree?.Sha ?? ""); n++; }
            Feed("n" + n);
        }

        return unchecked((long)h);
    }

    /// <summary>Primes a fresh topological revwalk (the expensive tip-priority setup on a large
    /// repo), stores its enumerator on <paramref name="cache"/> so later Extends / spill chunks
    /// continue it instead of re-priming, and pulls the first <c>wantCount - have</c> rows.
    /// <paramref name="have"/> rows are re-skipped when a partial on-disk cache already seeded
    /// <c>cache.Commits</c>. Caller holds the repo gate.</summary>
    private static WalkResult StartWalk(
        Repository repo, GraphQuery query, int wantCount, int have, DecoCache? decoCache,
        List<Reference> allRefs, long fingerprint, RepoCache cache, CancellationToken ct)
    {
        // A ref whose target object is missing (a corrupt packed-refs, a half-deleted branch)
        // makes repo.Commits.QueryBy throw and blanks the whole graph — drop it from the tips.
        static bool Resolvable(Reference? r) => r?.ResolveToDirectReference() is not null;

        List<object> tipList = query.RefScope switch
        {
            GraphRefScope.CurrentBranch => repo.Info.IsHeadUnborn ? [] : [repo.Head],
            GraphRefScope.Selected => (query.SelectedRefs ?? [])
                .Select(r => repo.Refs[r]).Where(Resolvable).Cast<object>().ToList(),
            _ => allRefs
                .Where(r => (r.IsLocalBranch || (query.IncludeRemoteBranches && r.IsRemoteTrackingBranch) || r.IsTag)
                            && Resolvable(r))
                .Cast<object>().ToList(),
        };

        Dictionary<ObjectId, IReadOnlyList<GraphDecorationInput>> decorations;
        bool decosFresh;
        if (decoCache is not null && fingerprint != 0 && decoCache.Fingerprint == fingerprint)
        {
            decorations = decoCache.Decos;
            decosFresh = false;
        }
        else
        {
            using (Diagnostics.PerfLog.Scope("BuildDecorations"))
                decorations = BuildDecorations(repo, query, allRefs);
            decosFresh = true;
        }

        // §V2-17.3: stashes are reachable roots only when the flag is set.
        if (query.IncludeStashes)
            foreach (var stash in repo.Stashes)
                if (stash.WorkTree is not null)
                    tipList.Add(stash.WorkTree);

        object[] tips = [.. tipList];
        Diagnostics.PerfLog.Mark($"Walk tips resolved: {tips.Length}, decosFresh={decosFresh}");

        var result = new List<RawCommit>(Math.Min(Math.Max(wantCount - have, 0), 8192));
        if (tips.Length == 0)
            return new WalkResult(result, decorations, true, fingerprint, decosFresh);

        var filter = new CommitFilter
        {
            SortBy = CommitSortStrategies.Topological | CommitSortStrategies.Time,
            IncludeReachableFrom = tips,
        };

        // §V2-17.3: a pathspec-limited history is a real Git concept with its own valid
        // topology — restrict the walk rather than filter the finished graph.
        var walk = string.IsNullOrWhiteSpace(query.PathFilter)
            ? repo.Commits.QueryBy(filter)
            : repo.Commits.QueryBy(query.PathFilter, filter).Select(e => e.Commit);

        // Keep the enumerator alive on the cache — the first MoveNext() below pays the topo
        // tip-priority prime; every later Extend / silent-crawl / spill chunk resumes from here.
        var walkEnum = walk.GetEnumerator();
        cache.LiveWalk = walkEnum;
        cache.LiveWalkFingerprint = fingerprint;
        cache.LiveWalkPos = 0;

        using var walkTimer = Diagnostics.PerfLog.Scope($"Walk loop (sort={filter.SortBy})");
        var exhausted = PumpCommits(walkEnum, result, skip: have, want: wantCount - have, cache, ct);
        if (exhausted)
            cache.DisposeLiveWalk();
        Diagnostics.PerfLog.Mark($"Walk loop read {result.Count} commits (pos {cache.LiveWalkPos}, exhausted={exhausted})");

        return new WalkResult(result, decorations, exhausted, fingerprint, decosFresh);
    }

    private enum Family { Branch, Tag, Stash }

    private sealed class BadgeBuilder
    {
        public required Family Family;
        public required string Label;
        public bool IsLocal;
        public bool IsHead;
        public readonly List<string> Remotes = [];
        public int Order; // 0 local, 1 remote-only, 2 tag, 3 stash
    }

    /// <summary>
    /// Builds the per-commit decoration list, folding a branch that exists both locally and on
    /// one or more remotes into a single badge (§V3-7.3). Works straight off the raw
    /// <paramref name="allRefs"/> — branch tips are read from the ref's own target id with no
    /// object load; only tags are peeled. The remote prefix is stripped by matching the
    /// configured remote names (longest first), falling back to the first path segment only for
    /// an orphan remote-tracking ref whose remote no longer exists.
    /// </summary>
    private static Dictionary<ObjectId, IReadOnlyList<GraphDecorationInput>> BuildDecorations(
        Repository repo, GraphQuery query, IReadOnlyList<Reference> allRefs)
    {
        // commit id → ((family, shortName) → builder)
        var map = new Dictionary<ObjectId, Dictionary<(Family, string), BadgeBuilder>>();

        BadgeBuilder Get(ObjectId id, Family family, string label)
        {
            if (!map.TryGetValue(id, out var byKey))
                map[id] = byKey = [];
            var key = (family, label);
            if (!byKey.TryGetValue(key, out var b))
                byKey[key] = b = new BadgeBuilder { Family = family, Label = label };
            return b;
        }

        static ObjectId? DirectTargetId(Reference r)
        {
            var sha = r.ResolveToDirectReference()?.TargetIdentifier;
            return sha is { Length: 40 } ? new ObjectId(sha) : null;
        }

        string[] remoteNames;
        try { remoteNames = repo.Network.Remotes.Select(rm => rm.Name).OrderByDescending(n => n.Length).ToArray(); }
        catch { remoteNames = []; }

        foreach (var r in allRefs)
        {
            var name = r.CanonicalName;

            if (name.StartsWith("refs/heads/", StringComparison.Ordinal))
            {
                if (DirectTargetId(r) is { } tip)
                    Get(tip, Family.Branch, name["refs/heads/".Length..]).IsLocal = true;
            }
            else if (name.StartsWith("refs/remotes/", StringComparison.Ordinal))
            {
                if (!query.IncludeRemoteBranches)
                    continue;
                var rest = name["refs/remotes/".Length..];
                var remote = Array.Find(remoteNames, rn => rest.StartsWith(rn + "/", StringComparison.Ordinal));
                string shortName;
                if (remote is not null)
                {
                    shortName = rest[(remote.Length + 1)..];
                }
                else
                {
                    var slash = rest.IndexOf('/');
                    if (slash < 0)
                        continue;
                    remote = rest[..slash];
                    shortName = rest[(slash + 1)..];
                }
                // refs/remotes/<remote>/HEAD is a symbolic pointer to the remote's default
                // branch, not a branch of its own — never a badge.
                if (string.Equals(shortName, "HEAD", StringComparison.Ordinal))
                    continue;
                if (DirectTargetId(r) is not { } tip)
                    continue;
                var b = Get(tip, Family.Branch, shortName);
                if (!b.Remotes.Contains(remote))
                    b.Remotes.Add(remote);
            }
            else if (name.StartsWith("refs/tags/", StringComparison.Ordinal))
            {
                if (DirectTargetId(r) is not { } immediate)
                    continue;
                var id = repo.Lookup<Commit>(immediate)?.Id ?? immediate;
                Get(id, Family.Tag, name["refs/tags/".Length..]);
            }
        }

        if (query.IncludeStashes)
        {
            var i = 0;
            foreach (var stash in repo.Stashes)
            {
                var id = stash.WorkTree?.Id;
                if (id is not null)
                    Get(id, Family.Stash, $"stash@{{{i}}}");
                i++;
            }
        }

        // HEAD folds into its branch badge; a standalone HEAD badge only when detached (§V3-7.3).
        var headRef = repo.Refs.Head;
        if (!repo.Info.IsHeadUnborn && headRef is not null && DirectTargetId(headRef) is { } headTip)
        {
            if (repo.Info.IsHeadDetached)
                Get(headTip, Family.Branch, "HEAD").IsHead = true;
            else
            {
                var t = headRef.TargetIdentifier;
                var shortName = t is not null && t.StartsWith("refs/heads/", StringComparison.Ordinal)
                    ? t["refs/heads/".Length..]
                    : t ?? "HEAD";
                Get(headTip, Family.Branch, shortName).IsHead = true;
            }
        }

        var defaultBranch = ResolveDefaultBranchShortName(repo);
        return map.ToDictionary(
            kv => kv.Key,
            kv => BuildInputs(kv.Value.Values, defaultBranch));
    }

    /// <summary>
    /// §V3-B ordering source: the branch whose badge sorts first when several sit on one commit.
    /// Precedence: the currently checked-out branch, then <c>origin/HEAD</c>'s target, then a
    /// branch literally named <c>main</c>, then <c>master</c>, else none.
    /// </summary>
    private static string? ResolveDefaultBranchShortName(Repository repo)
    {
        if (!repo.Info.IsHeadUnborn && !repo.Info.IsHeadDetached)
        {
            var t = repo.Refs.Head?.TargetIdentifier;
            return t is not null && t.StartsWith("refs/heads/", StringComparison.Ordinal)
                ? t["refs/heads/".Length..]
                : t;
        }

        try
        {
            if (repo.Refs["refs/remotes/origin/HEAD"] is SymbolicReference sym && sym.TargetIdentifier is { } t)
            {
                const string prefix = "refs/remotes/";
                var s = t.StartsWith(prefix, StringComparison.Ordinal) ? t[prefix.Length..] : t;
                var slash = s.IndexOf('/');
                return slash >= 0 ? s[(slash + 1)..] : s;
            }
        }
        catch { /* no origin/HEAD */ }

        if (repo.Refs["refs/heads/main"] is not null) return "main";
        if (repo.Refs["refs/heads/master"] is not null) return "master";
        return null;
    }

    /// <summary>
    /// §V3-B: as soon as a commit carries at least one branch, that branch's badge stands in for
    /// every ref on the commit — any further branches (default‑first, then alphabetical) and
    /// then any tags ride along in <c>Siblings</c>, shown in the badge's interactive hover list
    /// and folded into its <c>+N</c> counter. Stashes and a detached HEAD keep their own badges;
    /// a commit with no branch at all leaves tags/stashes as their own badges.
    /// </summary>
    private static bool IsDetachedHead(BadgeBuilder b) =>
        b.Family == Family.Branch && b.IsHead && !b.IsLocal && b.Remotes.Count == 0;

    private static IReadOnlyList<GraphDecorationInput> BuildInputs(IEnumerable<BadgeBuilder> badges, string? defaultBranch)
    {
        var all = badges.ToList();
        // A standalone detached-HEAD badge is never part of a collapsible branch group.
        var branches = all.Where(b => b.Family == Family.Branch && !IsDetachedHead(b)).ToList();

        // No branch to fold into → tags / stashes keep their own badges.
        if (branches.Count == 0)
            return Order(all).Select(b => ToInput(b)).ToList();

        // Stashes and a detached HEAD are still drawn as their own badges.
        var separate = all.Where(b => b.Family == Family.Stash || IsDetachedHead(b))
            .OrderBy(b => IsDetachedHead(b) ? 0 : 1)
            .ThenBy(b => b.Label, StringComparer.Ordinal)
            .Select(b => ToInput(b));

        branches.Sort((x, y) =>
        {
            var xd = string.Equals(x.Label, defaultBranch, StringComparison.Ordinal) ? 0 : 1;
            var yd = string.Equals(y.Label, defaultBranch, StringComparison.Ordinal) ? 0 : 1;
            return xd != yd ? xd - yd : string.CompareOrdinal(x.Label, y.Label);
        });

        // Siblings: the further branches (default-first / alphabetical), then the tags. Empty
        // when there is a single branch and no tags — then the head badge folds nothing.
        var tags = all.Where(b => b.Family == Family.Tag)
            .OrderBy(b => b.Label, StringComparer.Ordinal);
        var siblings = branches.Skip(1).Concat(tags).Select(b => ToInput(b)).ToArray();

        var head = ToInput(branches[0], siblings);
        return new[] { head }.Concat(separate).ToList();
    }

    private static IEnumerable<BadgeBuilder> Order(IEnumerable<BadgeBuilder> badges)
    {
        foreach (var b in badges)
            b.Order = b.Family switch
            {
                Family.Tag => 2,
                Family.Stash => 3,
                _ => b.IsLocal ? 0 : 1,
            };
        return badges.OrderBy(b => b.Order).ThenBy(b => b.Label, StringComparer.Ordinal);
    }

    private static GraphDecorationInput ToInput(BadgeBuilder b, GraphDecorationInput[]? siblings = null)
    {
        var type = b.Family switch
        {
            Family.Tag => DecorationType.Tag,
            Family.Stash => DecorationType.Stash,
            _ when b.IsHead && !b.IsLocal && b.Remotes.Count == 0 => DecorationType.Head, // detached HEAD
            _ => b.IsLocal ? DecorationType.LocalBranch : DecorationType.RemoteBranch,
        };
        return new GraphDecorationInput(
            type,
            b.Label,
            IsLocal: b.IsLocal,
            Remotes: b.Remotes.Count > 0 ? b.Remotes.ToArray() : null,
            IsHead: b.IsHead,
            Siblings: siblings is { Length: > 0 } ? siblings : null);
    }
}
