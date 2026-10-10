namespace Historia.Tests;

[Trait("tier", "git")]

public sealed class GraphSpillExhaustionTests : IAsyncDisposable
{
    private readonly StateDir _state = new();
    private readonly MiniHost _host = new();

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
        _state.Dispose();
    }

    private static Task? GetBackgroundSpillTask(GraphLayoutService svc, RepositoryContext ctx)
    {
        var cachesField = typeof(GraphLayoutService).GetField("_caches", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var cwt = cachesField.GetValue(svc)!;
        var tryGetValue = cwt.GetType().GetMethod("TryGetValue")!;
        var args = new object?[] { ctx, null };
        var found = (bool)tryGetValue.Invoke(cwt, args)!;
        Assert.True(found, "expected an existing RepoCache for this context");
        var cache = args[1]!;
        return (Task?)cache.GetType().GetProperty("BackgroundSpillTask", BindingFlags.Public | BindingFlags.Instance)!.GetValue(cache);
    }

    [Fact(Timeout = 30_000)]
    public async Task Background_spill_writes_the_whole_history_past_the_painted_rows()
    {
        const int commitCount = 60;
        using var repo = TestRepositoryBuilder.FromTemplate("linear-60", b => b.LinearHistory(commitCount));

        await _host.Settings.UpdateAsync(s => s with { GraphMetadataFromDisk = true });

        GraphLayoutService.TestSpillBatchRowsOverride = 7;
        try
        {
            var svc = new GraphLayoutService(_host.Settings, _host.Locator);
            await using var ctx = _host.Open(repo.Path);
            ctx.MarkFirstGraphPaintDone();
            ctx.MarkFirstFullLoadDone();

            var query = new GraphQuery { RefScope = GraphRefScope.AllRefs };
            var result = await svc.ComputeLayoutAsync(ctx, query, maxRows: 5);
            Assert.Equal(5, result.Layout.RowCount);

            var spillTask = GetBackgroundSpillTask(svc, ctx);
            Assert.NotNull(spillTask);
            await spillTask.WaitAsync(TimeSpan.FromSeconds(15));

            var probe = GraphMetaStore.ProbeExisting(repo.Path, includeRemoteBranches: true);
            Assert.True(probe.Ok, "meta store header should exist");
            Assert.True(probe.Exhausted, "background spill should have walked the whole history to exhaustion");
            Assert.Equal(commitCount, probe.RowCount);
            Assert.Equal(5, result.Layout.RowCount);
        }
        finally
        {
            GraphLayoutService.TestSpillBatchRowsOverride = 0;
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task Extend_after_an_exhausted_spill_reads_rows_from_disk()
    {
        const int commitCount = 60;
        using var repo = TestRepositoryBuilder.FromTemplate("linear-60", b => b.LinearHistory(commitCount));

        // Expected ground truth from memory walk
        await _host.Settings.UpdateAsync(s => s with { GraphMetadataFromDisk = false });
        var memSvc = new GraphLayoutService(_host.Settings, _host.Locator);
        await using var memCtx = _host.Open(repo.Path);
        var query = new GraphQuery { RefScope = GraphRefScope.AllRefs };
        var expected = await memSvc.ComputeLayoutAsync(memCtx, query, maxRows: commitCount);
        Assert.Equal(commitCount, expected.Layout.RowCount);

        // Disk-backed test
        await _host.Settings.UpdateAsync(s => s with { GraphMetadataFromDisk = true });
        GraphLayoutService.TestSpillBatchRowsOverride = 7;
        try
        {
            var svc = new GraphLayoutService(_host.Settings, _host.Locator);
            await using var ctx = _host.Open(repo.Path);
            ctx.MarkFirstGraphPaintDone();
            ctx.MarkFirstFullLoadDone();

            var initial = await svc.ComputeLayoutAsync(ctx, query, maxRows: 5);
            Assert.Equal(5, initial.Layout.RowCount);

            var spillTask = GetBackgroundSpillTask(svc, ctx);
            Assert.NotNull(spillTask);
            await spillTask.WaitAsync(TimeSpan.FromSeconds(15));

            var probe = GraphMetaStore.ProbeExisting(repo.Path, includeRemoteBranches: true);
            Assert.True(probe.Ok);
            Assert.True(probe.Exhausted);
            Assert.Equal(commitCount, probe.RowCount);

            // Extend past the initial 5 rows after exhaustion
            var extended = await svc.ExtendLayoutAsync(ctx, initial, additionalRows: 30);
            Assert.Equal(35, extended.Layout.RowCount);

            for (var i = 0; i < 35; i++)
                Assert.Equal(expected.Layout.Commits[i].Id, extended.Layout.Commits[i].Id);

            // Extend the rest to the end of history
            var full = await svc.ExtendLayoutAsync(ctx, extended, additionalRows: commitCount);
            Assert.Equal(commitCount, full.Layout.RowCount);

            for (var i = 0; i < commitCount; i++)
                Assert.Equal(expected.Layout.Commits[i].Id, full.Layout.Commits[i].Id);
        }
        finally
        {
            GraphLayoutService.TestSpillBatchRowsOverride = 0;
        }
    }
}
