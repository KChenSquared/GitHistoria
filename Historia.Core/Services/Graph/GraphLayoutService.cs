namespace Historia.Services.Graph;

/// <inheritdoc cref="IGraphLayoutService"/>
public sealed class GraphLayoutService : IGraphLayoutService
{
    private const int WalkOverscan = 2_000;

    /// <summary>Stuck-guard for <see cref="MaybeStartBackgroundSpill"/>: how long the background
    /// full-history walk waits for this tab to fully load (<see cref="RepositoryContext.FirstGraphPaint"/>
    /// + <see cref="RepositoryContext.FirstFullLoad"/>) before it starts anyway. In the running
    /// app both signals always complete; this only bites a headless open where nothing raises
    /// them — deliberately long so a slow-but-progressing large repo is never cut short.</summary>
    private static readonly TimeSpan SpillStartGrace = TimeSpan.FromSeconds(180);

    /// <summary>Rows per chunk of the background full-history walk (§7.6). Each chunk is a
    /// separate low-priority repo-gate acquisition that *resumes* the primed revwalk (cheap —
    /// no tip reseed), so this bounds how long a foreground caller that has already started
    /// waiting can be stuck behind the spill to roughly one chunk's pull.</summary>
    private const int SpillWalkChunkRows = 25_000;

    /// <summary>Minimum row-count growth between debounced <see cref="GraphWalkCache.Save"/>
    /// calls for the same cache (see <see cref="RepoCache.WalkCacheSavedRows"/>).</summary>
    private const int WalkCacheSaveStride = 5_000;

    private readonly ISettingsService? _settings;
    private readonly IGitExecutableLocator? _locator;
    private readonly CommitGraphMaintenance? _commitGraphMaintenance;

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

    /// <summary>DI ctor (§3 linux cold-prime plan): with a working <see cref="IGitExecutableLocator"/>
    /// the CLI streaming walk (<see cref="GitLogStream"/>) becomes the primary engine for the
    /// plain all-refs query; the parameterless / single-arg test ctors above always keep the
    /// libgit2 path, since <see cref="_locator"/> stays null.</summary>
    public GraphLayoutService(ISettingsService settings, IGitExecutableLocator locator) : this(settings)
    {
        _locator = locator;
    }

    /// <summary>DI ctor (§4 linux cold-prime plan): additionally wires up
    /// <see cref="CommitGraphMaintenance"/>, scheduled once a repo's background history spill
    /// (§7.6) reaches completion. The 2-arg ctor above keeps working with no commit-graph
    /// maintenance — used by tests that don't care about it.</summary>
    public GraphLayoutService(ISettingsService settings, IGitExecutableLocator locator, IGitCliService cli)
        : this(settings, locator)
    {
        _commitGraphMaintenance = new CommitGraphMaintenance(cli, settings);
    }

    /// <summary>The engine a FRESH prime would use right now — decidable with no repo access
    /// (locator + query only), so it can gate a disk-cache probe before the repo gate is even
    /// taken, and gate the optimistic (pre-fingerprint) snapshot path too.</summary>
    private WalkEngine ExpectedEngine(GraphQuery query) =>
        _locator is { Current.Found: true } && GraphWalkCache.Supports(query) ? WalkEngine.Cli : WalkEngine.LibGit2;

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

        // §V10.1.2: row count as of the last queued GraphWalkCache.Save — debounces the .gz
        // rewrite so a long run of small foreground extends (the silent crawler) doesn't
        // re-snapshot + re-gzip the whole window on every one.
        public int WalkCacheSavedRows { get; set; }

        // ── resumable walk (v10.1.2, streaming CLI engine since v10.1.3) ──
        // A live, primed walk enumerator (the multi-second tip-priority cost on a big repo, or —
        // for the CLI engine — the process spawn) pulled forward in chunks by EnsureWalkedAsync —
        // the quick phase, each scroll Extend, the silent crawler and the §7.6 background spill
        // all continue this ONE walk instead of restarting from the tips every time. Two engines:
        // a libgit2 revwalk (wrapped as RawCommit by LibGit2RawCommitEnumerator) which is
        // libgit2-backed and so ONLY ever touched inside RepositoryContext's gate, and a
        // GitLogStream (a live `git log` process) which is pulled OFF the gate — see
        // GraphLayoutService.EnsureWalkedAsync. Engine records which one is currently live so a
        // resume knows whether it needs the gate. Disposed on reset / Forget / exhaustion / a
        // fingerprint change.
        public IEnumerator<RawCommit>? LiveWalk { get; set; }
        public long LiveWalkFingerprint { get; set; }
        public int LiveWalkPos { get; set; } // rows already pulled from LiveWalk
        public WalkEngine Engine { get; set; }

