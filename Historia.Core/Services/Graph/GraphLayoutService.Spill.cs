namespace Historia.Services.Graph;

public sealed partial class GraphLayoutService
{
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

                // A queued or running headless job for this tab is a second writer on the same
                // store; this write stands aside and the background spill writes these rows after
                // waiting that job out. The layout is served from what is already on disk.
                // Rows of a proven prefix the full walk hasn't confirmed yet never reach disk.
                if (gateOpen && cache.WalkKey != 0 && !cache.WalkHalted && rowCount > cache.SpilledRows
                    && cache.ProvenPrefixIds is null
                    && !(_backgroundWriter?.HasInactiveJob(context) ?? false))
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
        // confirmed on disk plus the resident pending buffer (cache.Commits alone is not
        // a valid absolute-row-indexed view once earlier rows have been trimmed).
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
        TimeSpan? fullWalkElapsed = null, IReadOnlyList<GitObjectId>? sortedTips = null)
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

        // Trap: when another writer resets the store and comes back to this walk key (flapping
        // tips, two writers on one tab), a cache can still hold its store from before the reset.
        // The header matches this key again and appends succeed, but the held store reads retired
        // files that never grow, so resident rows are never trimmed. The writer reports the
        // incarnation on disk; a store from an older one is dropped and reopened below.
        if (outcome != SpillOutcome.Conflict && cache.MetaStore is { } held && held.Generation != cache.MetaWriter.Generation)
        {
            held.Dispose();
            cache.MetaStore = null;
        }

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
                cache.MetaStore ??= GraphMetaStore.TryOpen(repoPath, cache.WalkKey, includeRemotes, rowCount);
                cache.MetaStore?.SetCount(rowCount);
                // Drop only the resident rows the open store can serve: a reordered store's count
                // does not grow in place, and rows past it would be unreadable once gone from memory.
                var servable = Math.Min(rowCount, cache.MetaStore?.Count ?? 0);
                AdoptDurableStore(cache, servable);
                var probe = GraphMetaStore.Probe(repoPath, cache.WalkKey, includeRemotes);
                if (probe is { Ok: true, Exhausted: true } && cache.MetaStore?.Count >= probe.RowCount)
                {
                    lock (cache)
                    {
                        cache.MetaComplete = true;
                        cache.Exhausted = true;
                    }
                }
                return outcome;

            default:
                lock (cache)
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
    /// already confirmed on disk. Use this, not <c>cache.Commits.Count</c>, wherever "rows
    /// walked" is meant: confirmed-spilled rows are trimmed out of <c>cache.Commits</c>, so
    /// "rows resident" is a different number.</summary>
    private static int TotalWalked(RepoCache cache)
    {
        lock (cache)
            return cache.SpilledRows + cache.Commits.Count;
    }

    /// <summary>Result of <see cref="ReadWindow"/>: <see cref="Complete"/> is false only when the
    /// disk portion of the request could not be served — the cache holds no store, or the store
    /// returned fewer rows than it promised — never when the window simply ran off the end of
    /// everything walked so far (that is a normal, expected shortfall, not a fault).</summary>
    private readonly record struct WindowRead(List<RawCommit> Rows, bool Complete);

    /// <summary>Reads absolute rows [<paramref name="start"/>, <paramref name="start"/>
    /// + <paramref name="count"/>) by splitting between the on-disk store (rows below
    /// <c>cache.SpilledRows</c>, confirmed durable — see <see cref="TrimConfirmedSpilled"/>) and
    /// the resident pending buffer (<c>cache.Commits</c>, which holds only rows
    /// <c>[cache.SpilledRows, TotalWalked)</c>). Use this rather than a <c>GetRange</c> straight
    /// off <c>cache.Commits</c> by absolute row: spilled rows don't stay resident.
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
            if (start < spilledRows)
            {
                // Trap: with the store gone (dropped on a conflict with another writer, or torn down
                // by a tab switch), skipping the disk part and still reporting a complete window
                // builds the layout from the resident rows alone — painted row 0 would be the walk's
                // row `spilledRows`. Rows that live on disk cannot be served without the store; say
                // so.
                if (cache.MetaStore is not { } store)
                    return new WindowRead(result, Complete: false);
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
    /// has landed, waiting on the background-work gate (<c>WaitForGateAsync</c>) — on a large repo it can run for
    /// tens of seconds, so racing it against first paint is what makes a cold open of a big
    /// repo take far longer than the "small first window" design should ever cost.</para>
    /// </summary>
    private void MaybeStartBackgroundSpill(RepositoryContext context, RepoCache cache, GraphQuery query)
    {
        TestBeforeBackgroundSpillStart?.Invoke();
        var batchRows = TestSpillBatchRowsOverride > 0 ? TestSpillBatchRowsOverride : DefaultSpillBatchRows;
        var ct = cache.Cancellation;

        lock (cache.SpillSync)
        {
            // Note: cache.Exhausted (the walk finished) is NOT a reason to skip — that is exactly
            // when the remaining rows are already walked and just need spilling. A cancelled cache
            // was torn down while this compute or extend ran; nothing should spill into it.
            if (!SpillEnabled(query) || cache.Fingerprint == 0 || cache.BackgroundSpillRunning
                || cache.MetaComplete || cache.WalkHalted || ct.IsCancellationRequested)
                return;
            var have = TotalWalked(cache);
            if (cache.SpilledRows >= have && cache.Exhausted)
                return; // everything walked so far is already on disk and there is no more
            cache.BackgroundSpillRunning = true;
        }

        var spillTask = Task.Run(async () =>
        {
            try
            {
                // No start-anyway timeout in the app —
                // this waits on IBackgroundWorkGate, the "focused tab is done loading" signal.
                // A gate-less test ctor (no IBackgroundWorkGate supplied) behaves as "always
                // open".
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
                // instead of walking all the way to the ceiling before spilling anything. Walking
                // first and spilling after would let cache.Commits balloon to the whole pre-load
                // cap (up to 300k rows) before the disk writer ever ran; interleaving keeps the resident
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

                    int walkedSoFar;
                    bool walkDone;
                    lock (cache) { walkedSoFar = TotalWalked(cache); walkDone = cache.Exhausted; }
                    if (walkedSoFar / 50_000 != lastPreloadMemoryMark / 50_000)
                        Diagnostics.PerfLog.Memory($"silent pre-load {walkedSoFar} rows ({context.DisplayName})");
                    lastPreloadMemoryMark = walkedSoFar;

                    // Spill whatever this chunk produced, in <= batchRows batches, before walking
                    // further — every writer awaits the gate before it starts AND
                    // between batches, so a tab switch pauses within one batch's write.
                    var conflicted = false;
                    while (true)
                    {
                        await WaitForGateAsync(ct).ConfigureAwait(false);
                        if (ct.IsCancellationRequested)
                            break;
                        // Unconfirmed proven-prefix rows: the next walk step confirms them first.
                        if (cache.ProvenPrefixIds is not null)
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
                lock (cache.SpillSync)
                    cache.BackgroundSpillRunning = false;
                // On exhaustion PumpCommits already disposed the walk on the gate; on an
                // external cancel Forget/reset already DropLiveWalk'd it. This is just a
                // belt-and-braces no-op for those paths.
                if (ct.IsCancellationRequested || cache.Exhausted || cache.MetaComplete)
                    await RetireLiveWalkAsync(cache).ConfigureAwait(false);
            }
        }, ct);
        cache.BackgroundSpillTask = spillTask;
    }
}
