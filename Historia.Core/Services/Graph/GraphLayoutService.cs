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

    /// <summary>Rows per chunk of the background full-history walk. Each chunk *resumes*
    /// the primed <c>git log</c> walk (cheap — no respawn), off the repo gate while no ref file
    /// has changed and otherwise after one low-priority gate turn, so this bounds how long a
    /// foreground extend can wait behind the spill to roughly one chunk's pull.</summary>
    private const int SpillWalkChunkRows = 25_000;

    /// <summary>Rows per batch of the disk-spill write, both for the active tab's background
    /// spill and the headless per-tab pipeline. See <see cref="TestSpillBatchRowsOverride"/>.</summary>
    private const int DefaultSpillBatchRows = 25_000;

    /// <summary>Test seam: when &gt; 0, overrides <see cref="DefaultSpillBatchRows"/>
    /// for both the active-tab background spill and the headless spool — so a fixture repo with
    /// only a few hundred commits can still exercise multi-batch write races that would otherwise
    /// need a 25,000-row history to trigger. Left at 0 (production default) outside tests; a test
    /// that sets it must reset it to 0 when done, since it's process-wide state.</summary>
    private static readonly AsyncLocal<int> _testSpillBatchRowsOverride = new();
    internal static int TestSpillBatchRowsOverride
    {
        get => _testSpillBatchRowsOverride.Value;
        set => _testSpillBatchRowsOverride.Value = value;
    }

    /// <summary>Test seam: invoked once per streamed batch during
    /// <see cref="RunHeadlessReorderAsync"/> with that batch's own row count (never a running
    /// total) — lets a test assert resident memory during a reorder never exceeds one batch. No-op
    /// (left null) outside tests.</summary>
    internal static Action<int>? TestReorderBatchObserved;

    /// <summary>Test seams: override <see cref="GraphMetaSplice.MinRowsToConsider"/>
    /// / <see cref="GraphMetaSplice.MaxOldParentRow"/> for <see cref="TryHeadlessSpliceAsync"/> —
    /// a real 200,000-row history isn't practical to build in a fixture test, so a test lowers
    /// these to drive the live splice trigger at small scale. <c>null</c> (production default)
    /// outside tests; a test that sets either must reset it to <c>null</c> when done, since both
    /// are process-wide state.</summary>
    private static readonly AsyncLocal<int?> _testSpliceMinRowsOverride = new();
    internal static int? TestSpliceMinRowsOverride
    {
        get => _testSpliceMinRowsOverride.Value;
        set => _testSpliceMinRowsOverride.Value = value;
    }
    internal static int? TestSpliceMaxOldParentRowOverride;

    /// <summary>Test seam: substitutes for the header's real <c>LastFullWalkMs</c>
    /// (guard 4's baseline) in <see cref="TryHeadlessSpliceAsync"/>. A tiny fixture repo's own
    /// walk-from-row-0 time is itself only milliseconds, so the 25%-of-it budget guard 4 computes
    /// from it is too tight to reliably clear real process-spawn variance on a test machine — this
    /// lets a wiring test supply a realistic (large) baseline instead. <c>null</c> outside tests.</summary>
    private static readonly AsyncLocal<TimeSpan?> _testSpliceFullWalkElapsedOverride = new();
    internal static TimeSpan? TestSpliceFullWalkElapsedOverride
    {
        get => _testSpliceFullWalkElapsedOverride.Value;
        set => _testSpliceFullWalkElapsedOverride.Value = value;
    }

    /// <summary>Test seam: invoked with <c>(addedRecords, removedRecords)</c> right
    /// after a splice actually commits — since a spliced header looks identical to a reorder's
    /// (both produce <c>OrderVersion &gt; 0</c> with the right row/record counts), a test needs
    /// this to tell "the splice path ran" apart from "it fell back to the ordinary reorder". Null
    /// outside tests.</summary>
    internal static Action<int, int>? TestSpliceCommitted;

    /// <summary>Test seam: how many full ref scans (fingerprint + tip resolution) have run per
    /// repository path. A scan costs seconds on a repo with tens of thousands of refs, so tests
    /// assert on the delta across a call to prove a satisfied or resumed walk step skipped it.
    /// Keyed by <see cref="RepositoryContext.RepositoryPath"/> so parallel test classes on other
    /// fixtures never disturb the count.</summary>
    private static readonly ConcurrentDictionary<string, int> _refScanCounts = new(StringComparer.OrdinalIgnoreCase);

    internal static int RefScanCountForTesting(string repositoryPath) =>
        _refScanCounts.TryGetValue(repositoryPath, out var n) ? n : 0;

    private static void CountRefScan(string repositoryPath) =>
        _refScanCounts.AddOrUpdate(repositoryPath, 1, (_, n) => n + 1);

    /// <summary>Minimum row-count growth between debounced <see cref="GraphWalkCache.Save"/>
    /// calls for the same cache (see <see cref="RepoCache.WalkCacheSavedRows"/>).</summary>
    private const int WalkCacheSaveStride = 5_000;

    private readonly ISettingsService _settings;
    private readonly IGitExecutableLocator _locator;
    private readonly GraphBackgroundWriter? _backgroundWriter;

    /// <summary>Stored (not just forwarded to <see cref="GraphBackgroundWriter"/>)
    /// so <see cref="TryHeadlessSpliceAsync"/> can run its own <c>git rev-list --stdin</c> delta
    /// query directly. Null in the 2-arg ctor.</summary>
    private readonly IGitCliService? _cli;

    /// <summary>Null in every ctor except the 4-arg DI one below — callers that don't pass a gate
    /// (tests) run un-gated, as if the gate were always open.</summary>
    private readonly IBackgroundWorkGate? _gate;

    /// <summary>page per-commit metadata from a random-access file instead of keeping
    /// it resident. Read once — the setting takes effect on the next launch.</summary>
    private readonly bool _metaFromDisk;

    /// <summary>Every graph query is walked by <c>git log</c> (<see cref="GitLogStream"/>) through
    /// <paramref name="locator"/>. With git.exe missing the graph is empty rather than walked
    /// some other way (see <see cref="PrimeCliWalkAsync"/>); the startup notice tells the user
    /// why.</summary>
    public GraphLayoutService(ISettingsService settings, IGitExecutableLocator locator)
    {
        _settings = settings;
        _locator = locator;
        // Disk-backed metadata pages from the graph cache folder.
        _metaFromDisk = settings.Current.GraphMetadataFromDisk;
    }

    /// <summary>DI ctor (linux cold-prime plan): additionally wires up
    /// <see cref="GraphBackgroundWriter"/>, scheduled once a repo's background history spill
    /// reaches completion. The 2-arg ctor above keeps working with no commit-graph
    /// maintenance — used by tests that don't care about it.</summary>
    public GraphLayoutService(ISettingsService settings, IGitExecutableLocator locator, IGitCliService cli)
        : this(settings, locator)
    {
        _cli = cli;
        _backgroundWriter = new GraphBackgroundWriter(cli, settings);
    }

    /// <summary>DI ctor: additionally routes every
    /// background writer through <see cref="IBackgroundWorkGate"/> — no commit-graph write and no
    /// graph disk-cache write starts, for any tab, until the focused tab is done loading.</summary>
    public GraphLayoutService(ISettingsService settings, IGitExecutableLocator locator, IGitCliService cli, IBackgroundWorkGate gate)
        : this(settings, locator)
    {
        _cli = cli;
        _gate = gate;
        _backgroundWriter = new GraphBackgroundWriter(cli, settings, gate);
    }

    /// <summary>Awaits the gate if one was supplied; a no-op ("always open") for the ctors above
    /// that never received one.</summary>
    private Task WaitForGateAsync(CancellationToken ct) =>
        _gate?.WaitUntilOpenAsync(ct) ?? Task.CompletedTask;

    private sealed class RepoCache
    {
        /// <summary>The whole query this cache walks — scope, remotes, stashes, path filter and
        /// selected refs — so an extend continues exactly this walk. <see cref="GetOrResetCache"/>
        /// reuses the cache only for a query of the same shape (<see cref="SameWalkShape"/>).</summary>
        public required GraphQuery Query { get; init; }
        public bool IncludeRemotes => Query.IncludeRemoteBranches;
        public List<RawCommit> Commits { get; } = [];
        public Dictionary<ObjectId, IReadOnlyList<GraphDecorationInput>> Decorations { get; set; } = [];
        public bool Exhausted { get; set; }

        /// <summary>The walk could not run or continue — git.exe missing, the process failed to
        /// start, or it failed before exhausting (Rule 11: no fallback). The graph keeps whatever
        /// rows were already walked (none, for a failure before the first row) and nothing more
        /// is attempted for this cache: no respawn from the crawler or the spill, no disk write.
        /// <see cref="Forget"/> or a forced reset drops the cache, so a ref change or a reopen
        /// tries again.</summary>
        public bool WalkFailed { get; set; }

        /// <summary>The tip set moved after this cache committed to a walk (<see cref="WalkKey"/>
        /// set): its rows still picture the old refs correctly, but a walk over the new tips
        /// doesn't line up with them row for row, so nothing more is walked or written for this
        /// cache — no resume, no re-prime, no spill, no disk save. The next
        /// <see cref="ComputeLayoutAsync"/> (or the RefsChanged recompute's <see cref="Forget"/>)
        /// replaces it with a fresh walk from row 0. Normal, not a failure.</summary>
        public bool WalkSuperseded { get; set; }

        /// <summary>Nothing more is walked or written for this cache.</summary>
        public bool WalkHalted => WalkFailed || WalkSuperseded;

        // ── disk-backed metadata ──
        public long Fingerprint { get; set; }

        /// <summary>3 (3b): the sorted-tip-set key the on-disk store/`.gz`/live-walk
        /// resume are keyed on, computed by <see cref="ComputeWalkKey"/> — NOT <see
        /// cref="Fingerprint"/>, which still covers every ref + HEAD and stays the key
        /// <c>RecomputeAsync</c> and the ref-badge cache use. A checkout, a stash, a branch
        /// rename, or a ref outside the walk's scope changes <see cref="Fingerprint"/> (so badges
        /// refresh) but leaves <see cref="WalkKey"/> — and so the disk cache — unchanged.
        /// <para>Once non-zero it names the one walk this cache is committed to; a step that
        /// derives another key supersedes the walk (<see cref="WalkSuperseded"/>). Normally a
        /// prime or a <c>Done</c> sets it with <see cref="Fingerprint"/> and the decorations. It is
        /// set ahead of them — before any row is pulled — in two cases: when the cache adopts a
        /// partial on-disk store (whose rows are that key's), and when a headless job starts
        /// (keyed on the tips it probed, which its writes use).</para></summary>
        public long WalkKey { get; set; }
        public int SpilledRows { get; set; }
        public bool MetaComplete { get; set; }   // the whole history is on disk
        public bool BackgroundSpillRunning { get; set; }
        public GraphMetaWriter? MetaWriter { get; set; }
        public DiskCommitStore? MetaStore { get; set; }
        public readonly object SpillSync = new();
        public readonly CancellationTokenSource Cts = new();

        /// <summary>The background spill's own Task, so
        /// <see cref="IGraphLayoutService.Hibernate"/> can cancel <see cref="Cts"/> and then await
        /// this before disposing <see cref="LiveWalk"/> — never racing a Dispose against a spill
        /// chunk that may still be mid-<c>MoveNext</c>.</summary>
        public Task? BackgroundSpillTask { get; set; }

        // row count as of the last queued GraphWalkCache.Save — debounces the .gz
        // rewrite so a long run of small foreground extends (the silent crawler) doesn't
        // re-snapshot + re-gzip the whole window on every one.
        public int WalkCacheSavedRows { get; set; }

        // ── resumable walk ──
        // A live, primed `git log` process (GitLogStream) pulled forward in chunks by
        // EnsureWalkedAsync — the quick phase, each scroll Extend, the silent crawler and the
        // background spill all continue this ONE walk instead of respawning from the tips every
        // time. Pulled OFF the repo gate, under WalkLock. Disposed on reset / Forget / exhaustion /
        // failure, and when a tip-set change supersedes the walk (WalkSuperseded) — a walk is
        // never re-primed over another tip set within one cache.
        public IEnumerator<RawCommit>? LiveWalk { get; set; }

        /// <summary>3 (3b): despite the name (kept so reflection-based tests that poke this
        /// field by name still compile), this now holds the WALK KEY (<see cref="WalkKey"/>'s
        /// value at prime time), not the full ref fingerprint — a live walk resumes across a
        /// checkout/stash/rename, since none of those change the tip set it's reading.</summary>
        public long LiveWalkFingerprint { get; set; }
        public int LiveWalkPos { get; set; } // rows already pulled from LiveWalk

        /// <summary>The <see cref="RefSnapshot.Token"/> the live walk's tip set was last
        /// confirmed against. While the repo's ref files still hash to it, a walk step resumes
        /// straight off the repo gate — no ref read, no tip resolution. 0 = unconfirmed.</summary>
        public long LiveWalkSnapshotToken { get; set; }

        // Serializes a prime/resume pull (which runs off the repo gate) against a concurrent one
        // — e.g. a foreground Extend racing the background spill's next chunk.
        public readonly SemaphoreSlim WalkLock = new(1, 1);

        /// <summary>Dispose the walk (killing its git process) and forget it. Never while another
        /// thread may be mid-MoveNext: callers hold <see cref="WalkLock"/>, or have cancelled
        /// <see cref="Cts"/> and let the spill task observe it.</summary>
        public void DisposeLiveWalk()
        {
            var w = LiveWalk;
            DropLiveWalk();
            try { w?.Dispose(); } catch { /* best effort */ }
        }

        /// <summary>Forget the walk without disposing it — for callers that can't take
        /// <see cref="WalkLock"/> (Forget / reset), where a Dispose could race an in-flight
        /// MoveNext. The orphaned git process exits once its stdout pipe is closed by
        /// finalization, trading a brief leak for never killing a stream mid-read.</summary>
        public void DropLiveWalk()
        {
            LiveWalk = null;
            LiveWalkFingerprint = 0;
            LiveWalkPos = 0;
            LiveWalkSnapshotToken = 0;
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
    // The walk each layout's rows came from. Extend shares State in place, so one entry covers a
    // layout and every extension of it. A layout whose walk has since been reset (a RefsChanged
    // re-walk) must never be extended from the new walk — its row N is a different commit there.
    private readonly ConditionalWeakTable<GraphLayoutState, RepoCache> _layoutOwners = [];
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
        // A superseded walk can't grow — its tips moved — so a compute always starts a fresh one.
        if (cache.WalkSuperseded)
            cache = GetOrResetCache(context, query, force: true);
        // Refs that can't be read right now are no evidence the painted rows are stale — keep the
        // cache; EnsureWalkedAsync records the failure if it needs refs to go further.
        if (TotalWalked(cache) > 0 && cache.Fingerprint != 0
            && await TryGetRefStateAsync(context, query, background: false, ct).ConfigureAwait(false) is { } state
            && state.Fingerprint != cache.Fingerprint)
            cache = GetOrResetCache(context, query, force: true);
        await EnsureWalkedAsync(context, query, cache, maxRows + WalkOverscan, ct).ConfigureAwait(false);
        Diagnostics.PerfLog.Mark($"ComputeLayout walked ({TotalWalked(cache)} commits cached)");

        var spill = SpillEnabled(query);

        // cache.Commits no longer holds the whole walked history once rows are
        // confirmed spilled (see TrimConfirmedSpilled) — ReadWindow stitches the on-disk store
        // and the resident pending buffer back into one absolute-row-indexed list.
        // A short disk read (a store disposed mid-read, an IOException, a
        // concurrent from-row-0 re-spill) must never feed a misaligned window into the engine —
        // reset the cache (force: true) and re-walk once; if the retry is itself short (a fresh
        // cache has no disk store yet to fail, so this should not recur), proceed with whatever
        // came back rather than loop.
        var read = ReadWindow(cache, 0, maxRows + WalkOverscan);
        if (!read.Complete)
        {
            cache = GetOrResetCache(context, query, force: true);
            await EnsureWalkedAsync(context, query, cache, maxRows + WalkOverscan, ct).ConfigureAwait(false);
            read = ReadWindow(cache, 0, maxRows + WalkOverscan);
        }
        var input = read.Rows;

        // The trunk pre-pass's ancestry check needs no repo handle: DagLayoutEngine.SeedTipPriority
        // answers it with an exact in-window walk over the already-fetched commit prefix
        // (isAncestor: null below), so Compute runs fully off the repo gate.
        GraphLayoutResult result;
        using (Diagnostics.PerfLog.Scope("engine.Compute"))
            result = _engine.Compute(input, cache.Decorations, maxRows, query.MaxLanes, ct, retainCommits: !spill);

        result = result with { Fingerprint = cache.Fingerprint };
        _layoutOwners.AddOrUpdate(result.State, cache);

        if (!spill)
            return result;
        var swapped = SpillAndSwap(context, cache, query.IncludeRemoteBranches, result);
        if (cache.Exhausted && cache.MetaComplete)
            await RetireLiveWalkAsync(cache).ConfigureAwait(false);
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
            if (_caches.TryGetValue(context, out var existing) && TotalWalked(existing) > 0)
                return null;

        // try the metadata store first. Unlike the .gz walk cache below, it is
        // written incrementally to the FULL history by the background spill, isn't bounded by
        // whatever a single foreground walk reached, and (since GraphMetaStore.Version 2) now
        // carries real parent SHAs — enough to lay out a genuine DAG frame, not just a row list.
        // Stale-tolerant like TryLoadStale: any fingerprint is fine, this frame is always
        // replaced by the verified walk a moment later. Ref badges are not in this store, so the
        // frame paints commits/lanes with no decorations for that one moment.
        if (GraphWalkCache.Supports(query))
        {
            var probe = GraphMetaStore.ProbeAny(context.RepositoryPath, query.IncludeRemoteBranches);
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

        // The .gz walk cache is retired once disk-backed metadata is on (its save is
        // skipped in EnsureWalkedAsync below) — no point reading a file nothing writes anymore.
        if (_metaFromDisk)
            return null;

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

        // `existing` was laid out from an earlier walk (a RefsChanged re-walk reset the cache
        // while this layout was still on screen): the current walk's rows don't line up with its
        // rows, so splicing them on would duplicate commits and SpillAndSwap would shift every
        // row's metadata. Its replacement is already being computed — leave it as it is.
        if (!_layoutOwners.TryGetValue(existing.State, out var owner) || !ReferenceEquals(owner, cache))
            return existing;

        var want = existing.State.Rows.Count + additionalRows + WalkOverscan;
        await EnsureWalkedAsync(context, cache.Query, cache, want, ct, background).ConfigureAwait(false);

        // Extend mutates existing.State in place; run it to completion (a handful of ms for
        // <=5k rows) rather than risk a cancellation leaving that shared state half-updated.
        // Read the consumed-row count under lock(existing.State), then
        // RELEASE it before the (potentially disk-IO-bound) ReadWindow call — holding the lock
        // across disk IO blocks the render thread's own lock(existing.State) in DeriveWindow for
        // the duration of a full disk read. Re-take the lock afterwards and proceed only if
        // State.Rows.Count is STILL `consumed`: a concurrent ExtendLayoutAsync on this layout
        // (e.g. the initial compute's phase-2 extend racing a reveal-driven one) may have advanced
        // it while ReadWindow ran off-lock, and feeding _engine.Extend a slice read against a
        // stale `consumed` would re-place commits it already consumed — duplicate rows and a
        // corrupted RowByCommitId. The per-context extend pump is the only extender, so a mismatch
        // here means exactly that race, and returning `existing` unchanged lets the pump retry.
        int consumed;
        lock (existing.State)
            consumed = existing.State.Rows.Count;

        if (TotalWalked(cache) <= consumed)
            return existing;

        // Only what this extend consumes (+ overscan) — the background spill can have walked
        // (and spilled) far past this, and reading all of it here would be pointless.
        // ReadWindow reads confirmed-spilled rows from disk and the rest from the
        // resident pending buffer, since cache.Commits no longer holds the whole history.
        var read = ReadWindow(cache, consumed, additionalRows + WalkOverscan);
        if (!read.Complete)
            return existing; // A short disk read — never splice; the pump retries

        GraphLayoutResult result;
        lock (existing.State)
        {
            if (existing.State.Rows.Count != consumed)
                return existing; // Someone else extended this layout meanwhile
            result = _engine.Extend(existing, read.Rows, cache.Decorations, additionalRows, CancellationToken.None);
        }

        if (!SpillEnabled(cache.Query))
            return result with { Fingerprint = cache.Fingerprint };
        var swapped = SpillAndSwap(context, cache, cache.IncludeRemotes, result);
        if (cache.Exhausted && cache.MetaComplete)
            await RetireLiveWalkAsync(cache).ConfigureAwait(false);
        MaybeStartBackgroundSpill(context, cache, cache.Query); // resume if it stalled/hadn't started
        return swapped with { Fingerprint = cache.Fingerprint };
    }

    /// <summary>metadata is disk-backed only for the plain all-refs query the walk cache
    /// already supports; a path filter / single branch / stashes keep the in-memory store.</summary>
    private bool SpillEnabled(GraphQuery query) => _metaFromDisk && GraphWalkCache.Supports(query);

    /// <summary>
    /// append the freshly laid-out rows' metadata to the on-disk store, point
    /// <see cref="GraphLayout.Commits"/> at the paged reader, and release the resident strings
    /// for the spilled rows. On any IO failure the layout falls back to an in-memory array
    /// rebuilt from the still-resident walk records — correctness never depends on the file.
    /// </summary>
    private GraphLayoutResult SpillAndSwap(
        RepositoryContext context, RepoCache cache, bool includeRemotes, GraphLayoutResult result)
    {
        var rowCount = result.Layout.RowCount;
        var repoPath = context.RepositoryPath;
        // Opening an EXISTING matching store below is a read and
        // always allowed; only a fresh SpillRange write waits for the gate. When the gate is
        // closed and there is nothing to open yet, this call falls through to the in-memory
        // ArrayCommitStore fallback further down — exactly the "resident metadata" frame the
        // plan specifies for first paint while the gate is still closed.
        var gateOpen = _gate?.IsOpen ?? true;

        try
        {
            lock (cache.SpillSync)
            {
                // Warm launch: a matching store from a previous session already holds these rows.
                // (Usually already handled by PrepareWalk's own warm-store hit, so
                // this is a defensive fallback for the paths that don't go through it.)
                if (cache is { SpilledRows: 0, MetaStore: null } && cache.WalkKey != 0
                    && GraphMetaStore.TryOpen(repoPath, cache.WalkKey, includeRemotes, rowCount) is { } warm
                    && warm.Count >= rowCount)
                {
                    cache.MetaStore = warm;
                    AdoptDurableStore(cache, warm.Count);
                }

                if (gateOpen && cache.WalkKey != 0 && !cache.WalkHalted && rowCount > cache.SpilledRows)
                {
                    // Outcome is deliberately unobserved here: Appended/Adopted both leave
                    // cache.MetaStore ready to serve below; a Conflict leaves it null, and the
                    // fallback further down (a fresh ReadWindow-built array) already handles that.
                    _ = SpillRange(cache, repoPath, includeRemotes, cache.SpilledRows, rowCount, result.Layout.IsComplete);
                }
                else
                    cache.MetaStore?.SetCount(Math.Max(cache.MetaStore.Count, rowCount));

                if (cache.MetaStore is null && cache.WalkKey != 0)
                    cache.MetaStore = GraphMetaStore.TryOpen(repoPath, cache.WalkKey, includeRemotes, rowCount);

                if (cache.MetaStore is not null && cache.MetaStore.Count >= rowCount)
                {
                    result.Layout.Commits = cache.MetaStore;
                    return result;
                }
            }
        }
        catch { /* fall through to the in-memory rebuild */ }

        // Disk store unavailable or insufficient — hand back an array assembled from whatever is
        // confirmed on disk plus the resident pending buffer (cache.Commits alone is no
        // longer a valid absolute-row-indexed view once earlier rows have been trimmed).
        // (If the disk rows were id/parents-only, a Recompute re-walks and re-spills.)
        // Only build the array from a COMPLETE read — a short disk read here
        // would misalign every row after it, which is strictly worse than leaving the layout's
        // existing Commits store (already Empty when spill is on) in place for this one frame.
        var read = ReadWindow(cache, 0, rowCount);
        if (read.Complete)
        {
            var arr = new CommitMetadata[read.Rows.Count];
            for (var i = 0; i < arr.Length; i++)
                arr[i] = read.Rows[i].Metadata;
            result.Layout.Commits = new ArrayCommitStore(arr);
        }
        return result;
    }

    /// <summary>Appends rows <paramref name="from"/>..<paramref name="to"/> — always
    /// <c>cache.SpilledRows</c>..<paramref name="to"/> at call time, since
    /// <c>cache.Commits[0]</c> is row <c>cache.SpilledRows</c> once anything has been trimmed
    /// to the on-disk store. Only on CONFIRMED durability (re-opening/re-reading
    /// through <see cref="DiskCommitStore"/>, since <see cref="GraphMetaWriter"/> swallows IO
    /// errors and <c>cache.SpilledRows</c> must never advance on faith alone — "the disk cache is
    /// never a correctness dependency") does it also drop those rows from the resident pending
    /// buffer via <see cref="TrimConfirmedSpilled"/>. Caller holds <c>cache.SpillSync</c>.
    /// Never touches the live walk.
    /// <para>
    /// The actual append goes through <see cref="GraphMetaWriter.AppendSpillBatch"/>,
    /// which re-checks the on-disk header under this repo's cache-wide writer lock before writing
    /// — so an inactive-tab headless job and this cache's own spill can never both append to the
    /// same store. A <see cref="SpillOutcome.Adopted"/> result means another writer already wrote
    /// this exact range; a <see cref="SpillOutcome.Conflict"/> means the header moved out from
    /// under this writer entirely (a reorder, a reset by another writer) — the caller must stop
    /// spilling for this cache and let the next compute/headless pass resync from scratch.
    /// </para>
    /// </summary>
    private static SpillOutcome SpillRange(
        RepoCache cache, string repoPath, bool includeRemotes, int from, int to, bool exhausted,
        TimeSpan? fullWalkElapsed = null, IReadOnlyList<ObjectId>? sortedTips = null)
    {
        if (to <= from)
            return SpillOutcome.Appended;
        List<RawCommit> snap;
        lock (cache)
        {
            to = Math.Min(to, from + cache.Commits.Count);
            if (to <= from)
                return SpillOutcome.Appended;
            snap = cache.Commits.GetRange(0, to - from);
        }
        cache.MetaWriter ??= new GraphMetaWriter(repoPath, cache.WalkKey, includeRemotes);

        var (outcome, rowCount) = cache.MetaWriter.AppendSpillBatch(snap, from, to, exhausted, fullWalkElapsed, sortedTips);
        switch (outcome)
        {
            case SpillOutcome.Conflict:
                // The header no longer matches what this writer expected — don't keep serving a
                // store that may now belong to a different walk key. The next compute/headless
                // pass reopens/re-probes from scratch.
                cache.MetaStore?.Dispose();
                cache.MetaStore = null;
                return outcome;

            case SpillOutcome.Adopted:
                cache.MetaStore?.Dispose();
                cache.MetaStore = GraphMetaStore.TryOpen(repoPath, cache.WalkKey, includeRemotes, rowCount);
                AdoptDurableStore(cache, rowCount);
                var probe = GraphMetaStore.Probe(repoPath, cache.WalkKey, includeRemotes);
                if (probe is { Ok: true, Exhausted: true })
                {
                    cache.MetaComplete = true;
                    cache.Exhausted = true;
                }
                return outcome;

            default:
                cache.MetaComplete = exhausted;
                Diagnostics.PerfLog.Mark($"graph cache write: spill {snap.Count} rows ({repoPath})");

                cache.MetaStore ??= GraphMetaStore.TryOpen(repoPath, cache.WalkKey, includeRemotes, to);
                cache.MetaStore?.SetCount(to);
                if (cache.MetaStore is { } store && store.Count >= to)
                    TrimConfirmedSpilled(cache, to);
                // else: the write or the read-back didn't confirm — leave cache.Commits and
                // cache.SpilledRows where they are; SpillAndSwap's ReadWindow-based fallback and
                // the next spill attempt both cope, and no data is lost.
                return outcome;
        }
    }

    /// <summary>Once <paramref name="confirmedRows"/> leading rows THIS SESSION JUST
    /// WROTE are confirmed durable in <c>cache.MetaStore</c>, drop them from the resident pending
    /// buffer (<c>cache.Commits</c>) and advance <c>cache.SpilledRows</c> to match, so
    /// <c>cache.Commits</c> always holds exactly rows <c>[cache.SpilledRows, TotalWalked)</c>.
    /// Bounded by <c>TotalWalked</c> — this only ever confirms rows this cache actually walked.
    /// Idempotent. For adopting a store that already held rows before this cache walked anything
    /// (a warm launch), use <see cref="AdoptDurableStore"/> instead.</summary>
    private static void TrimConfirmedSpilled(RepoCache cache, int confirmedRows)
    {
        lock (cache)
        {
            if (confirmedRows <= cache.SpilledRows)
                return;
            var removable = Math.Min(confirmedRows, cache.SpilledRows + cache.Commits.Count) - cache.SpilledRows;
            if (removable <= 0)
                return;
            cache.Commits.RemoveRange(0, removable);
            cache.SpilledRows += removable;
        }
    }

    /// <summary>Adopt a pre-existing on-disk store as the durable prefix for a cache
    /// that hasn't walked anything itself yet (a warm launch) — unlike
    /// <see cref="TrimConfirmedSpilled"/>, <paramref name="confirmedRows"/> here can exceed
    /// whatever this session has walked so far, since the store's history predates this cache.
    /// Any rows already resident (there normally are none, at the point this is called) that fall
    /// within the adopted prefix are dropped to keep the invariant that <c>cache.Commits</c> never
    /// duplicates rows the store already serves.</summary>
    private static void AdoptDurableStore(RepoCache cache, int confirmedRows)
    {
        lock (cache)
        {
            if (confirmedRows <= cache.SpilledRows)
                return;
            var overlap = Math.Min(cache.Commits.Count, confirmedRows - cache.SpilledRows);
            if (overlap > 0)
                cache.Commits.RemoveRange(0, overlap);
            cache.SpilledRows = confirmedRows;
        }
    }

    /// <summary>Total rows the walk has produced so far, whether resident (pending) or
    /// already confirmed on disk. Replaces the old <c>cache.Commits.Count</c> reads that meant
    /// "rows walked" rather than "rows resident" — now that confirmed-spilled rows are trimmed
    /// out of <c>cache.Commits</c>, the two are no longer the same number.</summary>
    private static int TotalWalked(RepoCache cache)
    {
        lock (cache)
            return cache.SpilledRows + cache.Commits.Count;
    }

    /// <summary>Result of <see cref="ReadWindow"/>: <see cref="Complete"/> is false only when the
    /// on-disk store returned fewer rows than the disk portion of the request promised — never
    /// when the window simply ran off the end of everything walked so far (that is a normal,
    /// expected shortfall, not a fault).</summary>
    private readonly record struct WindowRead(List<RawCommit> Rows, bool Complete);

    /// <summary>Reads absolute rows [<paramref name="start"/>, <paramref name="start"/>
    /// + <paramref name="count"/>) by splitting between the on-disk store (rows below
    /// <c>cache.SpilledRows</c>, confirmed durable — see <see cref="TrimConfirmedSpilled"/>) and
    /// the resident pending buffer (<c>cache.Commits</c>, which holds only rows
    /// <c>[cache.SpilledRows, TotalWalked)</c>). Replaces every old "GetRange straight off
    /// cache.Commits by absolute row" read now that spilled rows no longer stay resident.
    /// <para><see cref="DiskCommitStore.ReadRawRange"/> can return fewer rows
    /// than asked — a store disposed by <see cref="Forget"/>/<see cref="Hibernate"/> while an
    /// extend is in flight, an <see cref="IOException"/> mid-decode, or a concurrent from-row-0
    /// re-spill all return short. Appending the resident buffer straight after a short disk read
    /// would serve row <c>start+k</c> the commit that belongs at <c>cache.SpilledRows+k</c> — so
    /// this stops and reports <see cref="WindowRead.Complete"/> = false instead of splicing.</para>
    /// </summary>
    private static WindowRead ReadWindow(RepoCache cache, int start, int count)
    {
        lock (cache)
        {
            var spilledRows = cache.SpilledRows;
            var totalWalked = spilledRows + cache.Commits.Count;
            var want = Math.Min(count, Math.Max(0, totalWalked - start));
            if (want <= 0)
                return new WindowRead([], Complete: true);

            var result = new List<RawCommit>(want);
            if (start < spilledRows && cache.MetaStore is { } store)
            {
                var diskCount = Math.Min(want, spilledRows - start);
                var diskRows = store.ReadRawRange(start, diskCount);
                result.AddRange(diskRows);
                if (diskRows.Count < diskCount)
                    return new WindowRead(result, Complete: false);
            }
            var residentStart = Math.Max(0, start - spilledRows);
            var residentTakeCount = (start + want) - Math.Max(start, spilledRows);
            if (residentTakeCount > 0)
                result.AddRange(cache.Commits.GetRange(residentStart, residentTakeCount));
            return new WindowRead(result, Complete: true);
        }
    }

    /// <summary>
    /// once the visible window is spilled, keep walking + spilling the rest of the
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
        var batchRows = TestSpillBatchRowsOverride > 0 ? TestSpillBatchRowsOverride : DefaultSpillBatchRows;

        lock (cache.SpillSync)
        {
            // Note: cache.Exhausted (the walk finished) is NOT a reason to skip — that is exactly
            // when the remaining rows are already walked and just need spilling.
            if (!SpillEnabled(query) || cache.Fingerprint == 0 || cache.BackgroundSpillRunning
                || cache.MetaComplete || cache.WalkHalted)
                return;
            var have = TotalWalked(cache);
            if (cache.SpilledRows >= have && cache.Exhausted)
                return; // everything walked so far is already on disk and there is no more
            cache.BackgroundSpillRunning = true;
        }

        var ct = cache.Cts.Token;
        var spillTask = Task.Run(async () =>
        {
            try
            {
                // No start-anyway timeout in the app —
                // this waits on IBackgroundWorkGate, which is the same "focused tab is done
                // loading" definition SpillStartGrace used to approximate with a 180s fallback.
                // A gate-less test ctor (no IBackgroundWorkGate supplied) behaves as "always
                // open", matching every existing test's expectations.
                await WaitForGateAsync(ct).ConfigureAwait(false);
                if (ct.IsCancellationRequested)
                    return;

                // Cancel-on-focus: if this context's inactive-tab headless job is
                // still queued or running when it gains focus, wait for it to actually exit before
                // this spill's first write — otherwise the headless job and this spill could both
                // append to the same on-disk store at once. A no-op when there's no such job (the
                // overwhelming common case) or no background writer configured (test ctors).
                if (_backgroundWriter is not null)
                    await _backgroundWriter.CancelInactiveTabAsync(context).ConfigureAwait(false);
                if (ct.IsCancellationRequested)
                    return;

                // Walk one chunk, then spill (and trim) that same chunk, then loop —
                // instead of walking all the way to the ceiling before spilling anything. The old
                // two-loop shape let cache.Commits balloon to the whole pre-load cap (up to
                // 300k rows) before the disk writer ever ran; interleaving keeps the resident
                // pending buffer bounded to roughly one batch at a time, which is the whole point
                // of the streaming spool.
                var lastPreloadMemoryMark = 0;
                var lastDiskWriterMemoryMark = 0;
                while (true)
                {
                    int walkTarget;
                    lock (cache) walkTarget = cache.SpilledRows + cache.Commits.Count + SpillWalkChunkRows;
                    await EnsureWalkedAsync(context, query, cache, walkTarget, ct, background: true, lowestPriority: true)
                        .ConfigureAwait(false);
                    if (cache.WalkHalted)
                        break;

                    // Schedule commit-graph maintenance as soon as this repo is known to
                    // be large, rather than waiting for spilling to also finish —
                    // `git commit-graph write --reachable` is a self-contained git operation that
                    // reads the ODB directly and does not depend on Historia's own walk/spill
                    // being complete. MaybeSchedule is idempotent per repo per process (an
                    // `_attempted` set), so calling it every chunk here is harmless once it has
                    // already fired.
                    int walkedSoFar;
                    bool walkDone;
                    lock (cache) { walkedSoFar = TotalWalked(cache); walkDone = cache.Exhausted; }
                    if (walkedSoFar / 50_000 != lastPreloadMemoryMark / 50_000)
                        Diagnostics.PerfLog.Memory($"silent pre-load {walkedSoFar} rows ({context.DisplayName})");
                    lastPreloadMemoryMark = walkedSoFar;
                    _backgroundWriter?.MaybeSchedule(context, walkedSoFar);

                    // Spill whatever this chunk produced, in <= batchRows batches, before walking
                    // further — every writer awaits the gate before it starts AND
                    // between batches, so a tab switch pauses within one batch's write.
                    var conflicted = false;
                    while (true)
                    {
                        await WaitForGateAsync(ct).ConfigureAwait(false);
                        if (ct.IsCancellationRequested)
                            break;

                        int from, to;
                        bool doneSpilling;
                        lock (cache.SpillSync)
                        {
                            var total = TotalWalked(cache);
                            from = cache.SpilledRows;
                            to = Math.Min(total, from + batchRows);
                            doneSpilling = to >= total;
                            if (to > from
                                && SpillRange(cache, context.RepositoryPath, cache.IncludeRemotes, from, to,
                                    exhausted: doneSpilling && walkDone && cache.Exhausted) == SpillOutcome.Conflict)
                                conflicted = true;
                        }
                        if (to / 100_000 != lastDiskWriterMemoryMark / 100_000)
                            Diagnostics.PerfLog.Memory($"disk writer {to} rows ({context.DisplayName})");
                        lastDiskWriterMemoryMark = to;
                        if (to <= from || doneSpilling || conflicted)
                            break;
                        await Task.Delay(150, ct).ConfigureAwait(false); // let UI git ops interleave
                    }

                    // A conflict means the on-disk header moved out from under this
                    // writer entirely (another writer reset or reordered it) — stop this whole
                    // background spill attempt rather than keep hammering a store it no longer
                    // safely understands. The next ComputeLayoutAsync/ExtendLayoutAsync resyncs.
                    if (walkDone || cache.Exhausted || cache.MetaComplete || ct.IsCancellationRequested || conflicted)
                        break;
                    await Task.Delay(150, ct).ConfigureAwait(false); // let foreground + crawl gate work in
                }
            }
            catch (OperationCanceledException) { }
            catch { /* best-effort background caching */ }
            finally
            {
                cache.BackgroundSpillRunning = false;
                // On exhaustion PumpCommits already disposed the walk on the gate; on an
                // external cancel Forget/reset already DropLiveWalk'd it. This is just a
                // belt-and-braces no-op for those paths.
                if (ct.IsCancellationRequested || cache.Exhausted || cache.MetaComplete)
                    await RetireLiveWalkAsync(cache).ConfigureAwait(false);

                // Safety net: the per-chunk call above already schedules commit-graph maintenance
                // as soon as the repo crosses the size threshold, so this normally no-ops (already
                // attempted). Kept for the case the walk loop above never ran at all this call
                // (e.g. it was already fully spilled from a prior invocation this session) but
                // exhaustion + full metadata spill are both true regardless. Not gated on ct — a
                // tab close cancels the spill loop above but the repo is still worth maintaining
                // for the next open.
                if (cache.Exhausted && cache.MetaComplete)
                    _backgroundWriter?.MaybeSchedule(context, TotalWalked(cache));
            }
        }, ct);
        cache.BackgroundSpillTask = spillTask;
    }

    /// <inheritdoc/>
    public void EnqueueInactiveTabCaching(RepositoryContext context, GraphQuery query) =>
        _backgroundWriter?.EnqueueInactiveTab(context, ct => RunHeadlessCacheAsync(context, query, ct));

    /// <inheritdoc/>
    public Task CancelInactiveTabCachingAsync(RepositoryContext context) =>
        _backgroundWriter?.CancelInactiveTabAsync(context) ?? Task.CompletedTask;

    /// <inheritdoc/>
    public void RefreshCommitGraphAfterFetch(RepositoryContext context) =>
        _backgroundWriter?.RefreshAfterFetch(context);

    /// <inheritdoc/>
    public async Task PrecacheClonedRepositoryAsync(
        RepositoryContext context, GraphQuery query, IProgress<GitProgress>? progress, CancellationToken ct)
    {
        progress?.Report(new GitProgress { Phase = CloneStatus.CommitGraphPhase });
        var writeTask = _backgroundWriter is not null
            ? _backgroundWriter.WriteForCloneAsync(context, ct)
            : Task.CompletedTask;

        if (!SpillEnabled(query))
        {
            await writeTask.ConfigureAwait(false);
            return;
        }

        progress?.Report(new GitProgress { Phase = CloneStatus.CachingPhase, ObjectsDone = 0 });

        var spoolTask = Task.Run(async () =>
        {
            if (await ProbeStoreAsync(context, query, ct).ConfigureAwait(false) is not { } probe)
                return 0; // refs unreadable — nothing to walk

            var rowsProgress = progress is null
                ? null
                : new SynchronousProgress<int>(rows =>
                    progress.Report(new GitProgress { Phase = CloneStatus.CachingPhase, ObjectsDone = rows }));

            return await RunHeadlessSpoolAsync(context, query, probe.Tips, probe.WalkKey, ct, ignoreGate: true, rowsSpilled: rowsProgress)
                .ConfigureAwait(false);
        }, ct);

        await Task.WhenAll(writeTask, spoolTask).ConfigureAwait(false);
        var rows = await spoolTask.ConfigureAwait(false);

        _backgroundWriter?.RecordWrite(context.GitDirectory, rows);
    }

    /// <summary>Null when the repo's refs can't be read — there is nothing to walk or key a store
    /// on.</summary>
    private static async Task<(
        long WalkKey,
        string[] Tips,
        (bool Ok, int RowCount, bool Exhausted) Current,
        (bool Ok, long WalkKey, int RowCount, int RecordCount, int OrderVersion, bool Exhausted, long LastFullWalkMs) Existing)?> ProbeStoreAsync(
        RepositoryContext context, GraphQuery query, CancellationToken ct)
    {
        // Off the repo gate: the tips come from the ref snapshot, and the probes are header reads
        // of Historia's own files.
        var repoPath = context.RepositoryPath;
        if (await TryGetRefStateAsync(context, query, background: true, ct).ConfigureAwait(false) is not { } state)
            return null;
        var (_, _, tips, walkKey) = state;
        return (
            WalkKey: walkKey,
            Tips: tips,
            Current: GraphMetaStore.Probe(repoPath, walkKey, query.IncludeRemoteBranches),
            Existing: GraphMetaStore.ProbeExisting(repoPath, query.IncludeRemoteBranches));
    }

    /// <inheritdoc/>
    public async Task RunHeadlessCacheAsync(RepositoryContext context, GraphQuery query, CancellationToken ct)
    {
        if (!SpillEnabled(query))
            return; // disk-backed metadata off, or an unsupported query shape — nothing to do headlessly
        if (!_locator.Current.Found)
            return; // no git.exe — nothing can walk; the startup notice already says so

        var repoPath = context.RepositoryPath;

        // Step 1 — off the repo gate: tip resolution + walk key from the ref snapshot, and a
        // header-only probe both at the current key and at whatever key the store was last
        // written under, regardless of match.
        if (await ProbeStoreAsync(context, query, ct).ConfigureAwait(false) is not { } probe)
            return; // refs unreadable — nothing to walk, nothing written

        if (probe.Current is { Ok: true, Exhausted: true })
        {
            // Already fully cached at the current walk key — still worth a commit-graph
            // staleness check now that its exact row count is known for free.
            _backgroundWriter?.MaybeSchedule(context, probe.Current.RowCount);
            return;
        }

        ct.ThrowIfCancellationRequested();

        // Step 2 — commit-graph, if the repo's size is already known from ANY prior header,
        // regardless of key: a stale walk key doesn't mean a small repo. If unknown, the spool
        // below schedules it itself once exhausted (mirrors the active tab's own ordering).
        if (probe.Existing is { Ok: true, RowCount: var knownRows } && knownRows >= GraphBackgroundWriter.MinCommitsToWrite)
            _backgroundWriter?.MaybeSchedule(context, knownRows);

        // A store whose dead records (superseded by earlier reorders, never
        // referenced by the current order) have grown past 25% of its live row count, or whose
        // unreachable dead prefix is >= 25% of .meta.dat, is compacted by a from-row-0 re-spill
        // instead of yet another reorder — RunHeadlessSpoolAsync resets the store to identity order
        // (RecordCount == RowCount) under the new key, which RunHeadlessReorderAsync's incremental
        // append-only shape can never do on its own (R8).
        var deadRecordsTooHigh = probe.Existing is { Ok: true } existing
            && (long)existing.RecordCount * 3 > (long)existing.RowCount * 4;
        var prefix = GraphMetaStore.ProbeDeadPrefix(repoPath);
        var deadPrefixTooHigh = prefix.DatLength > 0 && prefix.DeadBytes * 4 >= prefix.DatLength;
        if (deadPrefixTooHigh)
            Diagnostics.PerfLog.Mark($"graph cache compaction: dead prefix {prefix.DeadBytes / 1_000_000.0:0.0}MB of {prefix.DatLength / 1_000_000.0:0.0}MB ({repoPath})");

        // Step 3 — headless spool (store missing, partial at the CURRENT key, or over either
        // compaction threshold above) or reorder (an exhausted store exists at a DIFFERENT
        // key — a fetch, a new branch — and isn't due for compaction). Reordering needs no
        // concurrent foreground reader to protect, which is exactly why this headless pipeline —
        // not the active tab's gate/spill loop — is where the live reorder trigger lands.
        if (!probe.Current.Ok && probe.Existing is { Ok: true, Exhausted: true } && !deadRecordsTooHigh && !deadPrefixTooHigh)
        {
            // Try the guarded splice first when the setting is on —
            // it falls back to false on ANY guard rejection or query/write failure, in which case
            // the ordinary reorder below is always correct, just potentially slower.
            if (_settings.Current.EnableGraphSplice)
            {
                var splicedRows = await TryHeadlessSpliceAsync(
                    context, query, repoPath, probe.Tips, probe.WalkKey, probe.Existing, ct).ConfigureAwait(false);
                if (splicedRows >= 0)
                {
                    _backgroundWriter?.MaybeSchedule(context, splicedRows);
                    return;
                }
            }
            await RunHeadlessReorderAsync(context, query, repoPath, probe.Tips, probe.WalkKey, ct).ConfigureAwait(false);
        }
        else
            await RunHeadlessSpoolAsync(context, query, probe.Tips, probe.WalkKey, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The live trigger for <see cref="GraphMetaSplice"/> — brings an
    /// exhausted store whose walk key has moved (a fetch, a new branch) up to date by splicing in
    /// just the added/removed commits instead of a full re-walk, when every guard passes. Returns
    /// the new row count on success, or <c>-1</c> on any guard rejection, missing git/CLI
    /// availability, or query/write failure — the caller falls back to
    /// <see cref="RunHeadlessReorderAsync"/> in every <c>-1</c> case, so a failure here is never a
    /// correctness problem, only a missed optimization.
    /// </summary>
    private async Task<int> TryHeadlessSpliceAsync(
        RepositoryContext context, GraphQuery query, string repoPath, string[] newSortedTips, long newWalkKey,
        (bool Ok, long WalkKey, int RowCount, int RecordCount, int OrderVersion, bool Exhausted, long LastFullWalkMs) oldProbe,
        CancellationToken ct)
    {
        if (_cli is null || !_cli.IsGitAvailable)
            return -1;
        // Guard 3's threshold, checked up front — no point running two git processes on a history
        // too small for splicing to ever pay off.
        var minRowsToConsider = TestSpliceMinRowsOverride ?? GraphMetaSplice.MinRowsToConsider;
        if (oldProbe.RowCount < minRowsToConsider)
            return -1;

        // Guard 1: a store with no `.meta.tips` (never reordered/spooled-to-exhaustion since Phase
        // 5 started recording them) has nothing reliable to splice from.
        var oldTips = GraphMetaStore.ReadTips(repoPath);
        if (oldTips is not { Count: > 0 })
            return -1;
        var oldTipShas = oldTips.Select(t => t.Sha).ToArray();

        // Guard 2 (simplified, same scope cut as GraphMetaSplice's own doc comment): only the
        // CURRENT shallow state is checked, not "unchanged since the old order was built".
        var repoIsShallow = File.Exists(Path.Combine(context.GitDirectory, "shallow"));

        try
        {
            // "Added": GitLogStream fed the new tips plus the old tips as exclusions (`^sha`) —
            // git's own stdin revision syntax for `git log --stdin`, same as an argv `^rev`.
            var addedArgs = newSortedTips.Concat(oldTipShas.Select(sha => "^" + sha)).ToArray();
            var addedCommits = new List<RawCommit>();
            var addedSw = Stopwatch.StartNew();
            using (var stream = GitLogStream.TryStart(_locator, repoPath, addedArgs, context.ActiveProfile))
            {
                if (stream is null)
                    return -1;
                while (stream.MoveNext())
                {
                    ct.ThrowIfCancellationRequested();
                    addedCommits.Add(stream.Current);
                }
                if (stream.Failed)
                    return -1;
            }
            addedSw.Stop();

            // "Removed": plain `git rev-list --stdin` fed the old tips plus the new tips as
            // exclusions — commits reachable from the old tips but not the new ones.
            var removedStdin = string.Join('\n', oldTipShas.Concat(newSortedTips.Select(sha => "^" + sha))) + "\n";
            var removedSw = Stopwatch.StartNew();
            var removedResult = await _cli.RunAsync(new GitCliRequest
            {
                Arguments = ["rev-list", "--stdin"],
                WorkingDirectory = repoPath,
                StandardInput = removedStdin,
                Timeout = TimeSpan.FromSeconds(30),
            }, ct).ConfigureAwait(false);
            removedSw.Stop();
            if (!removedResult.IsSuccess)
                return -1;
            var removedShas = removedResult.StandardOutput.Split(
                '\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            using var oldStore = DiskCommitStore.Open(repoPath, oldProbe.RowCount, oldProbe.RecordCount, oldProbe.OrderVersion);
            var shaIndex = oldStore.BuildShaIndex();

            var removedRecords = new HashSet<int>();
            foreach (var sha in removedShas)
                if (shaIndex.TryGetValue(new ObjectId(sha), out var rec))
                    removedRecords.Add(rec);

            // Guard 5 only ever cares about an added commit's OLD parent sitting within the top
            // MaxOldParentRow rows — no need to index the whole (possibly multi-million-row) store.
            // Guard 6 tracks the max CommitWhen over these same top rows.
            var maxOldParentRow = TestSpliceMaxOldParentRowOverride ?? GraphMetaSplice.MaxOldParentRow;
            var capRows = Math.Min(oldProbe.RowCount, maxOldParentRow);
            var existingRecordRow = new Dictionary<string, int>(capRows);
            var newestOldTopCommitWhen = DateTimeOffset.MinValue;
            for (var row = 0; row < capRows; row++)
            {
                var record = oldStore[row];
                existingRecordRow[record.Id.Sha] = row;
                if (record.CommitWhen > newestOldTopCommitWhen)
                    newestOldTopCommitWhen = record.CommitWhen;
            }

            var guard = GraphMetaSplice.Evaluate(
                storeExhausted: oldProbe.Exhausted,
                oldTipsMatch: true,
                repoIsShallow: repoIsShallow,
                oldRowCount: oldProbe.RowCount,
                addedQueryElapsed: addedSw.Elapsed,
                removedQueryElapsed: removedSw.Elapsed,
                lastFullWalkElapsed: TestSpliceFullWalkElapsedOverride ?? TimeSpan.FromMilliseconds(oldProbe.LastFullWalkMs),
                addedCommits: addedCommits,
                existingRecordRow: existingRecordRow,
                newestOldTopCommitWhen: newestOldTopCommitWhen,
                minRowsToConsider: TestSpliceMinRowsOverride,
                maxOldParentRow: TestSpliceMaxOldParentRowOverride);

            if (!guard.Allowed)
            {
                Diagnostics.PerfLog.Mark($"graph splice rejected: {guard.Reason} ({repoPath})");
                return -1;
            }

            var plan = GraphMetaSplice.Build(oldStore.ToOrderArray(), removedRecords, addedCommits, oldProbe.RecordCount);

            var writer = new GraphMetaWriter(repoPath, newWalkKey, query.IncludeRemoteBranches);
            if (plan.NewRecords.Count > 0 && !writer.AppendReorderBatch(plan.NewRecords, oldProbe.WalkKey))
                return -1;

            if (!writer.TryCommitReorder(
                    oldProbe.WalkKey, oldProbe.Exhausted, oldProbe.OrderVersion, oldProbe.RecordCount,
                    plan.NewRecords.Count, plan.Order, [.. newSortedTips.Select(sha => new ObjectId(sha))]))
                return -1;

            Diagnostics.PerfLog.Mark(
                $"graph splice committed: +{plan.NewRecords.Count} new, -{removedRecords.Count}, {plan.Order.Length} rows ({repoPath})");
            GraphMetaStore.DeleteSupersededOrderFiles(repoPath, currentVersion: oldProbe.OrderVersion + 1);
            TestSpliceCommitted?.Invoke(plan.NewRecords.Count, removedRecords.Count);
            return plan.Order.Length;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // A splice is an optimization over the ordinary reorder, never a correctness
            // dependency — any unexpected failure just falls back.
            return -1;
        }
    }

    /// <summary>Streams a walk into the on-disk store for an inactive tab, one batch at
    /// a time, holding no more than one batch resident between spills — the same interleaved
    /// walk-then-spill shape as the active tab's <see cref="MaybeStartBackgroundSpill"/>, but with
    /// no <see cref="RepoCache"/> ever added to <see cref="_caches"/> and no layout produced.
    /// Cancelling <paramref name="ct"/> stops at the next batch boundary; whatever was already
    /// spilled stays on disk.</summary>
    private async Task<int> RunHeadlessSpoolAsync(
        RepositoryContext context, GraphQuery query, string[] sortedTips, long walkKey, CancellationToken ct,
        bool ignoreGate = false, IProgress<int>? rowsSpilled = null)
    {
        if (!_locator.Current.Found)
            return 0; // no git.exe — nothing can walk; the startup notice already says so

        var batchRows = TestSpillBatchRowsOverride > 0 ? TestSpillBatchRowsOverride : DefaultSpillBatchRows;
        // Committed to the probed walk from the start: the store's key and the tips recorded with
        // it are the probe's, so tips that moved before the first step supersede this spool
        // instead of walking other rows under them.
        var cache = new RepoCache { Query = query, WalkKey = walkKey };
        // Times this spool from the moment it starts pulling rows so a walk that
        // reaches exhaustion having started at row 0 (a genuine full walk, not a resumed partial
        // one — the ONLY case that stamp is meaningful for) can record GraphMetaSplice's guard-4
        // baseline. Only the headless spool times itself this way (not the active tab's own
        // foreground background spill or the extend-spill fallback) — a deliberate scope narrowing
        // for M5.1: the splice trigger itself only ever runs from this headless pipeline (step 5),
        // so only this path's timing is ever read back.
        var walkStopwatch = Stopwatch.StartNew();
        bool? fullWalkFromZero = null;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (!ignoreGate)
                {
                    await WaitForGateAsync(ct).ConfigureAwait(false);
                    if (ct.IsCancellationRequested)
                        break;
                }

                int target;
                lock (cache) target = cache.SpilledRows + cache.Commits.Count + batchRows;
                await EnsureWalkedAsync(context, query, cache, target, ct, background: true, lowestPriority: true)
                    .ConfigureAwait(false);
                if (cache.WalkHalted)
                    break;

                bool walkDone;
                lock (cache) walkDone = cache.Exhausted;

                if (!ignoreGate)
                {
                    await WaitForGateAsync(ct).ConfigureAwait(false);
                    if (ct.IsCancellationRequested)
                        break;
                }

                int from, to;
                var conflicted = false;
                lock (cache.SpillSync)
                {
                    lock (cache) { from = cache.SpilledRows; to = cache.SpilledRows + cache.Commits.Count; }
                    fullWalkFromZero ??= from == 0;
                    var fullWalkElapsed = walkDone && fullWalkFromZero == true ? walkStopwatch.Elapsed : (TimeSpan?)null;
                    var tipsForCommit = walkDone ? sortedTips.Select(sha => new ObjectId(sha)).ToArray() : null;
                    if (to > from)
                    {
                        var outcome = SpillRange(cache, context.RepositoryPath, cache.IncludeRemotes, from, to, exhausted: walkDone, fullWalkElapsed, tipsForCommit);
                        if (outcome == SpillOutcome.Conflict)
                            conflicted = true;
                        else
                            rowsSpilled?.Report(to);
                    }
                }
                // A conflict here means the active tab (or another headless job)
                // already moved this repo's store out from under this spool — stop; the next
                // RunHeadlessCacheAsync call re-probes and resyncs.
                if (conflicted)
                    break;
                if (walkDone || cache.Exhausted || cache.MetaComplete)
                {
                    _backgroundWriter?.MaybeSchedule(context, TotalWalked(cache));
                    break;
                }
                await Task.Delay(150, ct).ConfigureAwait(false);
            }
            if (!cache.Exhausted)
                ct.ThrowIfCancellationRequested();
            return TotalWalked(cache);
        }
        finally
        {
            cache.DisposeLiveWalk();
            cache.MetaStore?.Dispose();
            try { cache.Cts.Dispose(); } catch { /* best effort */ }
        }
    }

    /// <summary>Streams the new history in batches (never
    /// materializes the whole new walk resident at once, unlike the old one-shot shape this
    /// replaced), mapping each batch to a record — reusing the old store's by SHA where possible,
    /// appending only genuinely new ones — as it goes, and trims the walked batch out of
    /// <c>cache.Commits</c> immediately after mapping it. The row → record order isn't knowable
    /// until the walk is exhausted (a partial reorder has no valid order to write), so only the
    /// FINAL atomic <see cref="GraphMetaWriter.TryCommitReorder"/> switches the header over; every
    /// batch's new records were already durably appended via <see cref="GraphMetaWriter.AppendReorderBatch"/>,
    /// off that lock, so a mismatch there just means this whole attempt is abandoned — those
    /// records stay on disk, unreferenced, as harmless dead records for compaction to
    /// reclaim later. Cancelling <paramref name="ct"/> at any point before that final commit
    /// discards the whole attempt; the old order stays authoritative until it.</summary>
    private async Task RunHeadlessReorderAsync(
        RepositoryContext context, GraphQuery query, string repoPath, string[] sortedTips, long newWalkKey,
        CancellationToken ct)
    {
        if (!_locator.Current.Found)
            return; // no git.exe — nothing can walk; the startup notice already says so

        // Open the old store and build its SHA index up front — everything the walk
        // below needs to map each batch as it streams in, rather than only after the whole new
        // history has been walked and copied resident.
        var oldProbe = GraphMetaStore.ProbeExisting(repoPath, query.IncludeRemoteBranches);
        if (!oldProbe.Ok || !oldProbe.Exhausted || oldProbe.WalkKey == newWalkKey)
            return; // stale, gone, or someone else already brought it up to date

        Diagnostics.PerfLog.Memory($"reorder start ({repoPath})");

        GraphMetaReorderBuilder builder;
        using (var oldStore = DiskCommitStore.Open(repoPath, oldProbe.RowCount, oldProbe.RecordCount, oldProbe.OrderVersion))
            builder = new GraphMetaReorderBuilder(oldStore.BuildShaIndex(), oldStore.RecordCount);

        var writer = new GraphMetaWriter(repoPath, newWalkKey, query.IncludeRemoteBranches);
        // Committed to the probed walk: the order is written under newWalkKey with sortedTips, so
        // tips that moved before the first step supersede this reorder rather than feed it
        // another walk's rows.
        var cache = new RepoCache { Query = query, WalkKey = newWalkKey };
        try
        {
            var batchRows = TestSpillBatchRowsOverride > 0 ? TestSpillBatchRowsOverride : DefaultSpillBatchRows;
            while (!cache.Exhausted && !ct.IsCancellationRequested)
            {
                await WaitForGateAsync(ct).ConfigureAwait(false);
                if (ct.IsCancellationRequested)
                    return;
                // cache.Commits is cleared to empty at the end of every iteration below, so
                // TotalWalked(cache) (== cache.Commits.Count here — this cache never spills) is
                // always 0 at this point: EnsureWalkedAsync's own "have" bookkeeping treats
                // batchRows as "batchRows MORE from wherever the live walk enumerator currently
                // is" (tracked separately, by RepoCache.LiveWalkPos), not an absolute total.
                await EnsureWalkedAsync(context, query, cache, batchRows, ct, background: true, lowestPriority: true)
                    .ConfigureAwait(false);
                if (cache.WalkHalted)
                    return; // the new order can't be completed — the old one stays authoritative

                List<RawCommit> batch;
                lock (cache)
                {
                    batch = [.. cache.Commits];
                    cache.Commits.Clear(); // never accumulate past one batch
                }
                TestReorderBatchObserved?.Invoke(batch.Count);

                if (batch.Count > 0)
                {
                    var newRecords = builder.MapBatch(batch);
                    if (newRecords.Count > 0 && !writer.AppendReorderBatch(newRecords, oldProbe.WalkKey))
                        return; // the old store moved under this reorder — abandon
                }

                if (!cache.Exhausted)
                    await Task.Delay(150, ct).ConfigureAwait(false);
            }
            if (ct.IsCancellationRequested || !cache.Exhausted)
                return; // cancelled mid-walk — nothing durable is referenced by any order file yet

            // The atomic switch-over, re-checking under the writer lock that the
            // old store is still exactly what this reorder started from — another writer (a live
            // foreground open of this same repo) could have moved it during any of the streamed
            // batches above, all of which ran off this lock.
            if (!writer.TryCommitReorder(
                    oldProbe.WalkKey, oldProbe.Exhausted, oldProbe.OrderVersion, oldProbe.RecordCount,
                    builder.NewRecordCount, builder.Order, [.. sortedTips.Select(sha => new ObjectId(sha))]))
                return; // best-effort — the old order stays authoritative on any mismatch or write failure

            Diagnostics.PerfLog.Memory($"reorder committed ({repoPath})");
            Diagnostics.PerfLog.Mark(
                $"graph cache write: reorder {builder.NewRecordCount} new records, {builder.Order.Length} rows ({repoPath})");
            _backgroundWriter?.MaybeSchedule(context, builder.Order.Length);

            // The just-superseded order version's file (and anything older still
            // lingering) is no longer reachable — a reader loads the whole order file once, at
            // Open, so nothing can be holding a reference to it now that the header has switched.
            GraphMetaStore.DeleteSupersededOrderFiles(repoPath, currentVersion: oldProbe.OrderVersion + 1);
        }
        finally
        {
            cache.DisposeLiveWalk();
            cache.MetaStore?.Dispose();
            try { cache.Cts.Dispose(); } catch { /* best effort */ }
        }
    }

    public async Task<GraphLayoutResult> RecomputeAsync(
        RepositoryContext context, GraphQuery query, GraphLayoutResult previous,
        InvalidationReason reason, int maxRows = 10_000, CancellationToken ct = default)
    {
        if (reason == InvalidationReason.RefsChanged && previous.Fingerprint != 0)
        {
            // Unreadable refs: keep what is painted; the next RefsChanged tries again.
            if (await TryGetRefStateAsync(context, query, background: false, ct).ConfigureAwait(false) is not { } state
                || state.Fingerprint == previous.Fingerprint)
                return previous;
        }

        // Every invalidation reason recomputes the whole window: lane assignment is a
        // left-to-right dependency chain, so a change at row k can alter every later row.
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

    /// <inheritdoc/>
    public async Task Hibernate(RepositoryContext context)
    {
        RepoCache? cache;
        lock (_sync)
        {
            if (!_caches.TryGetValue(context, out cache))
                return;
            _caches.Remove(context);
        }

        try { cache.Cts.Cancel(); } catch (ObjectDisposedException) { }

        // R3a: let the background spill observe the cancellation and finish its own turn before
        // this touches LiveWalk — disposing it while a chunk may still be mid-MoveNext is exactly
        // the race DropLiveWalk exists to avoid. A stuck spill (repo gate never freed) falls back
        // to the same off-gate drop Forget already uses, rather than hanging tab-switch forever.
        if (cache.BackgroundSpillTask is { } spillTask)
        {
            try { await spillTask.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
            catch { /* timed out or faulted — best effort, DropLiveWalk below is the fallback */ }
        }

        cache.MetaStore?.Dispose();
        cache.MetaStore = null;
        cache.MetaWriter = null;

        if (cache.LiveWalk is { } walk)
        {
            try
            {
                // GitLogStream is a separate process — no repo gate needed, only serialization
                // against a concurrent pull.
                await cache.WalkLock.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (ReferenceEquals(cache.LiveWalk, walk))
                        cache.DisposeLiveWalk();
                }
                finally { cache.WalkLock.Release(); }
            }
            catch { cache.DropLiveWalk(); } // best effort
        }

        lock (cache)
        {
            cache.Commits.Clear();
            cache.Decorations = [];
        }
        try { cache.Cts.Dispose(); } catch { /* best effort */ }

        // R4: drop this repo's .gz read-cache entry too — kept resident it would outlive the tab
        // switch for the rest of the session.
        _walkCache.Evict(context.RepositoryPath);
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
            if (!force && _caches.TryGetValue(context, out var existing) && SameWalkShape(existing.Query, query))
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
            var fresh = new RepoCache { Query = query };
            _caches.Add(context, fresh);
            return fresh;
        }
    }

    /// <summary>True when two queries walk the same history — every knob that changes the tip
    /// set or the rows (scope, remotes, stashes, path filter, selected refs). <see cref="GraphQuery"/>'s
    /// own record equality can't be used: it compares <see cref="GraphQuery.SelectedRefs"/> by
    /// reference, and includes <see cref="GraphQuery.MaxLanes"/>, which only affects layout.</summary>
    private static bool SameWalkShape(GraphQuery a, GraphQuery b) =>
        a.RefScope == b.RefScope
        && a.IncludeRemoteBranches == b.IncludeRemoteBranches
        && a.IncludeStashes == b.IncludeStashes
        && string.Equals(a.PathFilter ?? "", b.PathFilter ?? "", StringComparison.Ordinal)
        && (a.SelectedRefs ?? []).SequenceEqual(b.SelectedRefs ?? [], StringComparer.Ordinal);

    private async Task EnsureWalkedAsync(
        RepositoryContext context, GraphQuery query, RepoCache cache, int wantCount, CancellationToken ct,
        bool background = false, bool lowestPriority = false)
    {
        if (cache.Exhausted || cache.WalkHalted || TotalWalked(cache) >= wantCount)
            return;

        DecoCache? deco;
        lock (_sync)
            _decoByPath.TryGetValue(context.RepositoryPath, out deco);

        Diagnostics.PerfLog.Mark($"EnsureWalked need {wantCount}, have {TotalWalked(cache)}, decoFp={deco?.Fingerprint ?? 0}");
        var repoPath = context.RepositoryPath;
        // three gate tiers, not two — a genuine foreground caller always wins; the
        // commit-graph's own silent crawl (background, but user-visible — it grows the
        // scrollbar) comes next; the full-history disk spill (background, no urgency of its
        // own) is lowestPriority and stands aside for either of the other two so it can never
        // starve the crawl the way it used to when both were the same tier.
        Task<GatePrep> WithRepo(Func<Repository, GatePrep> work) => (background, lowestPriority) switch
        {
            (true, true) => context.WithRepositorySpillAsync(work, ct),
            (true, false) => context.WithRepositoryBackgroundAsync(work, ct),
            _ => context.WithRepositoryAsync(work, ct),
        };

        GatePrep prep = default;
        for (var pass = 0; pass < 2; pass++)
        {
            // Step 0 — off the gate: the repo's refs. A file-state check (ms) unless a ref moved
            // since the last read, in which case one `git for-each-ref` — never a LibGit2Sharp
            // enumeration, which costs seconds at tens of thousands of refs.
            RefSnapshot? snapshot = null;
            string failure = "git.exe was not found";
            if (_locator.Current.Found)
            {
                try { snapshot = await context.RefSnapshots.GetAsync(ct).ConfigureAwait(false); }
                catch (HistoriaGitException ex) { failure = ex.Message; }
            }
            if (snapshot is null)
            {
                // Nothing can walk without the refs — the same outcome as `git log` failing to
                // start (Rule 11: no fallback): the graph keeps what it has and this cache stops
                // trying.
                await cache.WalkLock.WaitAsync(ct).ConfigureAwait(false);
                try { MarkWalkFailed(context, cache, $"reading refs failed: {failure}"); }
                finally { cache.WalkLock.Release(); }
                return;
            }

            long liveToken = cache.LiveWalkSnapshotToken;
            if (cache.LiveWalk is not null && liveToken != 0 && liveToken == snapshot.Token)
            {
                // No ref file changed since this walk's tip set was confirmed: the tips,
                // fingerprint and decorations are exactly the cache's — resume without touching
                // the repo gate. (The token covers HEAD and the stash reflog too, so this holds
                // with stashes on.) A resume never writes the cache's ref state, so nothing it
                // reads here can be merged back stale.
                prep = GatePrep.ForResumeCli(cache.Fingerprint, cache.LiveWalkFingerprint, TotalWalked(cache),
                    wantCount, cache.Decorations, snapshot.Token);
                break;
            }

            // Step 1 — on the repo gate: what still needs LibGit2Sharp (stash list, remote names
            // for decorations, the on-disk cache probe), plus the fingerprint and tip set derived
            // from the snapshot. It deliberately stops short of spawning or pulling `git log` —
            // the walk itself is what must never sit inside the gate.
            prep = await WithRepo(repo =>
                PrepareWalk(repo, repoPath, query, wantCount, deco, cache, snapshot, _metaFromDisk, _walkCache))
                .ConfigureAwait(false);

            // The snapshot was read before the gate wait, which for the spill can be long. A prime
            // from tips that have since moved would only be superseded on the next step, so when a
            // ref file changed meanwhile, prepare once more from fresh refs — but only while this
            // cache holds nothing yet. Once PrepareWalk adopted a partial on-disk store, the
            // prime must walk that store's own tips (its rows are the walk's first rows); a real
            // tip change then supersedes the walk on the next step.
            if (pass > 0 || prep.Kind != GatePrepKind.PrimeCli || prep.Have != 0
                || RefStateToken.Compute(context.GitDirectory).Token == prep.SnapshotToken)
                break;
            Diagnostics.PerfLog.Mark("refs moved during the gate wait — re-preparing");
        }

        if (prep.Kind == GatePrepKind.Superseded)
        {
            await cache.WalkLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                cache.DisposeLiveWalk();
                cache.WalkSuperseded = true;
            }
            finally { cache.WalkLock.Release(); }
            Diagnostics.PerfLog.Mark(
                $"graph walk superseded: tip set changed (walk key {cache.WalkKey:X} → {prep.WalkKey:X}) — waiting for reset");
            return;
        }

        // Step 2 — off the gate: prime or resume the walk.
        WalkResult walked = prep.Kind switch
        {
            GatePrepKind.PrimeCli => await PrimeCliWalkAsync(context, cache, query, prep, ct).ConfigureAwait(false),
            GatePrepKind.ResumeCli => await ResumeCliWalkAsync(context, cache, prep, ct).ConfigureAwait(false),
            _ => prep.Result,
        };

        lock (cache)
        {
            cache.Commits.AddRange(walked.Commits);
            // Never back to false: a concurrent step that exhausted the walk may merge before one
            // that saw the walk already gone and reported the flag it read before that merge.
            cache.Exhausted |= walked.Exhausted;
            // The fingerprint, walk key and decorations describe one ref state and change
            // together, written only by the step that derived them: a prime publishes them under
            // WalkLock before pulling a row (PublishRefState), and only a Done from PrepareWalk
            // carries them here. A resume carries none, so it can never write back values it read
            // before a newer prime published.
            if (walked.CarriesRefState)
            {
                cache.Decorations = walked.Decorations;
                if (walked.Fingerprint != 0)
                    cache.Fingerprint = walked.Fingerprint; // ref-based: RecomputeAsync's short-circuit + badges
                if (walked.WalkKey != 0)
                    cache.WalkKey = walked.WalkKey; // 3 (3b): the disk/resume identity key
            }
            if (walked.MetaStore is not null && cache.MetaStore is null)
            {
                cache.MetaStore = walked.MetaStore;
                // Adopting a pre-existing store the walk didn't write itself this
                // session (a warm launch) — AdoptDurableStore, not TrimConfirmedSpilled, since
                // the store's row count can exceed anything this cache has walked so far.
                AdoptDurableStore(cache, walked.MetaStore.Count);
                cache.MetaComplete = walked.MetaExhausted;
                cache.MetaWriter ??= new GraphMetaWriter(repoPath, cache.WalkKey, cache.IncludeRemotes);
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
        // disk via the MetaStore, and snapshotting cache.Commits per chunk would be O(n²).
        // Debounced: the silent crawler drives many small extends in a row, and re-snapshotting
        // + re-gzipping the whole window on every one is pure waste (and a kill mid-write can
        // leave a bad .gz). Save on exhaustion, the first fill, or every WalkCacheSaveStride rows.
        // Retired once disk-backed metadata is on — the MetaStore (with its walk
        // key, exhausted flag, and now ReadRawRange) is a strictly better warm-start source, and
        // this avoids ever building `[.. cache.Commits]` (an O(n) copy of the pending buffer)
        // for a file nothing reads anymore in disk mode (see PrepareWalk / TryOptimisticSnapshot).
        if (!background && !_metaFromDisk && !walked.FromDisk && !cache.WalkHalted && walked.Fingerprint != 0
            && GraphWalkCache.Supports(query))
        {
            List<RawCommit> commits;
            Dictionary<ObjectId, IReadOnlyList<GraphDecorationInput>> decos;
            bool exhausted;
            long walkKeyToSave;
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
                walkKeyToSave = cache.WalkKey; // 3 (3b): key the .gz on the walk, not every ref
            }
            if (save)
                // Background-write gate: queued until the gate opens rather than fired
                // immediately — during first paint this used to run inside the gate-closed
                // window (R7b).
                _ = Task.Run(async () =>
                {
                    await WaitForGateAsync(CancellationToken.None).ConfigureAwait(false);
                    _walkCache.Save(repoPath, query, walkKeyToSave, exhausted, commits, decos);
                });
        }
    }

    private enum GatePrepKind { Done, PrimeCli, ResumeCli, Superseded }

    /// <summary>Result of the on-gate half of a walk step. <see cref="GatePrepKind.Done"/> carries
    /// a finished <see cref="WalkResult"/> (a disk-cache hit, nothing left to walk, or an empty
    /// tip set); the prime and resume cases hand back just enough (fingerprint, row counts, tips,
    /// decorations) for the caller to finish the work OFF the gate;
    /// <see cref="GatePrepKind.Superseded"/> says the tip set moved under this cache's walk
    /// (<see cref="RepoCache.WalkSuperseded"/>), with the new walk key for the log.</summary>
    private readonly record struct GatePrep(
        GatePrepKind Kind,
        WalkResult Result,
        long Fingerprint,
        long WalkKey,
        int Have,
        int WantCount,
        string[]? Tips,
        Dictionary<ObjectId, IReadOnlyList<GraphDecorationInput>> Decorations,
        bool DecosFresh,
        long SnapshotToken)
    {
        public static GatePrep Done(WalkResult result) =>
            new(GatePrepKind.Done, result, result.Fingerprint, result.WalkKey, 0, 0, null, result.Decorations, result.DecosFresh, 0);

        public static GatePrep ForResumeCli(
            long fp, long walkKey, int have, int want,
            Dictionary<ObjectId, IReadOnlyList<GraphDecorationInput>> decorations, long snapshotToken) =>
            new(GatePrepKind.ResumeCli, default, fp, walkKey, have, want, null, decorations, false, snapshotToken);

        public static GatePrep ForPrimeCli(
            long fp, long walkKey, int have, int want, string[] tips,
            Dictionary<ObjectId, IReadOnlyList<GraphDecorationInput>> decorations, bool decosFresh, long snapshotToken) =>
            new(GatePrepKind.PrimeCli, default, fp, walkKey, have, want, tips, decorations, decosFresh, snapshotToken);
    }

    /// <summary>Caller holds the repo gate. Derives the fingerprint and walk key from
    /// <paramref name="snapshot"/> (plus the stash list, when the query includes stashes) and tries
    /// the on-disk caches; otherwise hands back what the caller needs to resume the live
    /// <see cref="GitLogStream"/> or prime a fresh one, both OFF the gate. Never spawns or pulls
    /// the stream itself.</summary>
    private static GatePrep PrepareWalk(
        Repository repo, string repoPath, GraphQuery query, int wantCount, DecoCache? decoCache,
        RepoCache cache, RefSnapshot snapshot, bool metaFromDisk, GraphWalkCache walkCache)
    {
        // Read fresh on the gate — a concurrent EnsureWalkedAsync (the silent crawler vs the
        // background spill) serialises here and has usually already walked what this caller
        // wanted. Checked before any ref work: on a repo with tens of thousands of refs that work
        // costs seconds, and a satisfied caller needs none of it. "Have" is TOTAL rows walked
        // (spilled + resident pending), not cache.Commits.Count alone — confirmed-spilled rows no
        // longer stay resident. Carries no ref state: the cache's own stays untouched.
        var have = cache.SpilledRows + cache.Commits.Count;
        if (have >= wantCount || cache.Exhausted)
        {
            Diagnostics.PerfLog.Mark($"EnsureWalked satisfied on gate (have {have} >= want {wantCount})");
            return GatePrep.Done(new WalkResult([], cache.Decorations, cache.Exhausted, Fingerprint: 0, DecosFresh: false,
                WalkKey: 0, CarriesRefState: false));
        }

        // 3 (3b): the disk cache, live-walk resume, and .gz identity all key on the sorted
        // TIP SET the walk actually reads (`walkKey`) instead of every ref + HEAD (`fp`, still
        // used below for badges/decorations and by the caller for RecomputeAsync's short-circuit).
        // A checkout, a stash push/pop, a branch rename, or a ref outside this set changes `fp`
        // but leaves `walkKey` — and so the on-disk cache — untouched.
        var stashShas = query.IncludeStashes ? ReadStashShas(repo) : [];
        var (fp, tips, walkKey) = DeriveRefState(snapshot, query, stashShas, repoPath);

        // This cache already walked (or adopted from disk) rows of another tip set. A walk over
        // the new tips doesn't match those rows one for one — new commits land anywhere in its
        // first rows — so neither resuming nor skipping ahead in a fresh walk is correct, and the
        // rows must never reach the old walk's on-disk store. Stop; a reset rebuilds from row 0.
        // Keyed on the walk key, not `have`: the headless reorder empties cache.Commits after
        // every batch.
        if (cache.WalkKey != 0 && walkKey != cache.WalkKey)
            return new GatePrep(GatePrepKind.Superseded, default, fp, walkKey, have, wantCount, null, cache.Decorations, false, snapshot.Token);

        // Fresh cache, nothing primed: try the on-disk walk before spawning anything. Only the
        // plain all-refs shape is ever cached on disk.
        if (have == 0 && cache.LiveWalk is null && GraphWalkCache.Supports(query))
        {
            // Prefer the meta store over the (in disk mode, retired) .gz walk
            // cache — it never needs a resident copy of rows already durable. An exhausted store
            // serves every row straight off disk (WalkResult.Commits stays empty: nothing to add
            // to the pending buffer); a partial store resumes the walk with skip = store.Count
            // instead of reloading any of its rows into memory.
            if (metaFromDisk)
            {
                var probe = GraphMetaStore.Probe(repoPath, walkKey, query.IncludeRemoteBranches);
                if (probe.Ok && probe.RowCount > 0
                    && GraphMetaStore.TryOpen(repoPath, walkKey, query.IncludeRemoteBranches,
                        probe.Exhausted ? probe.RowCount : 0) is { } store)
                {
                    if (probe.Exhausted)
                    {
                        // The .meta store has no decoration data (unlike the retired .gz DTO) —
                        // resolve it the same way a fresh walk would, so a warm reopen doesn't
                        // paint with empty ref badges (GraphSpillTrunkStabilityTests: the tip-
                        // priority pre-pass also depends on which commit is decorated).
                        var (decorations, decosFresh) = ResolveDecorations(repo, query, snapshot, decoCache, fp);
                        Diagnostics.PerfLog.Mark($"meta store HIT (exhausted): {store.Count} rows on disk, no resident copy");
                        return GatePrep.Done(new WalkResult([], decorations, true, fp, decosFresh, FromDisk: true, MetaStore: store, MetaExhausted: true, WalkKey: walkKey));
                    }

                    cache.MetaStore = store;
                    cache.MetaWriter ??= new GraphMetaWriter(repoPath, walkKey, query.IncludeRemoteBranches);
                    AdoptDurableStore(cache, store.Count);
                    // The adopted rows commit this cache to the store's walk now, not at the
                    // prime: a step that prepares before that prime and sees other tips must
                    // supersede, not prime its own walk over these rows.
                    lock (cache)
                        cache.WalkKey = walkKey;
                    have = store.Count;
                    Diagnostics.PerfLog.Mark($"meta store HIT (partial): resuming walk after {store.Count} durable rows");
                    if (have >= wantCount)
                    {
                        var (decorations, decosFresh) = ResolveDecorations(repo, query, snapshot, decoCache, fp);
                        return GatePrep.Done(new WalkResult([], decorations, false, fp, decosFresh, FromDisk: true, WalkKey: walkKey));
                    }
                    // fall through below to prime/continue a walk with skip = have
                }
            }
            else if (walkCache.TryLoad(repoPath, query, wantCount, walkKey, metadataOnDisk: false) is { } hit)
            {
                // The file is keyed on the tip set, which ignores HEAD — its saved badges can
                // predate a checkout. Badges come from the refs now, as for the meta store.
                var (decorations, decosFresh) = ResolveDecorations(repo, query, snapshot, decoCache, fp);
                Diagnostics.PerfLog.Mark($"walk cache HIT: {hit.Commits.Count} commits, exhausted={hit.Exhausted}");
                return GatePrep.Done(new WalkResult(hit.Commits, decorations, hit.Exhausted, fp, decosFresh, FromDisk: true, WalkKey: walkKey));
            }
        }

        // Continue the ONE primed walk for this walk key — no tip reseed, no BuildDecorations.
        // This is what makes an Extend / silent-crawl / spill chunk cheap on a big repo after the
        // first (unavoidably expensive) prime, and what lets it survive a checkout/stash/rename
        // that leaves the tip set — and so the walk key — unchanged.
        // Pulling from the git process is what must stay off the gate — hand it back.
        if (cache.LiveWalk is not null && cache.LiveWalkFingerprint == walkKey)
            return GatePrep.ForResumeCli(fp, walkKey, have, wantCount, cache.Decorations, snapshot.Token);

        // No live walk for this tip set: prime one. The tip set is this cache's own (or the cache
        // is fresh, possibly holding a partial on-disk store of this same walk key), so the `have`
        // rows the prime skips are exactly the walk's first rows. A prime publishes its walk key
        // with LiveWalk (PublishRefState), so no other live walk can be left here; the dispose is
        // a guard, not a path.
        cache.DisposeLiveWalk();
        var (primeDecorations, primeDecosFresh) = ResolveDecorations(repo, query, snapshot, decoCache, fp);
        if (tips.Length == 0)
            return GatePrep.Done(new WalkResult([], primeDecorations, true, fp, primeDecosFresh, WalkKey: walkKey));
        return GatePrep.ForPrimeCli(fp, walkKey, have, wantCount, tips, primeDecorations, primeDecosFresh, snapshot.Token);
    }

    /// <summary>3 (3b): the disk-cache / live-walk-resume identity key — a hash of the
    /// SORTED tip SHAs the walk actually reads (stash tips included when the query includes
    /// stashes), the remote-branch flag and, only when set, the path filter. Unlike <see
    /// cref="ComputeFingerprint"/> it does not depend on HEAD or on any ref outside the walk's
    /// tips, so a checkout, a stash outside the walk, a branch rename, or `pack-refs`/`gc` leaves
    /// it unchanged as long as the tip set is the same. The path is fed only when non-empty, so a
    /// filtered walk can never be resumed as the unfiltered one (or another path's) while every
    /// unfiltered key — and so every existing on-disk store — stays as it is.
    /// <paramref name="sortedTips"/> must already be ordinally sorted (<see cref="ResolveTipShas"/>
    /// guarantees this) — the hash itself doesn't re-sort, so a caller feeding an unsorted list
    /// would silently defeat the point.</summary>
    private static long ComputeWalkKey(IReadOnlyList<string> sortedTips, GraphQuery query)
    {
        var h = StableHash.Fnv64OffsetBasis;
        void Feed(string s) => h = StableHash.Fnv1a64Step(h, s);
        foreach (var sha in sortedTips)
            Feed(sha);
        Feed(query.IncludeRemoteBranches ? "R1" : "R0");
        if (!string.IsNullOrEmpty(query.PathFilter))
            Feed("P:" + query.PathFilter);
        return unchecked((long)h);
    }

    /// <summary>Spawns <see cref="GitLogStream"/> and pulls the first batch — entirely off the
    /// repo gate. When git is missing, the process can't start, or it fails, the walk stops there
    /// (<see cref="RepoCache.WalkFailed"/>): the rows already pulled are kept and returned
    /// non-exhausted, so an empty or partial history is never recorded as complete.
    /// <para>The prime's ref state (fingerprint, walk key, decorations) reaches the cache here,
    /// under <see cref="RepoCache.WalkLock"/> and before any row is pulled, so a resume that takes
    /// the lock next already sees it. The result carries none for the caller to merge.</para></summary>
    private async Task<WalkResult> PrimeCliWalkAsync(
        RepositoryContext context, RepoCache cache, GraphQuery query, GatePrep prep, CancellationToken ct)
    {
        await cache.WalkLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Another step primed (or superseded the walk) while this one waited for the lock — its
            // outcome stands; the next step resumes it, or supersedes it when the tips differ. A
            // walk that already ran to the end leaves no LiveWalk behind, so the row count tells
            // too: priming now would skip only the rows this step saw and pull the rest again.
            if (cache.WalkHalted || cache.LiveWalk is not null
                || (cache.WalkKey != 0 && cache.WalkKey != prep.WalkKey)
                || TotalWalked(cache) != prep.Have)
                return new WalkResult([], prep.Decorations, Exhausted: false, prep.Fingerprint, DecosFresh: false,
                    WalkKey: prep.WalkKey, CarriesRefState: false);

            using var walkTimer = Diagnostics.PerfLog.Scope("Walk loop (cli)");
            PublishRefState(cache, prep);
            var stream = GitLogStream.TryStart(
                _locator, context.RepositoryPath, prep.Tips!, context.ActiveProfile, query.PathFilter);

            if (stream is null)
            {
                MarkWalkFailed(context, cache,
                    _locator.Current.Found ? "git log could not be started" : "git.exe was not found");
                return new WalkResult([], prep.Decorations, Exhausted: false, prep.Fingerprint, prep.DecosFresh,
                    WalkKey: prep.WalkKey, CarriesRefState: false);
            }

            cache.LiveWalk = stream;
            cache.LiveWalkFingerprint = prep.WalkKey;
            cache.LiveWalkPos = 0;
            cache.LiveWalkSnapshotToken = prep.SnapshotToken;

            var need = prep.WantCount - prep.Have;
            var delta = new List<RawCommit>(Math.Min(need, 8192));
            // prep.Have rows may already be resident from a partial on-disk cache (a
            // non-exhausted .gz or .meta store), so the fresh walk re-skips them or its rows
            // [0, need) would land duplicated after the cached ones.
            var exhausted = PumpCommits(stream, delta, skip: prep.Have, want: need, cache, ct);

            if (stream.Failed)
            {
                MarkWalkFailed(context, cache, stream.FailureReason ?? "git log failed");
                exhausted = false;
            }
            else if (exhausted)
                cache.DisposeLiveWalk();

            AppendWalked(cache, delta);
            Diagnostics.PerfLog.Mark($"Walk loop (cli) read {delta.Count} commits (pos {cache.LiveWalkPos}, exhausted={exhausted})");
            return new WalkResult([], prep.Decorations, exhausted, prep.Fingerprint, prep.DecosFresh,
                WalkKey: prep.WalkKey, CarriesRefState: false);
        }
        finally
        {
            cache.WalkLock.Release();
        }
    }

    /// <summary>Writes a prime's fingerprint, walk key and decorations to the cache together.
    /// Caller holds <see cref="RepoCache.WalkLock"/>.</summary>
    private static void PublishRefState(RepoCache cache, GatePrep prep)
    {
        lock (cache)
        {
            cache.Fingerprint = prep.Fingerprint;
            cache.WalkKey = prep.WalkKey;
            cache.Decorations = prep.Decorations;
        }
    }

    /// <summary>Stops the walk for good — see <see cref="RepoCache.WalkFailed"/> — and records
    /// why. Caller holds <see cref="RepoCache.WalkLock"/>.</summary>
    private static void MarkWalkFailed(RepositoryContext context, RepoCache cache, string reason)
    {
        cache.DisposeLiveWalk();
        cache.WalkFailed = true;
        Diagnostics.PerfLog.Mark($"graph walk failed: {reason}");
        AppLog.Write(AppLogLevel.Warn, "graph", $"Commit graph walk failed for {context.RepositoryPath}: {reason}");
    }

    /// <summary>Resumes an already-primed <see cref="GitLogStream"/> — off the repo gate, so a
    /// scroll Extend or the background spill's next chunk never queues behind status/diff
    /// work on this repo.</summary>
    private static async Task<WalkResult> ResumeCliWalkAsync(
        RepositoryContext context, RepoCache cache, GatePrep prep, CancellationToken ct)
    {
        await cache.WalkLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // The walk may have been disposed/replaced between PrepareWalk's read and here (a
            // concurrent Forget/reset) — nothing to add this round; the caller's next attempt
            // re-evaluates from scratch.
            if (cache.LiveWalk is not { } live || cache.LiveWalkFingerprint != prep.WalkKey)
                return new WalkResult([], prep.Decorations, cache.Exhausted, prep.Fingerprint, DecosFresh: false, WalkKey: prep.WalkKey, CarriesRefState: false);

            // PrepareWalk just matched this walk's key against a tip set derived from this
            // snapshot, so later steps may skip the gate until a ref file changes again.
            if (prep.SnapshotToken != 0)
                cache.LiveWalkSnapshotToken = prep.SnapshotToken;

            // Sized from the rows walked now, not prep.Have: a concurrent resume may have pulled a
            // whole chunk while this one waited on WalkLock, after PrepareWalk read it. Exact
            // here because every pull appends its rows before releasing WalkLock (AppendWalked).
            // Not LiveWalkPos: the reorder pass clears cache.Commits after every batch and asks
            // for batchRows each time, i.e. relative to what the cache holds, not to the stream.
            var need = prep.WantCount - TotalWalked(cache);
            if (need <= 0)
                return new WalkResult([], cache.Decorations, Exhausted: false, prep.Fingerprint, DecosFresh: false, WalkKey: prep.WalkKey, CarriesRefState: false);

            var delta = new List<RawCommit>(Math.Min(need, 8192));
            var exhausted = PumpCommits(live, delta, skip: 0, want: need, cache, ct);
            if (live is GitLogStream { Failed: true } failed)
            {
                MarkWalkFailed(context, cache, failed.FailureReason ?? "git log failed");
                exhausted = false;
            }
            else if (exhausted)
                cache.DisposeLiveWalk();
            AppendWalked(cache, delta);
            Diagnostics.PerfLog.Mark($"walk resumed (cli) +{delta.Count} (pos {cache.LiveWalkPos}, exhausted={exhausted})");
            return new WalkResult([], cache.Decorations, exhausted, prep.Fingerprint, DecosFresh: false, WalkKey: prep.WalkKey, CarriesRefState: false);
        }
        finally
        {
            cache.WalkLock.Release();
        }
    }

    /// <summary>Appends a pull's rows to the pending buffer while the caller still holds
    /// <see cref="RepoCache.WalkLock"/>, so two pulls in a row can never land in the buffer in the
    /// opposite order to the one they were read from the stream in.</summary>
    private static void AppendWalked(RepoCache cache, List<RawCommit> delta)
    {
        if (delta.Count == 0)
            return;
        lock (cache)
            cache.Commits.AddRange(delta);
    }

    private const int RetireWalkLockTimeoutMs = 5_000;

    /// <summary>
    /// Safely retires and disposes the cache's live walk off the spill lock, under
    /// <see cref="RepoCache.WalkLock"/> so it never races an in-flight pull. Gives up after
    /// <see cref="RetireWalkLockTimeoutMs"/> rather than stall a layout.
    /// </summary>
    private static async Task RetireLiveWalkAsync(RepoCache cache)
    {
        if (cache.LiveWalk is null)
            return;

        try
        {
            if (await cache.WalkLock.WaitAsync(RetireWalkLockTimeoutMs).ConfigureAwait(false))
            {
                try
                {
                    cache.DisposeLiveWalk();
                }
                finally
                {
                    cache.WalkLock.Release();
                }
            }
        }
        catch
        {
            // Retirement is best-effort; a failed retirement must never fail a layout.
        }
    }

    /// <summary>Pull up to <paramref name="want"/> commits from an already-positioned walk
    /// enumerator into <paramref name="into"/>, discarding the first <paramref name="skip"/>
    /// still-unconsumed rows (used once, when a partial on-disk cache seeded
    /// <c>cache.Commits</c> and the fresh walk has to re-skip them). Returns true when the walk
    /// ran out — which, for a <see cref="GitLogStream"/>, is also how a failure surfaces, so
    /// callers check <see cref="GitLogStream.Failed"/> before treating it as exhaustion.
    /// Cooperative: a cancelled <paramref name="ct"/> stops early and leaves the enumerator valid
    /// for the next call. Caller holds <see cref="RepoCache.WalkLock"/>.</summary>
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

    /// <summary>Resolves the walk's tips from <paramref name="snapshot"/> — local / remote / tag
    /// refs filtered by <see cref="GraphQuery.RefScope"/>, plus each stash's work-tree commit
    /// (<paramref name="stashShas"/>) when <see cref="GraphQuery.IncludeStashes"/> is set — as
    /// commit SHAs for <c>git log --stdin</c>.
    /// <para>An annotated tag reaches the walk through its peeled commit (a release tagged on a
    /// branch later deleted is only reachable that way); a ref that reaches a tree or blob, such
    /// as linux's <c>v2.6.11-tree</c>, is dropped (<see cref="RefEntry.PeeledCommit"/>). A dangling
    /// symbolic ref never appears in the snapshot at all. The scope filters are the plain
    /// <c>refs/heads/</c> / <c>refs/remotes/</c> / <c>refs/tags/</c> prefix tests LibGit2Sharp's
    /// <c>IsLocalBranch</c> / <c>IsRemoteTrackingBranch</c> / <c>IsTag</c> apply (verified against
    /// 0.31.0), and <see cref="GraphRefScope.Selected"/> names are full ref names or <c>HEAD</c> —
    /// so the walk key over this tip set is the one LibGit2Sharp-resolved tips gave, and existing
    /// on-disk stores stay valid.</para>
    /// The returned array is sorted ordinally — git's topological sort depends on the order tips
    /// are fed to it (measured), and the fingerprint already ignores ref enumeration order, so an
    /// unsorted list would let `git gc`/`pack-refs` silently reorder the walk under an unchanged
    /// fingerprint.</summary>
    private static string[] ResolveTipShas(RefSnapshot snapshot, GraphQuery query, IReadOnlyList<string?> stashShas)
    {
        var shas = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Add(string? sha) { if (sha is not null && seen.Add(sha)) shas.Add(sha); }

        switch (query.RefScope)
        {
            case GraphRefScope.CurrentBranch:
                Add(snapshot.HeadTarget?.Sha);
                break;

            case GraphRefScope.Selected:
                foreach (var name in query.SelectedRefs ?? [])
                {
                    if (string.Equals(name, "HEAD", StringComparison.Ordinal))
                        Add(snapshot.HeadTarget?.Sha);
                    else
                        Add(snapshot.Find(name)?.PeeledCommit?.Sha);
                }
                break;

            default:
                foreach (var r in snapshot.Refs)
                {
                    var name = r.CanonicalName;
                    if (name.StartsWith("refs/heads/", StringComparison.Ordinal)
                        || (query.IncludeRemoteBranches && name.StartsWith("refs/remotes/", StringComparison.Ordinal))
                        || name.StartsWith("refs/tags/", StringComparison.Ordinal))
                        Add(r.PeeledCommit?.Sha);
                }
                break;
        }

        // stashes are reachable roots only when the flag is set.
        if (query.IncludeStashes)
            foreach (var sha in stashShas)
                Add(sha);

        shas.Sort(StringComparer.Ordinal);
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
        bool MetaExhausted = false,
        long WalkKey = 0,
        // Whether the merge writes Fingerprint / WalkKey / Decorations to the cache: only a Done
        // that derived them on the gate. A prime publishes its own under WalkLock; a resume
        // derives none.
        bool CarriesRefState = true);

    /// <summary>Each stash's work-tree commit, newest first — the one ref-derived input the
    /// snapshot can't supply (older stashes live only in the stash reflog). Caller holds the
    /// repo gate; only read when the query includes stashes.</summary>
    private static string?[] ReadStashShas(Repository repo) =>
        [.. repo.Stashes.Select(st => st.WorkTree?.Sha)];

    /// <summary>The fingerprint, sorted tip set and walk key for one query over one snapshot —
    /// pure computation, milliseconds even at chromium's 39k refs.</summary>
    private static (long Fingerprint, string[] Tips, long WalkKey) DeriveRefState(
        RefSnapshot snapshot, GraphQuery query, IReadOnlyList<string?> stashShas, string repositoryPath)
    {
        CountRefScan(repositoryPath);
        var fp = ComputeFingerprint(snapshot, query, stashShas);
        var tips = ResolveTipShas(snapshot, query, stashShas);
        return (fp, tips, ComputeWalkKey(tips, query));
    }

    /// <summary>The current ref state for <paramref name="query"/>, read off the repo gate (the
    /// snapshot is a token check or one separate process); only a query that includes stashes
    /// takes one short gate turn, for the stash list. Null when the refs can't be read — the
    /// <see cref="HistoriaGitException"/> has already logged why, and each caller decides what an
    /// unknown ref state means for it; none lets the failure escape the service.</summary>
    private static async Task<(RefSnapshot Snapshot, long Fingerprint, string[] Tips, long WalkKey)?> TryGetRefStateAsync(
        RepositoryContext context, GraphQuery query, bool background, CancellationToken ct)
    {
        RefSnapshot snapshot;
        try { snapshot = await context.RefSnapshots.GetAsync(ct).ConfigureAwait(false); }
        catch (HistoriaGitException ex)
        {
            Diagnostics.PerfLog.Mark($"ref state unreadable: {ex.Message}");
            return null;
        }
        IReadOnlyList<string?> stashShas = [];
        if (query.IncludeStashes)
            stashShas = background
                ? await context.WithRepositoryBackgroundAsync(ReadStashShas, ct).ConfigureAwait(false)
                : await context.WithRepositoryAsync(ReadStashShas, ct).ConfigureAwait(false);
        var (fp, tips, walkKey) = DeriveRefState(snapshot, query, stashShas, context.RepositoryPath);
        return (snapshot, fp, tips, walkKey);
    }

    /// <summary>
    /// Stable (cross-process) fingerprint of everything that changes the walk output or its
    /// badges: every ref's name + target (a symbolic ref's target ref name), where HEAD points
    /// and what it resolves to, and the query knobs (scope, remote-branch and stash inclusion,
    /// path filter, selected refs, and — only when stashes are in scope — the stash list).
    /// FNV-1a over ordinally sorted input, so it does not depend on ref enumeration order or
    /// per-process <see cref="string.GetHashCode()"/> randomisation. In-memory only — the disk
    /// stores key on <see cref="ComputeWalkKey"/>.
    /// </summary>
    private static long ComputeFingerprint(RefSnapshot snapshot, GraphQuery query, IReadOnlyList<string?> stashShas)
    {
        var h = StableHash.Fnv64OffsetBasis;
        void Feed(string s) => h = StableHash.Fnv1a64Step(h, s);

        foreach (var r in snapshot.Refs) // already ordinal by name
        {
            Feed(r.CanonicalName);
            Feed(r.SymrefTarget ?? r.Target?.Sha ?? "");
        }

        Feed("HEAD");
        Feed(snapshot.HeadSymref ?? "");
        Feed(snapshot.HeadTarget?.Sha ?? "");

        Feed("query");
        Feed(query.RefScope.ToString());
        Feed(query.IncludeRemoteBranches ? "R1" : "R0");
        Feed(query.IncludeStashes ? "S1" : "S0");
        Feed(query.PathFilter ?? "");
        if (query.RefScope == GraphRefScope.Selected && query.SelectedRefs is { } sel)
            foreach (var s in sel.OrderBy(x => x, StringComparer.Ordinal))
                Feed(s);

        if (query.IncludeStashes)
        {
            foreach (var sha in stashShas)
                Feed(sha ?? "");
            Feed("n" + stashShas.Count);
        }

        return unchecked((long)h);
    }

    /// <summary>Reuses the per-path decoration cache while the ref fingerprint is unchanged;
    /// otherwise rebuilds it with <see cref="BuildDecorations"/>.</summary>
    private static (Dictionary<ObjectId, IReadOnlyList<GraphDecorationInput>> Decorations, bool Fresh) ResolveDecorations(
        Repository repo, GraphQuery query, RefSnapshot snapshot, DecoCache? decoCache, long fingerprint)
    {
        if (decoCache is not null && fingerprint != 0 && decoCache.Fingerprint == fingerprint)
            return (decoCache.Decos, false);

        Dictionary<ObjectId, IReadOnlyList<GraphDecorationInput>> decorations;
        using (Diagnostics.PerfLog.Scope("BuildDecorations"))
            decorations = BuildDecorations(repo, query, snapshot);
        return (decorations, true);
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
    /// one or more remotes into a single badge. Works straight off
    /// <paramref name="snapshot"/> — branch tips are the ref's resolved target, tags their
    /// peeled commit, with no object load; only the remote names and the stash list come from
    /// <paramref name="repo"/>. The remote prefix is stripped by matching the
    /// configured remote names (longest first), falling back to the first path segment only for
    /// an orphan remote-tracking ref whose remote no longer exists.
    /// </summary>
    private static Dictionary<ObjectId, IReadOnlyList<GraphDecorationInput>> BuildDecorations(
        Repository repo, GraphQuery query, RefSnapshot snapshot)
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

        string[] remoteNames;
        try { remoteNames = repo.Network.Remotes.Select(rm => rm.Name).OrderByDescending(n => n.Length).ToArray(); }
        catch { remoteNames = []; }

        foreach (var r in snapshot.Refs)
        {
            var name = r.CanonicalName;

            if (name.StartsWith("refs/heads/", StringComparison.Ordinal))
            {
                if (r.Target is { } tip)
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
                if (r.Target is not { } tip)
                    continue;
                var b = Get(tip, Family.Branch, shortName);
                if (!b.Remotes.Contains(remote))
                    b.Remotes.Add(remote);
            }
            else if (name.StartsWith("refs/tags/", StringComparison.Ordinal))
            {
                if (r.PeeledCommit is { } id)
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

        // HEAD folds into its branch badge; a standalone HEAD badge only when detached.
        if (snapshot.HeadTarget is { } headTip)
        {
            if (snapshot.IsHeadDetached)
                Get(headTip, Family.Branch, "HEAD").IsHead = true;
            else
            {
                var t = snapshot.HeadSymref;
                var shortName = t is not null && t.StartsWith("refs/heads/", StringComparison.Ordinal)
                    ? t["refs/heads/".Length..]
                    : t ?? "HEAD";
                Get(headTip, Family.Branch, shortName).IsHead = true;
            }
        }

        var defaultBranch = ResolveDefaultBranchShortName(snapshot);
        return map.ToDictionary(
            kv => kv.Key,
            kv => BuildInputs(kv.Value.Values, defaultBranch));
    }

    /// <summary>
    /// ordering source: the branch whose badge sorts first when several sit on one commit.
    /// Precedence: the currently checked-out branch, then <c>origin/HEAD</c>'s target, then a
    /// branch literally named <c>main</c>, then <c>master</c>, else none.
    /// </summary>
    private static string? ResolveDefaultBranchShortName(RefSnapshot snapshot)
    {
        if (!snapshot.IsHeadUnborn && !snapshot.IsHeadDetached)
        {
            var t = snapshot.HeadSymref;
            return t is not null && t.StartsWith("refs/heads/", StringComparison.Ordinal)
                ? t["refs/heads/".Length..]
                : t;
        }

        if (snapshot.Find("refs/remotes/origin/HEAD")?.SymrefTarget is { } target)
        {
            const string prefix = "refs/remotes/";
            var s = target.StartsWith(prefix, StringComparison.Ordinal) ? target[prefix.Length..] : target;
            var slash = s.IndexOf('/');
            return slash >= 0 ? s[(slash + 1)..] : s;
        }

        if (snapshot.Find("refs/heads/main") is not null) return "main";
        if (snapshot.Find("refs/heads/master") is not null) return "master";
        return null;
    }

    /// <summary>
    /// as soon as a commit carries at least one branch, that branch's badge stands in for
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
