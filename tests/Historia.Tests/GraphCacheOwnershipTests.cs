namespace Historia.Tests;

/// <summary>
/// Two writers (the focused tab's own spill and an
/// inactive tab's headless spool/reorder) could both append to the same repo's <c>.meta.*</c>
/// store, producing a duplicated or misordered on-disk history. The fix has two layers, each
/// covered here:
/// <list type="bullet">
/// <item><see cref="GraphMetaWriter.AppendSpillBatch"/> / <see cref="GraphMetaWriter.TryCommitReorder"/>
/// — every write re-checks the on-disk header under a per-repo-stem lock
/// (<see cref="GraphCacheLocks"/>) before writing, so a stale writer either adopts what's already
/// there or backs off instead of corrupting the store.</item>
/// <item><see cref="GraphBackgroundWriter.CancelInactiveTabAsync"/> — a tab gaining focus stops
/// its own queued or running headless job outright, so the two writers are rarely even racing in
/// the first place.</item>
/// </list>
/// </summary>
[Trait("tier", "git")]
public sealed class GraphCacheOwnershipTests : IAsyncDisposable
{
    private readonly StateDir _state = new();
    private readonly MiniHost _host = new();
    private const long WalkKeyA = 0x1111_2222_3333_4444;
    private const long WalkKeyB = 0x5555_6666_7777_8888;
    private const string Repo = @"C:\repos\cache-ownership-demo";
    private const WalkEngine Engine = WalkEngine.LibGit2;

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
        _state.Dispose();
    }

    private static ObjectId Oid(int i) => new(i.ToString("x40"));

    private static CommitMetadata Meta(int i) => new()
    {
        Id = Oid(i),
        MessageShort = $"commit {i}",
        AuthorName = "A",
        AuthorEmail = "a@x",
        AuthorWhen = DateTimeOffset.UnixEpoch.AddMinutes(i),
        CommitWhen = DateTimeOffset.UnixEpoch.AddMinutes(i),
        ParentCount = i == 0 ? 0 : 1,
    };

    private static RawCommit Raw(int i) => new(Oid(i), i == 0 ? [] : [Oid(i - 1)], Meta(i));

    private static List<RawCommit> RawRange(int from, int count) =>
        Enumerable.Range(from, count).Select(Raw).ToList();

    private static GraphMetaStore.Header ReadHeader()
    {
        var bytes = File.ReadAllBytes(GraphCachePaths.File(Repo, GraphMetaStore.HdrExt));
        return System.Text.Json.JsonSerializer.Deserialize<GraphMetaStore.Header>(bytes, GraphMetaStore.Json)!;
    }

    // ── AppendSpillBatch: the header re-check ──

    [Fact]
    public void A_fresh_writer_at_row_zero_with_no_existing_header_appends_and_resets()
    {
        var writer = new GraphMetaWriter(Repo, WalkKeyA, includeRemoteBranches: true, Engine);

        var (outcome, rowCount) = writer.AppendSpillBatch(RawRange(0, 10), from: 0, to: 10, exhausted: false);

        Assert.Equal(SpillOutcome.Appended, outcome);
        Assert.Equal(10, rowCount);
        Assert.Equal(10, ReadHeader().RowCount);
    }

    [Fact]
    public void A_batch_that_continues_a_matching_header_appends_normally()
    {
        var writer = new GraphMetaWriter(Repo, WalkKeyA, includeRemoteBranches: true, Engine);
        writer.AppendSpillBatch(RawRange(0, 10), from: 0, to: 10, exhausted: false);

        var (outcome, rowCount) = writer.AppendSpillBatch(RawRange(10, 10), from: 10, to: 20, exhausted: false);

        Assert.Equal(SpillOutcome.Appended, outcome);
        Assert.Equal(20, rowCount);
        Assert.Equal(20, ReadHeader().RowCount);
    }

    [Fact]
    public void A_batch_whose_range_another_writer_already_durably_wrote_is_adopted_not_reappended()
    {
        var firstWriter = new GraphMetaWriter(Repo, WalkKeyA, includeRemoteBranches: true, Engine);
        firstWriter.AppendSpillBatch(RawRange(0, 20), from: 0, to: 20, exhausted: false); // "another writer"
        var datLengthAfterFirst = new FileInfo(GraphCachePaths.File(Repo, GraphMetaStore.DatExt)).Length;

        // This writer thinks it's continuing from row 10 — stale, since row 20 is already durable.
        var staleWriter = new GraphMetaWriter(Repo, WalkKeyA, includeRemoteBranches: true, Engine);
        var (outcome, rowCount) = staleWriter.AppendSpillBatch(RawRange(10, 10), from: 10, to: 20, exhausted: false);

        Assert.Equal(SpillOutcome.Adopted, outcome);
        Assert.Equal(20, rowCount);
        // Nothing was appended a second time.
        Assert.Equal(datLengthAfterFirst, new FileInfo(GraphCachePaths.File(Repo, GraphMetaStore.DatExt)).Length);
    }

    [Fact]
    public void A_batch_with_a_gap_before_the_headers_row_count_conflicts()
    {
        var writer = new GraphMetaWriter(Repo, WalkKeyA, includeRemoteBranches: true, Engine);
        writer.AppendSpillBatch(RawRange(0, 15), from: 0, to: 15, exhausted: false); // header RowCount now 15

        // A batch expecting to continue from row 10 — but the header is already past that, and
        // not far enough to cover this whole range either (15 is strictly between 10 and 20).
        var (outcome, _) = writer.AppendSpillBatch(RawRange(10, 10), from: 10, to: 20, exhausted: false);

        Assert.Equal(SpillOutcome.Conflict, outcome);
        Assert.Equal(15, ReadHeader().RowCount); // untouched by the conflicting attempt
    }

    [Fact]
    public void A_batch_at_a_different_walk_key_than_the_header_conflicts_when_not_starting_from_zero()
    {
        var writerA = new GraphMetaWriter(Repo, WalkKeyA, includeRemoteBranches: true, Engine);
        writerA.AppendSpillBatch(RawRange(0, 10), from: 0, to: 10, exhausted: false);

        // A different walk key trying to continue from row 10 (as if it thought it owned this
        // store) — the on-disk header belongs to a different walk entirely.
        var writerB = new GraphMetaWriter(Repo, WalkKeyB, includeRemoteBranches: true, Engine);
        var (outcome, rowCount) = writerB.AppendSpillBatch(RawRange(10, 10), from: 10, to: 20, exhausted: false);

        Assert.Equal(SpillOutcome.Conflict, outcome);
        Assert.Equal(WalkKeyA, ReadHeader().Fingerprint); // the other walk's header is untouched
    }

    [Fact]
    public void A_batch_at_a_different_walk_key_starting_from_zero_resets_and_appends()
    {
        var writerA = new GraphMetaWriter(Repo, WalkKeyA, includeRemoteBranches: true, Engine);
        writerA.AppendSpillBatch(RawRange(0, 10), from: 0, to: 10, exhausted: false);

        // A fresh walk under a new key, legitimately starting over from row 0 (e.g. a from-scratch
        // recompute) — this IS the reset case, not a conflict.
        var writerB = new GraphMetaWriter(Repo, WalkKeyB, includeRemoteBranches: true, Engine);
        var (outcome, rowCount) = writerB.AppendSpillBatch(RawRange(0, 5), from: 0, to: 5, exhausted: false);

        Assert.Equal(SpillOutcome.Appended, outcome);
        Assert.Equal(5, rowCount);
        var hdr = ReadHeader();
        Assert.Equal(WalkKeyB, hdr.Fingerprint);
        Assert.Equal(5, hdr.RowCount);
    }

    // ── TryCommitReorder: the atomic switch-over re-check ──

    [Fact]
    public void TryCommitReorder_succeeds_when_the_header_still_matches_what_the_reorder_started_from()
    {
        var original = new GraphMetaWriter(Repo, WalkKeyA, includeRemoteBranches: true, Engine);
        original.AppendSpillBatch(RawRange(0, 5), from: 0, to: 5, exhausted: true);
        var before = ReadHeader();

        var reorderWriter = new GraphMetaWriter(Repo, WalkKeyB, includeRemoteBranches: true, Engine);
        var order = new[] { 0, 1, 2, 3, 4, 5 }; // rows 0..4 reuse old records, row 5 is new
        var newRecords = RawRange(5, 1);
        // The new record is appended (streaming) BEFORE the commit, exactly as
        // GraphLayoutService.RunHeadlessReorderAsync does per batch.
        Assert.True(reorderWriter.AppendReorderBatch(newRecords, expectedWalkKey: before.Fingerprint));

        var ok = reorderWriter.TryCommitReorder(
            expectedOldWalkKey: before.Fingerprint, expectedOldExhausted: before.Exhausted,
            expectedOldOrderVersion: before.OrderVersion, expectedOldRecordCount: before.RecordCount,
            newRecordCount: newRecords.Count, order, sortedTips: [Oid(5)]);

        Assert.True(ok);
        var after = ReadHeader();
        Assert.Equal(WalkKeyB, after.Fingerprint);
        Assert.Equal(6, after.RowCount);
        Assert.Equal(6, after.RecordCount);
        Assert.Equal(before.OrderVersion + 1, after.OrderVersion);
        Assert.True(after.Exhausted);
    }

    [Fact]
    public void TryCommitReorder_abandons_when_the_header_moved_under_it()
    {
        var original = new GraphMetaWriter(Repo, WalkKeyA, includeRemoteBranches: true, Engine);
        original.AppendSpillBatch(RawRange(0, 5), from: 0, to: 5, exhausted: true);
        var before = ReadHeader(); // what the reorder "saw" when it started

        var reorderWriter = new GraphMetaWriter(Repo, WalkKeyB, includeRemoteBranches: true, Engine);
        var newRecords = RawRange(5, 1);
        Assert.True(reorderWriter.AppendReorderBatch(newRecords, expectedWalkKey: before.Fingerprint));

        // Another writer changes the store while the reorder's walk was still streaming in (off
        // any lock) — the header no longer has the RecordCount the reorder started from.
        original.AppendSpillBatch(RawRange(5, 2), from: 5, to: 7, exhausted: true);
        var mutated = ReadHeader();

        var ok = reorderWriter.TryCommitReorder(
            expectedOldWalkKey: before.Fingerprint, expectedOldExhausted: before.Exhausted,
            expectedOldOrderVersion: before.OrderVersion, expectedOldRecordCount: before.RecordCount,
            newRecordCount: newRecords.Count, order: [0, 1, 2, 3, 4, 5], sortedTips: [Oid(5)]);

        Assert.False(ok);
        // The old order stays authoritative — untouched by the abandoned attempt. The record
        // AppendReorderBatch already wrote above is now a harmless dead record (compaction
        // compacts these away later); it doesn't corrupt anything since nothing references it.
        var after = ReadHeader();
        Assert.Equal(mutated.Fingerprint, after.Fingerprint);
        Assert.Equal(mutated.RowCount, after.RowCount);
        Assert.Equal(mutated.RecordCount, after.RecordCount);
        Assert.Equal(mutated.OrderVersion, after.OrderVersion);
    }

    [Fact]
    public void AppendReorderBatch_refuses_to_write_once_the_walk_key_has_moved()
    {
        var original = new GraphMetaWriter(Repo, WalkKeyA, includeRemoteBranches: true, Engine);
        original.AppendSpillBatch(RawRange(0, 5), from: 0, to: 5, exhausted: true);
        var before = ReadHeader();

        // A different writer resets the store to a brand-new key entirely before this reorder's
        // batch gets a chance to append.
        var otherWriter = new GraphMetaWriter(Repo, WalkKeyB, includeRemoteBranches: true, Engine);
        otherWriter.AppendSpillBatch(RawRange(0, 3), from: 0, to: 3, exhausted: false);
        var datLengthAfterOtherWrite = new FileInfo(GraphCachePaths.File(Repo, GraphMetaStore.DatExt)).Length;

        var reorderWriter = new GraphMetaWriter(Repo, WalkKeyA, includeRemoteBranches: true, Engine);
        var ok = reorderWriter.AppendReorderBatch(RawRange(5, 1), expectedWalkKey: before.Fingerprint);

        Assert.False(ok);
        // Nothing appended on top of otherWriter's own write.
        Assert.Equal(datLengthAfterOtherWrite, new FileInfo(GraphCachePaths.File(Repo, GraphMetaStore.DatExt)).Length);
        Assert.Equal(WalkKeyB, ReadHeader().Fingerprint);
    }

    // ── cancel-on-focus ──

    [Fact]
    public async Task CancelInactiveTabAsync_cancels_a_queued_job_before_it_ever_runs()
    {
        using var repo = TestRepositoryBuilder.FromTemplate("seed-1", b => b.WriteFile("f.txt", "1\n").Stage().Commit("c1"));
        var writer = new GraphBackgroundWriter(new RecordingGitCliService(), _host.Settings);
        await using var ctxA = _host.Open(repo.Path);
        await using var ctxB = _host.Open(repo.Path);

        var blockA = new TaskCompletionSource();
        writer.EnqueueInactiveTab(ctxA, async ct => await blockA.Task.WaitAsync(ct));
        await Task.Delay(150); // let A start running and occupy the drain loop

        var bRan = false;
        writer.EnqueueInactiveTab(ctxB, ct => { bRan = true; return Task.CompletedTask; });

        var cancelTask = writer.CancelInactiveTabAsync(ctxB);
        Assert.False(cancelTask.IsCompleted); // B is still behind A in the drain loop

        blockA.SetResult(); // let A finish so the loop can reach (and skip) B

        await cancelTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(bRan, "a job cancelled while still queued must never actually run");
    }

    [Fact]
    public async Task CancelInactiveTabAsync_waits_for_a_running_jobs_own_exit()
    {
        using var repo = TestRepositoryBuilder.FromTemplate("seed-1", b => b.WriteFile("f.txt", "1\n").Stage().Commit("c1"));
        var writer = new GraphBackgroundWriter(new RecordingGitCliService(), _host.Settings);
        await using var ctx = _host.Open(repo.Path);

        var started = new TaskCompletionSource();
        var exited = false;
        writer.EnqueueInactiveTab(ctx, async ct =>
        {
            started.SetResult();
            try { await Task.Delay(Timeout.Infinite, ct); }
            finally { exited = true; }
        });

        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var cancelTask = writer.CancelInactiveTabAsync(ctx);
        await cancelTask.WaitAsync(TimeSpan.FromSeconds(5)); // must not complete before the job actually exits

        Assert.True(exited);
    }

    [Fact]
    public async Task CancelInactiveTabAsync_is_a_no_op_when_nothing_is_queued_or_running()
    {
        using var repo = TestRepositoryBuilder.FromTemplate("seed-1", b => b.WriteFile("f.txt", "1\n").Stage().Commit("c1"));
        var writer = new GraphBackgroundWriter(new RecordingGitCliService(), _host.Settings);
        await using var ctx = _host.Open(repo.Path);

        await writer.CancelInactiveTabAsync(ctx).WaitAsync(TimeSpan.FromSeconds(5));
    }

    // ── the race itself: a headless spool and a foreground spill on the same repo ──

    /// <summary>An inactive tab's headless spool and
    /// the focused tab's own spill both targeting the same repo's store at once, with tiny batches
    /// so the race window is wide open across many batch boundaries. Without the writer-lock
    /// re-check this reliably produces a duplicated or misordered store (failure
    /// scenario); with it, every writer either appends cleanly, adopts, or backs off — so the
    /// final store is always exactly the fixture's commit history, once.</summary>
    [Fact(Timeout = 60_000)]
    public async Task Concurrent_headless_spool_and_foreground_spill_never_duplicate_or_misorder_the_store()
    {
        using var repo = TestRepositoryBuilder.Create();
        const int commitCount = 60;
        for (var i = 0; i < commitCount; i++)
            repo.WriteFile("f.txt", $"{i}\n").Stage().Commit($"commit {i:D2}");

        await _host.Settings.UpdateAsync(s => s with { GraphMetadataFromDisk = true });

        GraphLayoutService.TestSpillBatchRowsOverride = 7; // force many batch boundaries over 60 commits
        try
        {
            var headlessSvc = new GraphLayoutService(_host.Settings);
            var foregroundSvc = new GraphLayoutService(_host.Settings);
            var query = new GraphQuery { RefScope = GraphRefScope.AllRefs };

            await using var headlessCtx = _host.Open(repo.Path);
            await using var foregroundCtx = _host.Open(repo.Path);

            var headlessTask = headlessSvc.RunHeadlessCacheAsync(headlessCtx, query, CancellationToken.None);
            var foregroundTask = Task.Run(async () =>
            {
                var result = await foregroundSvc.ComputeLayoutAsync(foregroundCtx, query, maxRows: commitCount);
                // Keep extending (which resumes MaybeStartBackgroundSpill) until the whole history
                // is laid out, so the foreground side's own background spill runs concurrently
                // with the headless one for as long as possible.
                while (!result.Layout.IsComplete)
                    result = await foregroundSvc.ExtendLayoutAsync(foregroundCtx, result, additionalRows: commitCount);
                return result;
            });

            await Task.WhenAll(headlessTask, foregroundTask);
            // Give both sides' background spill tasks (not awaited above) a moment to finish
            // whatever batch they were mid-way through.
            await Task.Delay(500);

            // Ground truth, unrelated to either writer above.
            await _host.Settings.UpdateAsync(s => s with { GraphMetadataFromDisk = false });
            var memSvc = new GraphLayoutService(_host.Settings);
            await using var memCtx = _host.Open(repo.Path);
            var expected = await memSvc.ComputeLayoutAsync(memCtx, query, maxRows: commitCount);
            Assert.Equal(commitCount, expected.Layout.RowCount);

            var probe = GraphMetaStore.ProbeExisting(repo.Path, includeRemoteBranches: true, WalkEngine.LibGit2);
            Assert.True(probe.Ok);
            using var store = DiskCommitStore.Open(repo.Path, probe.RowCount, probe.RecordCount, probe.OrderVersion);

            // No duplicated rows: every record's SHA is distinct.
            var shaIndex = store.BuildShaIndex();
            Assert.Equal(store.RecordCount, shaIndex.Count);

            // Rows equal an in-memory compute of the same history, in order — not just "no
            // duplicates" but the actual right commit at every row.
            Assert.Equal(commitCount, store.Count);
            for (var i = 0; i < store.Count; i++)
                Assert.Equal(expected.Layout.Commits[i].Id, store.ReadRaw(i).Id);
        }
        finally
        {
            GraphLayoutService.TestSpillBatchRowsOverride = 0;
        }
    }
}
