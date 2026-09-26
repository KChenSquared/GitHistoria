namespace Historia.Tests;

/// <summary>
/// A cache walks one tip set. When the tips move under a walk that already produced rows (a
/// fetch, a new branch), a walk over the new tips doesn't line up with those rows, so the cache
/// stops — no resume, no re-prime that skips rows, no disk write — until the next compute
/// rebuilds from row 0. Without this, the graph showed rows from two walks and the old walk key's
/// on-disk store received the new walk's rows.
/// Serial: the headless-reorder case sets the process-wide
/// <see cref="GraphLayoutService.TestReorderBatchObserved"/> seam.
/// </summary>
[Trait("tier", "git")]
[Collection("serial")]
public sealed class GraphWalkSupersedeTests : IAsyncDisposable
{
    private readonly StateDir _state = new();
    private readonly MiniHost _host = new();
    private readonly List<TestRepositoryBuilder> _repos = [];

    public async ValueTask DisposeAsync()
    {
        foreach (var r in _repos)
            r.Dispose();
        await _host.DisposeAsync();
        _state.Dispose();
    }

    private bool GitMissing => !_host.Locator.Current.Found;

    private static readonly GraphQuery AllRefs = new() { RefScope = GraphRefScope.AllRefs };

    private TestRepositoryBuilder Linear3000()
    {
        var repo = TestRepositoryBuilder.FromTemplate("linear-3000", b => b.LinearHistory(3_000));
        _repos.Add(repo);
        return repo;
    }

    private RepositoryContext Open(TestRepositoryBuilder repo)
    {
        var ctx = _host.Open(repo.Path);
        ctx.MarkFirstGraphPaintDone();
        ctx.MarkFirstFullLoadDone();
        return ctx;
    }

    /// <summary>A commit no earlier walk saw, on a new branch — it sorts first by date, so a
    /// walk over the new tips starts with it.</summary>
    private static ObjectId AddSideTip(TestRepositoryBuilder repo, string branch)
    {
        var side = repo.Git("commit-tree", "HEAD~5^{tree}", "-p", "HEAD~5", "-m", branch).Trim();
        repo.Git("update-ref", $"refs/heads/{branch}", side);
        return new ObjectId(side);
    }

    private static object GetCache(GraphLayoutService svc, RepositoryContext ctx)
    {
        var cwt = typeof(GraphLayoutService).GetField("_caches", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(svc)!;
        var args = new object?[] { ctx, null };
        Assert.True((bool)cwt.GetType().GetMethod("TryGetValue")!.Invoke(cwt, args)!);
        return args[1]!;
    }

    private static T Get<T>(object target, string property) =>
        (T)target.GetType().GetProperty(property)!.GetValue(target)!;

    private static int TotalWalked(object cache) =>
        Get<int>(cache, "SpilledRows") + ((System.Collections.ICollection)cache.GetType().GetProperty("Commits")!.GetValue(cache)!).Count;

    private static List<ObjectId> Ids(GraphLayoutResult r) =>
        Enumerable.Range(0, r.Layout.RowCount).Select(i => r.Layout.Commits[i].Id).ToList();