        // Serializes a CLI-engine prime/resume pull (which runs off the repo gate) against a
        // concurrent one — e.g. a foreground Extend racing the §7.6 background spill's next
        // chunk. The libgit2 engine doesn't need this: it's already serialized by the repo gate.
        public readonly SemaphoreSlim WalkLock = new(1, 1);

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

        // §V10.1.2: try the §7.6 metadata store first. Unlike the .gz walk cache below, it is
        // written incrementally to the FULL history by the background spill, isn't bounded by
        // whatever a single foreground walk reached, and (since GraphMetaStore.Version 2) now
        // carries real parent SHAs — enough to lay out a genuine DAG frame, not just a row list.
        // Stale-tolerant like TryLoadStale: any fingerprint is fine, this frame is always
        // replaced by the verified walk a moment later. Ref badges are not in this store, so the
        // frame paints commits/lanes with no decorations for that one moment.
        if (GraphWalkCache.Supports(query))
        {
            var probe = GraphMetaStore.ProbeAny(context.RepositoryPath, query.IncludeRemoteBranches, ExpectedEngine(query));
            if (probe.Ok)
            {
                DiskCommitStore? store = null;
                try
                {
                    store = DiskCommitStore.Open(context.RepositoryPath, probe.RowCount);
                    var take = Math.Min(maxRows, store.Count);
                    if (take > 0)
                    {
                        var commits = new List<RawCommit>(take);
                        for (var i = 0; i < take; i++)
                            commits.Add(store.ReadRaw(i));
                        Diagnostics.PerfLog.Mark($"optimistic snapshot HIT (meta): {commits.Count} commits (stale, unverified)");
                        return _engine.Compute(commits, EmptyDecorations, maxRows, query.MaxLanes, CancellationToken.None, retainCommits: true);
                    }
                }
                catch { /* fall through to the .gz path — the meta store is an optimisation */ }
                finally { store?.Dispose(); }
            }
        }

        var loaded = _walkCache.TryLoadStale(context.RepositoryPath, query, metadataOnDisk: false);
        if (loaded is null)
            return null;

