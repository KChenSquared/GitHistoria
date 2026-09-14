using System.Reflection;

namespace Historia.Tests;

/// <summary>
/// Once rows are confirmed durable in the
/// §7.6 disk store, they are trimmed out of the resident pending buffer
/// (<c>RepoCache.Commits</c>) instead of staying resident for the rest of the session. These
/// tests drive the real <see cref="GraphLayoutService"/> code path, using reflection only to
/// peek at the private <c>RepoCache</c>'s bookkeeping fields (<c>SpilledRows</c>, <c>Commits</c>)
/// — the same pattern <see cref="GraphWalkResumeAfterPartialCacheTests"/> uses.
/// </summary>
public sealed class GraphSpoolTests : IAsyncDisposable
{
    private readonly MiniHost _host = new();
    private readonly TestRepositoryBuilder _repo;

    public GraphSpoolTests() => _repo = TestRepositoryBuilder.Create();

    public async ValueTask DisposeAsync()
    {
        _repo.Dispose();
        await _host.DisposeAsync();
    }

    private void SeedCommits(int n)
    {
        for (var i = 0; i < n; i++)
            _repo.WriteFile("f.txt", $"{i}\n").Stage().Commit($"commit {i:D3}");
    }

    private static object GetCache(GraphLayoutService svc, RepositoryContext ctx)
    {
        var cachesField = typeof(GraphLayoutService).GetField("_caches", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var cwt = cachesField.GetValue(svc)!;
        var tryGetValue = cwt.GetType().GetMethod("TryGetValue")!;
        var args = new object?[] { ctx, null };
        var found = (bool)tryGetValue.Invoke(cwt, args)!;
        Assert.True(found, "expected an existing RepoCache for this context");
        return args[1]!;
    }

    private static int SpilledRows(object cache) =>
        (int)cache.GetType().GetProperty("SpilledRows", BindingFlags.Public | BindingFlags.Instance)!.GetValue(cache)!;

    private static int ResidentCount(object cache)
    {
        var commits = (System.Collections.IList)cache.GetType()
            .GetProperty("Commits", BindingFlags.Public | BindingFlags.Instance)!.GetValue(cache)!;
        return commits.Count;
    }

    private async Task<GraphLayoutService> DiskBackedServiceAsync()
    {
        await _host.Settings.UpdateAsync(s => s with { GraphMetadataFromDisk = true });
        return new GraphLayoutService(_host.Settings);
    }

    [Fact]
    public async Task Foreground_spill_trims_only_the_confirmed_prefix_leaving_the_rest_resident()
    {
        const int total = 30;
        const int window = 10;
        SeedCommits(total);

        var svc = await DiskBackedServiceAsync();
        await using var ctx = _host.Open(_repo.Path);

        var result = await svc.ComputeLayoutAsync(ctx, new GraphQuery { RefScope = GraphRefScope.AllRefs }, maxRows: window);
        Assert.Equal(window, result.Layout.RowCount);

        var cache = GetCache(svc, ctx);
        // The synchronous SpillAndSwap inside this very call spilled and confirmed exactly the
        // visible window — everything walked beyond it (the rest of this small history, since
        // the walk-overscan pulls it all in one shot) stays resident until the background spool
        // catches up.
        Assert.Equal(window, SpilledRows(cache));
        Assert.Equal(total - window, ResidentCount(cache));

        svc.Forget(ctx);
    }

    [Fact]
    public async Task Pending_buffer_is_trimmed_to_empty_once_the_background_spill_exhausts_the_history()
    {
        const int total = 40;
        SeedCommits(total);

        var svc = await DiskBackedServiceAsync();
        await using var ctx = _host.Open(_repo.Path);
        ctx.MarkFirstGraphPaintDone(); ctx.MarkFirstFullLoadDone(); // stand in for the real load signals — opens the gate

        // Small visible window — the rest must be walked, spilled, AND trimmed out of the
        // resident pending buffer by the background spool on its own.
        await svc.ComputeLayoutAsync(ctx, new GraphQuery { RefScope = GraphRefScope.AllRefs }, maxRows: 5);

        var cache = GetCache(svc, ctx);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline && SpilledRows(cache) < total)
            await Task.Delay(50);

        Assert.Equal(total, SpilledRows(cache));
        // Once every row is confirmed durable, none of it needs to stay
        // resident — this is what bounds RepoCache.Commits at "pre-gate rows + one batch" instead
        // of the whole walked history.
        Assert.Equal(0, ResidentCount(cache));

        svc.Forget(ctx);
    }

    [Fact]
    public async Task Reading_rows_split_across_the_disk_prefix_and_the_resident_buffer_matches_an_in_memory_compute()
    {
        const int total = 25;
        const int window = 8;
        SeedCommits(total);

        // Ground truth: the same history laid out entirely in memory (no spilling at all).
        await _host.Settings.UpdateAsync(s => s with { GraphMetadataFromDisk = false });
        var memSvc = new GraphLayoutService(_host.Settings);
        await using var memCtx = _host.Open(_repo.Path);
        var expected = await memSvc.ComputeLayoutAsync(memCtx, new GraphQuery { RefScope = GraphRefScope.AllRefs });
        Assert.Equal(total, expected.Layout.RowCount);

        // Disk-backed: the first `window` rows are confirmed-spilled (and trimmed out of the
        // resident buffer — see the test above), the rest of the history stays resident. A
        // second, wider compute must stitch both sources back into the SAME row order and
        // content — this is exactly GraphLayoutService.ReadWindow's job.
        var diskSvc = await DiskBackedServiceAsync();
        await using var diskCtx = _host.Open(_repo.Path);
        await diskSvc.ComputeLayoutAsync(diskCtx, new GraphQuery { RefScope = GraphRefScope.AllRefs }, maxRows: window);

        var cache = GetCache(diskSvc, diskCtx);
        Assert.Equal(window, SpilledRows(cache));
        Assert.True(ResidentCount(cache) > 0, "the rest of this small history should still be resident, not yet background-spilled");

        var actual = await diskSvc.ComputeLayoutAsync(diskCtx, new GraphQuery { RefScope = GraphRefScope.AllRefs });
        Assert.Equal(total, actual.Layout.RowCount);

        for (var i = 0; i < total; i++)
            Assert.Equal(expected.Layout.Commits[i].Id, actual.Layout.Commits[i].Id);
        for (var i = 0; i < Math.Min(total, expected.Layout.Rows.Length); i++)
            Assert.Equal(expected.Layout.Rows[i].LaneIndex, actual.Layout.Rows[i].LaneIndex);

        diskSvc.Forget(diskCtx);
    }
}