    private static async Task<long> CurrentWalkKeyAsync(RepositoryContext ctx, GraphQuery query)
    {
        var method = typeof(GraphLayoutService).GetMethod("TryGetRefStateAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
        var task = (Task)method.Invoke(null, [ctx, query, false, CancellationToken.None])!;
        await task;
        var state = (System.Runtime.CompilerServices.ITuple)task.GetType().GetProperty("Result")!.GetValue(task)!;
        return (long)state[3]!;
    }

    /// <summary>Open for the foreground spill of painted rows, never open for the background
    /// full-history spill — so the store holds exactly the painted rows and nothing races the
    /// test.</summary>
    private sealed class PaintOnlyGate : IBackgroundWorkGate
    {
        public bool IsOpen => true;
        public Task WaitUntilOpenAsync(CancellationToken ct = default) => Task.Delay(Timeout.Infinite, ct);
        public void ReportGraphSettled(RepositoryContext context) { }
    }

    private GraphLayoutService DiskService() =>
        new(_host.Settings, _host.Locator, _host.Provider.GetRequiredService<IGitCliService>(), new PaintOnlyGate());

    [Fact]
    public async Task Tip_change_mid_crawl_stops_the_walk_without_new_rows()
    {
        if (GitMissing)
            return;
        var repo = Linear3000();
        await _host.Settings.UpdateAsync(s => s with { GraphMetadataFromDisk = false });
        await using var ctx = Open(repo);
        var svc = new GraphLayoutService(_host.Settings, _host.Locator);

        var initial = await svc.ComputeLayoutAsync(ctx, AllRefs, maxRows: 100); // 2,100 walked
        var cache = GetCache(svc, ctx);
        var walked = TotalWalked(cache);
        var side = AddSideTip(repo, "side");

        var extended = await svc.ExtendLayoutAsync(ctx, initial, additionalRows: 400);
        Assert.True(Get<bool>(cache, "WalkSuperseded"));
        Assert.Null(cache.GetType().GetProperty("LiveWalk")!.GetValue(cache));
        Assert.Equal(walked, TotalWalked(cache));

        // Past everything walked: nothing more is pulled, foreground or background.
        var further = await svc.ExtendLayoutAsync(ctx, extended, additionalRows: 3_000);
        await svc.ExtendLayoutAsync(ctx, further, additionalRows: 3_000, background: true);
        Assert.Equal(walked, TotalWalked(cache));
        var ids = Ids(further);
        Assert.True(ids.Count <= walked);
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.DoesNotContain(side, ids);
        Assert.Equal(Ids(initial), ids.Take(initial.Layout.RowCount));
    }

    [Fact]
    public async Task Next_compute_after_a_supersede_rebuilds_from_row_zero()
    {
        if (GitMissing)
            return;
        var repo = Linear3000();
        await _host.Settings.UpdateAsync(s => s with { GraphMetadataFromDisk = false });
        await using var ctx = Open(repo);
        var svc = new GraphLayoutService(_host.Settings, _host.Locator);

        var initial = await svc.ComputeLayoutAsync(ctx, AllRefs, maxRows: 100);
        var stale = GetCache(svc, ctx);
        var side = AddSideTip(repo, "side");
        await svc.ExtendLayoutAsync(ctx, initial, additionalRows: 400);
        Assert.True(Get<bool>(stale, "WalkSuperseded"));

        var rebuilt = await svc.ComputeLayoutAsync(ctx, AllRefs, maxRows: 100);
        var cache = GetCache(svc, ctx);
        Assert.NotSame(stale, cache);
        Assert.False(Get<bool>(cache, "WalkSuperseded"));
        Assert.Equal(await CurrentWalkKeyAsync(ctx, AllRefs), Get<long>(cache, "WalkKey"));
        Assert.Equal(side, rebuilt.Layout.Commits[0].Id);

        await using var freshCtx = Open(repo);
        var expected = await new GraphLayoutService(_host.Settings, _host.Locator).ComputeLayoutAsync(freshCtx, AllRefs, maxRows: 100);
        Assert.Equal(Ids(expected), Ids(rebuilt));
    }

    [Fact]
    public async Task Superseded_walk_writes_nothing_to_disk()
    {
        if (GitMissing)
            return;
        var repo = Linear3000();
        await _host.Settings.UpdateAsync(s => s with { GraphMetadataFromDisk = true });
        await using var ctx = Open(repo);
        var svc = DiskService();

        var initial = await svc.ComputeLayoutAsync(ctx, AllRefs, maxRows: 100);
        var before = GraphMetaStore.ProbeExisting(repo.Path, includeRemoteBranches: true);
        Assert.True(before.Ok);
        Assert.Equal(100, before.RowCount);
        var paintedIds = Ids(initial);

        AddSideTip(repo, "side");
        var extended = await svc.ExtendLayoutAsync(ctx, initial, additionalRows: 400);
        await svc.ExtendLayoutAsync(ctx, extended, additionalRows: 3_000);
        Assert.True(Get<bool>(GetCache(svc, ctx), "WalkSuperseded"));

        var after = GraphMetaStore.ProbeExisting(repo.Path, includeRemoteBranches: true);
        Assert.Equal(before.WalkKey, after.WalkKey);
        Assert.Equal(before.RowCount, after.RowCount);
        Assert.False(after.Exhausted);
        using var store = DiskCommitStore.Open(repo.Path, after.RowCount);
        Assert.Equal(paintedIds, Enumerable.Range(0, store.Count).Select(i => store.ReadRaw(i).Id).ToList());
    }

    [Fact]
    public async Task Partial_store_adopted_before_the_gate_wait_primes_its_own_walk()
    {
        if (GitMissing)
            return;
        var repo = Linear3000();
        await _host.Settings.UpdateAsync(s => s with { GraphMetadataFromDisk = true });
        await using var ctx = Open(repo);
        var svc = DiskService();

        // A partial store (the painted 100 rows) for the current tips.
        await svc.ComputeLayoutAsync(ctx, AllRefs, maxRows: 100);
        var store = GraphMetaStore.ProbeExisting(repo.Path, includeRemoteBranches: true);
        Assert.True(store is { Ok: true, Exhausted: false, RowCount: 100 });
        svc.Forget(ctx);
        await ctx.RefSnapshots.GetAsync(); // the next compute finds these refs cached

        // The compute reads those refs, then waits on the gate; the tips move meanwhile.
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = ctx.WithRepositoryAsync(_ =>
        {
            entered.SetResult();
            release.Task.Wait();
            return 0;
        });
        await entered.Task;
        Task<GraphLayoutResult> compute;
        try
        {
            compute = svc.ComputeLayoutAsync(ctx, AllRefs, maxRows: 100);
            await Task.Delay(500);
            AddSideTip(repo, "side");
        }
        finally
        {
            release.SetResult();
        }
        var result = await compute;

        // The adopted rows are the store's walk, so the prime walks the store's tips.
        var cache = GetCache(svc, ctx);
        Assert.Equal(store.WalkKey, Get<long>(cache, "LiveWalkFingerprint"));
        Assert.Equal(store.WalkKey, Get<long>(cache, "WalkKey"));

        // The next step sees the new tips and stops; the store never receives their rows.
        await svc.ExtendLayoutAsync(ctx, result, additionalRows: 3_000);
        Assert.True(Get<bool>(cache, "WalkSuperseded"));
        var after = GraphMetaStore.ProbeExisting(repo.Path, includeRemoteBranches: true);
        Assert.Equal(store.WalkKey, after.WalkKey);
        Assert.Equal(100, after.RowCount);
    }

    private static SemaphoreSlim WalkLock(object cache) =>
        (SemaphoreSlim)cache.GetType().GetField("WalkLock")!.GetValue(cache)!;

    /// <summary>Holds the repo gate until <see cref="IAsyncDisposable.DisposeAsync"/>.</summary>
    private static async Task<IAsyncDisposable> HoldGateAsync(RepositoryContext ctx)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = ctx.WithRepositoryAsync(_ =>
        {
            entered.SetResult();
            release.Task.Wait();
            return 0;
        });
        await entered.Task;
        return new GateHold(release, held);
    }