        Diagnostics.PerfLog.Mark($"optimistic snapshot HIT: {loaded.Commits.Count} commits (stale, unverified)");
        return _engine.Compute(loaded.Commits, loaded.Decorations, maxRows, query.MaxLanes, CancellationToken.None, retainCommits: true);
    }

    private static readonly Dictionary<ObjectId, IReadOnlyList<GraphDecorationInput>> EmptyDecorations = [];

    public async Task<GraphLayoutResult> ExtendLayoutAsync(
        RepositoryContext context, GraphLayoutResult existing, int additionalRows, CancellationToken ct = default,
        bool background = false)
    {
        RepoCache? cache;
        lock (_sync)
            _caches.TryGetValue(context, out cache);
        if (cache is null)
            return existing;

        var want = existing.State.Rows.Count + additionalRows + WalkOverscan;
        await EnsureWalkedAsync(context, new GraphQuery { RefScope = cache.Scope, IncludeRemoteBranches = cache.IncludeRemotes },
            cache, want, ct, background).ConfigureAwait(false);

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
                    && GraphMetaStore.TryOpen(repoPath, cache.Fingerprint, includeRemotes, rowCount, cache.Engine) is { } warm
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
                    cache.MetaStore = GraphMetaStore.TryOpen(repoPath, cache.Fingerprint, includeRemotes, rowCount, cache.Engine);

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
        cache.MetaWriter ??= new GraphMetaWriter(repoPath, cache.Fingerprint, includeRemotes, cache.Engine);
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
                // Hold off until this tab is fully loaded — first paint on screen AND its
                // identity resolution + first RefreshAllAsync done (§12.1 FirstFullLoad). The
                // walk below still holds the repo gate in bursts and burns CPU; starting it
                // before the tab has settled would race the quick/extend paint and the tab's
                // own status/branch work. FirstFullLoad also gates the cross-tab init pump
                // (WorkspaceService), so by the time it completes the machine is no longer
                // fanning every restored tab's load out at once. SpillStartGrace is the
                // headless/no-signal safety net.
                await Task.WhenAny(
                    Task.WhenAll(context.FirstGraphPaint, context.FirstFullLoad),
                    Task.Delay(SpillStartGrace, ct)).ConfigureAwait(false);
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
                    await EnsureWalkedAsync(context, query, cache, target, ct, background: true, lowestPriority: true)
                        .ConfigureAwait(false);
                    bool walkDone;
                    lock (cache) walkDone = cache.Exhausted || cache.Commits.Count >= cap;
                    if (walkDone)
                        break;
                    await Task.Delay(150, ct).ConfigureAwait(false); // let foreground + crawl gate work in
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

                // §4: the whole history has now been walked AND spilled to disk — the point at
                // which a large repo without a commit-graph file benefits from one. Not gated on
                // ct — a tab close cancels the spill loop above but the repo is still worth
                // maintaining for the next open.
                if (cache.Exhausted && cache.MetaComplete)
                {
                    int walked;
                    lock (cache) walked = cache.Commits.Count;
                    _commitGraphMaintenance?.MaybeSchedule(context, walked);
                }
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
        bool background = false, bool lowestPriority = false)
    {
        if (cache.Exhausted || cache.Commits.Count >= wantCount)
            return;

        DecoCache? deco;
        lock (_sync)
            _decoByPath.TryGetValue(context.RepositoryPath, out deco);

        Diagnostics.PerfLog.Mark($"EnsureWalked need {wantCount}, have {cache.Commits.Count}, decoFp={deco?.Fingerprint ?? 0}");
        var repoPath = context.RepositoryPath;
        var expectedEngine = ExpectedEngine(query);
        // §V10.1.2: three gate tiers, not two — a genuine foreground caller always wins; the
        // commit-graph's own silent crawl (background, but user-visible — it grows the
        // scrollbar) comes next; the §7.6 full-history disk spill (background, no urgency of its
        // own) is lowestPriority and stands aside for either of the other two so it can never
        // starve the crawl the way it used to when both were the same tier.
        Task<GatePrep> WithRepo(Func<Repository, GatePrep> work) => (background, lowestPriority) switch
        {
            (true, true) => context.WithRepositorySpillAsync(work, ct),
            (true, false) => context.WithRepositoryBackgroundAsync(work, ct),
            _ => context.WithRepositoryAsync(work, ct),
        };

        // Step 1 — on the repo gate: fingerprint, the on-disk cache probe, and (only for the
        // libgit2 engine) the actual walk pull. For the CLI engine this deliberately stops short
        // of spawning/pulling the process — tip resolution + BuildDecorations here cost ms even
        // on linux; the walk itself is what must never sit inside the gate (§3).
        var prep = await WithRepo(repo =>
            PrepareWalk(repo, repoPath, query, wantCount, deco, cache, _metaFromDisk, _walkCache, expectedEngine, ct))
            .ConfigureAwait(false);

        WalkResult walked = prep.Kind switch
        {
            GatePrepKind.PrimeCli => await PrimeCliWalkAsync(context, cache, query, deco, prep, ct).ConfigureAwait(false),
            GatePrepKind.ResumeCli => await ResumeCliWalkAsync(cache, prep, ct).ConfigureAwait(false),
            _ => prep.Result,
        };

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
                cache.MetaWriter ??= new GraphMetaWriter(repoPath, cache.Fingerprint, cache.IncludeRemotes, cache.Engine);
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
        // Debounced: the silent crawler drives many small extends in a row, and re-snapshotting
        // + re-gzipping the whole window on every one is pure waste (and a kill mid-write can
        // leave a bad .gz). Save on exhaustion, the first fill, or every WalkCacheSaveStride rows.
        if (!background && !walked.FromDisk && walked.Fingerprint != 0 && GraphWalkCache.Supports(query))
        {
            List<RawCommit> commits;
            Dictionary<ObjectId, IReadOnlyList<GraphDecorationInput>> decos;
            bool exhausted;
            long fp;
            WalkEngine engine;
            var save = false;
            lock (cache)
            {
                exhausted = cache.Exhausted;
                var rows = cache.Commits.Count;
                if (exhausted || cache.WalkCacheSavedRows == 0 || rows - cache.WalkCacheSavedRows >= WalkCacheSaveStride)
                {
                    cache.WalkCacheSavedRows = rows;
                    save = true;
                }
                commits = save ? [.. cache.Commits] : [];
                decos = cache.Decorations;
                fp = cache.Fingerprint;
                engine = cache.Engine;
            }
            if (save)
                _ = Task.Run(() => _walkCache.Save(repoPath, query, fp, exhausted, commits, decos, engine));
        }
    }

    private enum GatePrepKind { Done, PrimeCli, ResumeCli }

    /// <summary>Result of the on-gate half of a walk step. <see cref="GatePrepKind.Done"/> carries
    /// a finished <see cref="WalkResult"/> (the libgit2 engine, or a disk-cache hit); the two CLI
    /// cases hand back just enough (fingerprint, row counts, tips, decorations) for the caller to
    /// finish the work OFF the gate.</summary>
    private readonly record struct GatePrep(
        GatePrepKind Kind,
        WalkResult Result,
        long Fingerprint,
        int Have,
        int WantCount,
        string[]? Tips,
        Dictionary<ObjectId, IReadOnlyList<GraphDecorationInput>> Decorations,
        bool DecosFresh)
    {
        public static GatePrep Done(WalkResult result) =>
            new(GatePrepKind.Done, result, result.Fingerprint, 0, 0, null, result.Decorations, result.DecosFresh);

        public static GatePrep ForResumeCli(long fp, int have, int want, Dictionary<ObjectId, IReadOnlyList<GraphDecorationInput>> decorations) =>
            new(GatePrepKind.ResumeCli, default, fp, have, want, null, decorations, false);

        public static GatePrep ForPrimeCli(
            long fp, int have, int want, string[] tips,
            Dictionary<ObjectId, IReadOnlyList<GraphDecorationInput>> decorations, bool decosFresh) =>
            new(GatePrepKind.PrimeCli, default, fp, have, want, tips, decorations, decosFresh);
    }

    /// <summary>Caller holds the repo gate. Computes the fingerprint, tries the on-disk caches,
    /// resumes an already-primed libgit2 walk, or — when nothing is usable — either starts a
    /// fresh libgit2 walk (engine = LibGit2) or resolves tips/decorations for a CLI prime the
    /// caller spawns off the gate (engine = Cli). Never spawns or pulls a <see cref="GitLogStream"/>
    /// itself.</summary>
    private static GatePrep PrepareWalk(
        Repository repo, string repoPath, GraphQuery query, int wantCount, DecoCache? decoCache,
        RepoCache cache, bool metaFromDisk, GraphWalkCache walkCache, WalkEngine expectedEngine, CancellationToken ct)
    {
        var fp = ComputeFingerprint(repo, query, out var allRefs);
        // Read fresh on the gate — a concurrent EnsureWalkedAsync (foreground extend vs the
        // background spill) serialises here and may have appended since the outer check.
        var have = cache.Commits.Count;
        if (have >= wantCount || cache.Exhausted)
            return GatePrep.Done(new WalkResult([], cache.Decorations, cache.Exhausted, fp, DecosFresh: false));

        // Fresh cache, nothing primed: try the on-disk walk before touching either engine at all.
        // When the paged metadata store opens for this fingerprint, load id + parents only — the
        // strings come from the store, not a resident copy of the whole history.
        if (have == 0 && cache.LiveWalk is null)
        {
            var probe = metaFromDisk
                ? GraphMetaStore.Probe(repoPath, fp, query.IncludeRemoteBranches, expectedEngine)
                : default;
            var store = probe.Ok
                ? GraphMetaStore.TryOpen(repoPath, fp, query.IncludeRemoteBranches, wantCount, expectedEngine)
                : null;
            // Load id/parents only when the store already holds the WHOLE history — otherwise
            // the background spill still needs real metadata in cache.Commits to fill the gap.
            var lite = store is not null && probe.Exhausted;
            if (walkCache.TryLoad(repoPath, query, wantCount, fp, expectedEngine, metadataOnDisk: lite) is { } hit)
            {
                Diagnostics.PerfLog.Mark($"walk cache HIT: {hit.Commits.Count} commits, exhausted={hit.Exhausted}, lite={lite}");
                cache.Engine = expectedEngine;
                return GatePrep.Done(new WalkResult(hit.Commits, hit.Decorations, hit.Exhausted, fp, DecosFresh: true, FromDisk: true, MetaStore: store, MetaExhausted: probe.Exhausted));
            }
            store?.Dispose(); // TryLoad missed — don't leak the handle
        }

        // Continue the ONE primed walk for this fingerprint — no tip reseed, no BuildDecorations.
        // This is what makes an Extend / silent-crawl / spill chunk cheap on a big repo after the
        // first (unavoidably expensive) prime.
        if (cache.LiveWalk is { } live && cache.LiveWalkFingerprint == fp)
        {
            if (cache.Engine == WalkEngine.Cli)
                // Pulling from the git process is what must stay off the gate — hand it back.
                return GatePrep.ForResumeCli(fp, have, wantCount, cache.Decorations);

            var need = wantCount - have;
            var delta = new List<RawCommit>(Math.Min(need, 8192));
            var exhaustedNow = PumpCommits(live, delta, skip: 0, want: need, cache, ct);
            if (exhaustedNow)
                cache.DisposeLiveWalk();
            Diagnostics.PerfLog.Mark($"walk resumed +{delta.Count} (pos {cache.LiveWalkPos}, exhausted={exhaustedNow})");
            return GatePrep.Done(new WalkResult(delta, cache.Decorations, exhaustedNow, fp, DecosFresh: false));
        }

        // No usable primed walk (first walk, or the ref set changed): prime a fresh one.
        cache.DisposeLiveWalk();
        cache.Engine = expectedEngine;

        if (expectedEngine == WalkEngine.Cli)
        {
            var tips = ResolveTipShas(repo, query, allRefs);
            var (decorations, decosFresh) = ResolveDecorations(repo, query, allRefs, decoCache, fp);
            if (tips.Length == 0)
                return GatePrep.Done(new WalkResult([], decorations, true, fp, decosFresh));
            return GatePrep.ForPrimeCli(fp, have, wantCount, tips, decorations, decosFresh);
        }

        return GatePrep.Done(StartWalk(repo, query, wantCount, have, decoCache, allRefs, fp, cache, ct));
    }

    /// <summary>Spawns <see cref="GitLogStream"/> and pulls the first batch — entirely off the
    /// repo gate. A spawn failure, or a failure before the very first row, falls back to a fresh
    /// libgit2 <see cref="StartWalk"/> (which re-takes the gate).</summary>
    private async Task<WalkResult> PrimeCliWalkAsync(
        RepositoryContext context, RepoCache cache, GraphQuery query, DecoCache? deco, GatePrep prep, CancellationToken ct)
    {
        await cache.WalkLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var walkTimer = Diagnostics.PerfLog.Scope("Walk loop (cli)");
            var stream = _locator is not null
                ? GitLogStream.TryStart(_locator, context.RepositoryPath, prep.Tips!, context.ActiveProfile)
                : null;

            if (stream is null)
            {
                Diagnostics.PerfLog.Mark("GitLogStream spawn failed — falling back to libgit2 walk");
                return await FallbackToLibGit2Async(context, cache, query, deco, prep, ct).ConfigureAwait(false);
            }

            cache.LiveWalk = stream;
            cache.LiveWalkFingerprint = prep.Fingerprint;
            cache.LiveWalkPos = 0;

            var need = prep.WantCount - prep.Have;
            var delta = new List<RawCommit>(Math.Min(need, 8192));
            var exhausted = PumpCommits(stream, delta, skip: 0, want: need, cache, ct);

            if (delta.Count == 0 && !exhausted && stream.Failed)
            {
                cache.DisposeLiveWalk();
                Diagnostics.PerfLog.Mark("GitLogStream failed before yielding a row — falling back to libgit2 walk");
                return await FallbackToLibGit2Async(context, cache, query, deco, prep, ct).ConfigureAwait(false);
            }

            if (exhausted)
                cache.DisposeLiveWalk();

            Diagnostics.PerfLog.Mark($"Walk loop (cli) read {delta.Count} commits (pos {cache.LiveWalkPos}, exhausted={exhausted})");
            return new WalkResult(delta, prep.Decorations, exhausted, prep.Fingerprint, prep.DecosFresh);
        }
        finally
        {
            cache.WalkLock.Release();
        }
    }

    /// <summary>Resumes an already-primed <see cref="GitLogStream"/> — off the repo gate, so a
    /// scroll Extend or the §7.6 background spill's next chunk never queues behind status/diff
    /// work on this repo.</summary>
    private static async Task<WalkResult> ResumeCliWalkAsync(RepoCache cache, GatePrep prep, CancellationToken ct)
    {
        await cache.WalkLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // The walk may have been disposed/replaced between PrepareWalk's read and here (a
            // concurrent Forget/reset) — nothing to add this round; the caller's next attempt
            // re-evaluates from scratch.
            if (cache.LiveWalk is not { } live || cache.Engine != WalkEngine.Cli || cache.LiveWalkFingerprint != prep.Fingerprint)
                return new WalkResult([], prep.Decorations, cache.Exhausted, prep.Fingerprint, DecosFresh: false);

            var need = prep.WantCount - prep.Have;
            var delta = new List<RawCommit>(Math.Min(need, 8192));
            var exhausted = PumpCommits(live, delta, skip: 0, want: need, cache, ct);
            if (exhausted)
                cache.DisposeLiveWalk();
            Diagnostics.PerfLog.Mark($"walk resumed (cli) +{delta.Count} (pos {cache.LiveWalkPos}, exhausted={exhausted})");
            return new WalkResult(delta, cache.Decorations, exhausted, prep.Fingerprint, DecosFresh: false);
        }
        finally
        {
            cache.WalkLock.Release();
        }
    }

    /// <summary>Re-takes the repo gate and runs the libgit2 walk from scratch — the CLI engine's
    /// fallback when git is missing, the spawn fails, or the process fails before yielding a
    /// single row.</summary>
    private async Task<WalkResult> FallbackToLibGit2Async(
        RepositoryContext context, RepoCache cache, GraphQuery query, DecoCache? deco, GatePrep prep, CancellationToken ct)
    {
        cache.Engine = WalkEngine.LibGit2;
        return await context.WithRepositoryAsync(repo =>
        {
            var fp = ComputeFingerprint(repo, query, out var allRefs);
            cache.DisposeLiveWalk();
            return StartWalk(repo, query, prep.WantCount, prep.Have, deco, allRefs, fp, cache, ct);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Pull up to <paramref name="want"/> commits from an already-positioned walk
    /// enumerator into <paramref name="into"/>, discarding the first <paramref name="skip"/>
    /// still-unconsumed rows (used once, when a partial on-disk cache seeded
    /// <c>cache.Commits</c> and the fresh walk has to re-skip them). Returns true when the walk
    /// ran out. Cooperative: a cancelled <paramref name="ct"/> stops early and leaves the
    /// enumerator valid for the next call. Works for both engines — the libgit2 engine's
    /// enumerator is wrapped by <see cref="LibGit2RawCommitEnumerator"/> so both yield
    /// <see cref="RawCommit"/> directly; the caller must hold the repo gate for the libgit2
    /// engine, but not for a <see cref="GitLogStream"/>.</summary>
    private static bool PumpCommits(
        IEnumerator<RawCommit> walk, List<RawCommit> into, int skip, int want, RepoCache cache, CancellationToken ct)
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
            into.Add(walk.Current);
            collected++;
        }
        return false;
    }

    /// <summary>Adapts a libgit2 <c>Commit</c> revwalk enumerator to <see cref="RawCommit"/> so
    /// <see cref="PumpCommits"/> and <see cref="RepoCache.LiveWalk"/> need only ever know about
    /// one shape regardless of which engine is walking.</summary>
    private sealed class LibGit2RawCommitEnumerator(IEnumerator<Commit> inner) : IEnumerator<RawCommit>
    {
        public RawCommit Current { get; private set; }
        object IEnumerator.Current => Current;

        public bool MoveNext()
        {
            if (!inner.MoveNext())
                return false;
            var commit = inner.Current;
            // Enumerate Parents once — each access re-parses the commit object in libgit2.
            var parentIds = commit.Parents.Select(p => p.Id).ToList();
            Current = new RawCommit(
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
                });
            return true;
        }

        public void Reset() => inner.Reset();
        public void Dispose() => inner.Dispose();
    }

    /// <summary>Resolves the same tip set <see cref="StartWalk"/> would (local / remote / tag,
    /// filtered by <see cref="GraphQuery.RefScope"/> and <see cref="Resolvable"/>), peeled to
    /// commit SHAs for <c>git log --stdin</c>. A ref whose target doesn't peel to a commit (a
    /// lightweight or annotated tag pointing at a tree/blob, e.g. linux's <c>v2.6.11-tree</c>) is
    /// silently skipped, matching how <see cref="StartWalk"/>'s libgit2 walk treats it. Caller
    /// holds the repo gate.</summary>
    private static string[] ResolveTipShas(Repository repo, GraphQuery query, List<Reference> allRefs)
    {
        static bool Resolvable(Reference? r) => r?.ResolveToDirectReference() is not null;
        static Commit? SafeLookupCommit(Repository repo, string targetIdentifier)
        {
            try { return repo.Lookup<Commit>(targetIdentifier); }
            catch { return null; }
        }

        var shas = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Add(Commit? c) { if (c is not null && seen.Add(c.Sha)) shas.Add(c.Sha); }

        switch (query.RefScope)
        {
            case GraphRefScope.CurrentBranch:
                if (!repo.Info.IsHeadUnborn)
                    Add(repo.Head.Tip);
                break;

            case GraphRefScope.Selected:
                foreach (var name in query.SelectedRefs ?? [])
                {
                    var r = repo.Refs[name];
                    if (!Resolvable(r))
                        continue;
                    var target = r!.ResolveToDirectReference()!.TargetIdentifier;
                    if (target is not null)
                        Add(SafeLookupCommit(repo, target));
                }
                break;

            default:
                foreach (var r in allRefs)
                {
                    if (!(r.IsLocalBranch || (query.IncludeRemoteBranches && r.IsRemoteTrackingBranch) || r.IsTag) || !Resolvable(r))
                        continue;
                    var target = r.ResolveToDirectReference()!.TargetIdentifier;
                    if (target is not null)
                        Add(SafeLookupCommit(repo, target));
                }
                break;
        }

        return [.. shas];
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

    /// <summary>Shared by <see cref="StartWalk"/> (libgit2) and <see cref="PrepareWalk"/> (CLI
    /// prime): the decoration cache hit/rebuild decision is identical for both engines — the
    /// walk row order never affects which refs decorate which commit.</summary>
    private static (Dictionary<ObjectId, IReadOnlyList<GraphDecorationInput>> Decorations, bool Fresh) ResolveDecorations(
        Repository repo, GraphQuery query, List<Reference> allRefs, DecoCache? decoCache, long fingerprint)
    {
        if (decoCache is not null && fingerprint != 0 && decoCache.Fingerprint == fingerprint)
            return (decoCache.Decos, false);

        Dictionary<ObjectId, IReadOnlyList<GraphDecorationInput>> decorations;
        using (Diagnostics.PerfLog.Scope("BuildDecorations"))
            decorations = BuildDecorations(repo, query, allRefs);
        return (decorations, true);
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

        var (decorations, decosFresh) = ResolveDecorations(repo, query, allRefs, decoCache, fingerprint);

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
        var walkEnum = new LibGit2RawCommitEnumerator(walk.GetEnumerator());
        cache.LiveWalk = walkEnum;
        cache.LiveWalkFingerprint = fingerprint;
        cache.LiveWalkPos = 0;
        cache.Engine = WalkEngine.LibGit2;

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