    private sealed class GateHold(TaskCompletionSource release, Task held) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            release.TrySetResult();
            await held;
        }
    }

    private static async Task<(string[] Tips, long WalkKey)> CurrentTipsAsync(RepositoryContext ctx, GraphQuery query)
    {
        var method = typeof(GraphLayoutService).GetMethod("TryGetRefStateAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
        var task = (Task)method.Invoke(null, [ctx, query, false, CancellationToken.None])!;
        await task;
        var state = (System.Runtime.CompilerServices.ITuple)task.GetType().GetProperty("Result")!.GetValue(task)!;
        return ((string[])state[2]!, (long)state[3]!);
    }

    [Fact]
    public async Task A_prime_queued_behind_a_walk_that_ran_out_pulls_nothing_again()
    {
        if (GitMissing)
            return;
        var repo = TestRepositoryBuilder.FromTemplate("linear-60", b => b.LinearHistory(60));
        _repos.Add(repo);
        await _host.Settings.UpdateAsync(s => s with { GraphMetadataFromDisk = false });
        await using var ctx = Open(repo);
        var svc = new GraphLayoutService(_host.Settings, _host.Locator);

        // Two computes on one fresh cache both prepare a prime (nothing walked yet), then queue on
        // WalkLock. The first reads all 60 commits and closes its walk; the second must not start
        // another one that pulls them again.
        Task<GraphLayoutResult> a, b;
        SemaphoreSlim walkLock;
        await using (await HoldGateAsync(ctx))
        {
            a = svc.ComputeLayoutAsync(ctx, AllRefs, maxRows: 100);
            b = svc.ComputeLayoutAsync(ctx, AllRefs, maxRows: 100);
            walkLock = WalkLock(GetCache(svc, ctx));
            await walkLock.WaitAsync();
        }
        try { await Task.Delay(1_000); } // both prepare on the gate, then wait on WalkLock
        finally { walkLock.Release(); }
        var results = await Task.WhenAll(a, b);

        Assert.Equal(60, TotalWalked(GetCache(svc, ctx)));
        foreach (var r in results)
        {
            var ids = Ids(r);
            Assert.Equal(60, ids.Count);
            Assert.Equal(ids.Count, ids.Distinct().Count());
        }
    }

    [Fact]
    public async Task A_step_prepared_after_a_store_adoption_supersedes_on_new_tips()
    {
        if (GitMissing)
            return;
        var repo = Linear3000();
        await _host.Settings.UpdateAsync(s => s with { GraphMetadataFromDisk = true });
        await using var ctx = Open(repo);
        var svc = DiskService();

        // A partial store (the painted 100 rows) for the current tips.
        await svc.ComputeLayoutAsync(ctx, AllRefs, maxRows: 100);
        var store = GraphMetaStore.ProbeExisting(repo.Path, includeRemoteBranches: true);
        Assert.True(store is { Ok: true, Exhausted: false, RowCount: 100 });
        svc.Forget(ctx);

        // Compute A adopts the store on the gate and queues its prime on WalkLock.
        Task<GraphLayoutResult> a;
        object cache;
        SemaphoreSlim walkLock;
        await using (await HoldGateAsync(ctx))
        {
            a = svc.ComputeLayoutAsync(ctx, AllRefs, maxRows: 100);
            cache = GetCache(svc, ctx);
            walkLock = WalkLock(cache);
            await walkLock.WaitAsync();
        }
        Task<GraphLayoutResult> b;
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (TotalWalked(cache) < 100 && DateTime.UtcNow < deadline)
                await Task.Delay(20);
            Assert.Equal(100, TotalWalked(cache));

            // Compute B on the same cache sees new tips before A's prime ran.
            AddSideTip(repo, "side");
            var (_, newKey) = await CurrentTipsAsync(ctx, AllRefs);
            b = svc.ComputeLayoutAsync(ctx, AllRefs, maxRows: 100);
            await Task.Delay(1_000); // B prepares on the gate, then waits on WalkLock
        }
        finally { walkLock.Release(); }
        await Task.WhenAll(a, b);

        // B superseded the walk instead of priming the new tips over the adopted rows.
        Assert.True(Get<bool>(cache, "WalkSuperseded"));
        Assert.Equal(store.WalkKey, Get<long>(cache, "WalkKey"));
        Assert.NotEqual(await CurrentWalkKeyAsync(ctx, AllRefs), Get<long>(cache, "LiveWalkFingerprint"));
        var after = GraphMetaStore.ProbeExisting(repo.Path, includeRemoteBranches: true);
        Assert.Equal(store.WalkKey, after.WalkKey);
        Assert.Equal(100, after.RowCount);
    }

    /// <summary>An exhausted store for the fixture's original tips, written by a full spill.</summary>
    private async Task WriteExhaustedStoreAsync(TestRepositoryBuilder repo)
    {
        await using var ctx0 = Open(repo);
        var svc0 = new GraphLayoutService(_host.Settings, _host.Locator);
        await svc0.ComputeLayoutAsync(ctx0, AllRefs, maxRows: 100);
        var spill = (Task?)GetCache(svc0, ctx0).GetType().GetProperty("BackgroundSpillTask")!.GetValue(GetCache(svc0, ctx0));
        Assert.NotNull(spill);
        await spill.WaitAsync(TimeSpan.FromSeconds(60));
    }

    [Fact]
    public async Task Headless_reorder_keyed_on_tips_that_moved_before_it_started_writes_nothing()
    {
        if (GitMissing)
            return;
        var repo = Linear3000();
        await _host.Settings.UpdateAsync(s => s with { GraphMetadataFromDisk = true, EnableGraphSplice = false });
        await WriteExhaustedStoreAsync(repo);
        var old = GraphMetaStore.ProbeExisting(repo.Path, includeRemoteBranches: true);
        Assert.True(old is { Ok: true, Exhausted: true });

        // The probe saw one tip change; a second lands before the reorder's first step.
        await using var ctx = Open(repo);
        AddSideTip(repo, "side");
        var (probedTips, probedKey) = await CurrentTipsAsync(ctx, AllRefs);
        AddSideTip(repo, "second");

        var svc = new GraphLayoutService(_host.Settings, _host.Locator);
        var reorder = typeof(GraphLayoutService).GetMethod("RunHeadlessReorderAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)reorder.Invoke(svc, [ctx, AllRefs, ctx.RepositoryPath, probedTips, probedKey, CancellationToken.None])!;

        var after = GraphMetaStore.ProbeExisting(repo.Path, includeRemoteBranches: true);
        Assert.Equal(old.WalkKey, after.WalkKey);
        Assert.Equal(old.OrderVersion, after.OrderVersion);
        Assert.Equal(old.RowCount, after.RowCount);
    }

    [Fact]
    public async Task Headless_spool_keyed_on_tips_that_moved_before_it_started_writes_nothing()
    {
        if (GitMissing)
            return;
        var repo = Linear3000();
        await _host.Settings.UpdateAsync(s => s with { GraphMetadataFromDisk = true });
        await using var ctx = Open(repo);
        var (probedTips, probedKey) = await CurrentTipsAsync(ctx, AllRefs);
        AddSideTip(repo, "side");

        var svc = new GraphLayoutService(_host.Settings, _host.Locator);
        var spool = typeof(GraphLayoutService).GetMethod("RunHeadlessSpoolAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var task = (Task<int>)spool.Invoke(svc, [ctx, AllRefs, probedTips, probedKey, CancellationToken.None, true, null])!;

        Assert.Equal(0, await task);
        Assert.False(GraphMetaStore.ProbeExisting(repo.Path, includeRemoteBranches: true).Ok);
    }

    [Fact]
    public async Task Headless_reorder_abandons_when_the_tips_move_mid_reorder()
    {
        if (GitMissing)
            return;
        var repo = Linear3000();
        await _host.Settings.UpdateAsync(s => s with { GraphMetadataFromDisk = true, EnableGraphSplice = false });

        // An exhausted store for the original tips.
        await using (var ctx0 = Open(repo))
        {
            var svc0 = new GraphLayoutService(_host.Settings, _host.Locator);
            await svc0.ComputeLayoutAsync(ctx0, AllRefs, maxRows: 100);
            var spill = (Task?)GetCache(svc0, ctx0).GetType().GetProperty("BackgroundSpillTask")!.GetValue(GetCache(svc0, ctx0));
            Assert.NotNull(spill);
            await spill.WaitAsync(TimeSpan.FromSeconds(60));
        }
        var old = GraphMetaStore.ProbeExisting(repo.Path, includeRemoteBranches: true);
        Assert.True(old is { Ok: true, Exhausted: true });

        // New tips → the headless pass reorders; a second change lands after its first batch.
        AddSideTip(repo, "side");
        var batches = 0;
        GraphLayoutService.TestSpillBatchRowsOverride = 500;
        GraphLayoutService.TestReorderBatchObserved = _ =>
        {
            if (Interlocked.Increment(ref batches) == 1)
                AddSideTip(repo, "second");
        };
        try
        {
            await using var ctx = Open(repo);
            var svc = new GraphLayoutService(_host.Settings, _host.Locator);
            await svc.RunHeadlessCacheAsync(ctx, AllRefs, CancellationToken.None);
        }
        finally
        {
            GraphLayoutService.TestReorderBatchObserved = null;
            GraphLayoutService.TestSpillBatchRowsOverride = 0;
        }

        Assert.True(batches >= 1, "the reorder should have started");
        var after = GraphMetaStore.ProbeExisting(repo.Path, includeRemoteBranches: true);
        Assert.Equal(old.WalkKey, after.WalkKey);
        Assert.Equal(old.OrderVersion, after.OrderVersion);
        Assert.Equal(old.RowCount, after.RowCount);
    }
}
