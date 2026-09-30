namespace Historia.Tests;

/// <summary>
/// A tab's headless cache job and its own foreground load can overlap (focus only requests the
/// job's cancellation). Whatever the interleaving, every painted layout is the real history and
/// the on-disk store never holds a row twice.
/// </summary>
[Trait("tier", "git")]
public sealed class GraphFocusedHeadlessRaceTests : IAsyncDisposable
{
    private readonly MiniHost _host = new();
    private readonly List<TestRepositoryBuilder> _repos = [];

    private const int BaseCommits = 300;
    private const int NewCommits = 4;
    private static readonly GraphQuery Q = new() { RefScope = GraphRefScope.AllRefs };

    public async ValueTask DisposeAsync()
    {
        foreach (var r in _repos)
            r.Dispose();
        await _host.DisposeAsync();
    }

    private TestRepositoryBuilder NewRepo(string template, int commits)
    {
        var repo = TestRepositoryBuilder.FromTemplate(template, b => b.LinearHistory(commits));
        _repos.Add(repo);
        return repo;
    }

    private static List<GitObjectId> Ids(GraphLayoutResult r) =>
        Enumerable.Range(0, r.Layout.RowCount).Select(i => r.Layout.Commits[i].Id).ToList();

    private async Task<List<GitObjectId>> InMemoryOrderAsync(string repoPath)
    {
        await _host.Settings.UpdateAsync(s => s with { GraphMetadataFromDisk = false });
        var memSvc = new GraphLayoutService(_host.Settings, _host.Locator);
        await using var memCtx = _host.Open(repoPath);
        var ids = Ids(await memSvc.ComputeLayoutAsync(memCtx, Q));
        await _host.Settings.UpdateAsync(s => s with { GraphMetadataFromDisk = true });
        return ids;
    }

    private static void AssertPaintedRows(GraphLayoutResult layout, List<GitObjectId> expected)
    {
        var ids = Ids(layout);
        Assert.Equal(expected.Take(ids.Count), ids);
        for (var i = 0; i < layout.Layout.RowCount; i++)
        {
            if (layout.Layout.Commits[i].ParentCount == 0)
                continue;
            var lines = layout.Layout.GetRowSegments(i).Length + layout.Layout.GetRowElbows(i).Length;
            Assert.True(lines > 0, $"row {i} has parents but paints no lines");
        }
    }

    private static (bool Ok, bool Exhausted, int RowCount, int RecordCount, int OrderVersion) Header(string repoPath)
    {
        var p = GraphMetaStore.ProbeExisting(repoPath, includeRemoteBranches: true);
        return (p.Ok, p.Exhausted, p.RowCount, p.RecordCount, p.OrderVersion);
    }

    [Theory(Timeout = 120_000)]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(100)]
    [InlineData(300)]
    [InlineData(700)]
    [InlineData(1500)]
    public async Task Foreground_load_during_a_headless_job_leaves_a_clean_store(int cancelAfterMs)
    {
        var repo = NewRepo("linear-300", BaseCommits);
        await _host.Settings.UpdateAsync(s => s with { GraphMetadataFromDisk = true });
        var svc = new GraphLayoutService(_host.Settings, _host.Locator);

        GraphLayoutService.TestSpillBatchRowsOverride = 20;
        try
        {
            await using (var spool = _host.Open(repo.Path))
                await svc.RunHeadlessCacheAsync(spool, Q, CancellationToken.None);

            repo.Checkout("feature", create: true);
            for (var i = 0; i < NewCommits; i++)
                repo.WriteFile("f.txt", $"feature {i}\n").Stage().Commit($"feature commit {i}");
            var expected = await InMemoryOrderAsync(repo.Path);
            Assert.Equal(BaseCommits + NewCommits, expected.Count);

            await using var ctx = _host.Open(repo.Path);
            ctx.MarkFirstGraphPaintDone();
            ctx.MarkFirstFullLoadDone();
            using var cts = new CancellationTokenSource();
            var headless = Task.Run(async () =>
            {
                try { await svc.RunHeadlessCacheAsync(ctx, Q, cts.Token); }
                catch { /* cancelled or superseded by the foreground load: the outcome is the store */ }
            });
            if (cancelAfterMs >= 0)
                _ = Task.Run(async () => { await Task.Delay(cancelAfterMs); cts.Cancel(); });

            await Task.Delay(50);
            var layout = await svc.ComputeLayoutAsync(ctx, Q, 50);
            AssertPaintedRows(layout, expected);
            for (var i = 0; i < 40 && layout.Layout.RowCount < expected.Count; i++)
            {
                layout = await svc.ExtendLayoutAsync(ctx, layout, additionalRows: 100);
                AssertPaintedRows(layout, expected);
            }
            Assert.Equal(expected.Count, layout.Layout.RowCount);
            await headless;

            // Give the tab's own background spill time to finish before reading the store back.
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (Header(repo.Path).RowCount < expected.Count && DateTime.UtcNow < deadline)
                await Task.Delay(50);

            var header = Header(repo.Path);
            Assert.True(header.Ok);
            using var store = DiskCommitStore.Open(repo.Path, header.RowCount, header.RecordCount, header.OrderVersion);
            var stored = Enumerable.Range(0, store.Count).Select(i => store.ReadRaw(i).Id).ToList();
            Assert.Equal(stored.Count, stored.Distinct().Count());
            Assert.Equal(expected.Take(stored.Count), stored);
            if (header.OrderVersion == 0)
                Assert.Equal(header.RowCount, header.RecordCount);
            if (header.Exhausted)
                Assert.Equal(expected, stored);

            // A fresh service reopening the repo paints the whole history once.
            var fresh = new GraphLayoutService(_host.Settings, _host.Locator);
            await using var reopened = _host.Open(repo.Path);
            reopened.MarkFirstGraphPaintDone();
            reopened.MarkFirstFullLoadDone();
            var again = await fresh.ComputeLayoutAsync(reopened, Q);
            Assert.Equal(expected, Ids(again));
        }
        finally
        {
            GraphLayoutService.TestSpillBatchRowsOverride = 0;
        }
    }

    /// <summary>Open for the foreground spill of painted rows; nothing else ever starts, so a
    /// headless job enqueued through the service stays queued for the whole test.</summary>
    private sealed class QueuedOnlyGate : IBackgroundWorkGate
    {
        public bool IsOpen => true;
        public Task WaitUntilOpenAsync(CancellationToken ct = default) => Task.Delay(Timeout.Infinite, ct);
        public void ReportGraphSettled(RepositoryContext context) { }
    }

    [Fact(Timeout = 60_000)]
    public async Task Foreground_spill_stands_aside_while_a_headless_job_is_queued()
    {
        var repo = NewRepo("linear-300", BaseCommits);
        var expected = await InMemoryOrderAsync(repo.Path);
        var svc = new GraphLayoutService(
            _host.Settings, _host.Locator, _host.Provider.GetRequiredService<IGitCliService>(), new QueuedOnlyGate());
        await using var ctx = _host.Open(repo.Path);
        ctx.MarkFirstGraphPaintDone();
        ctx.MarkFirstFullLoadDone();

        svc.EnqueueInactiveTabCaching(ctx, Q);
        var layout = await svc.ComputeLayoutAsync(ctx, Q, 50);

        AssertPaintedRows(layout, expected);
        Assert.False(GraphMetaStore.ProbeExisting(repo.Path, includeRemoteBranches: true).Ok,
            "the foreground load must not write while a headless job for its tab is queued");

        await svc.CancelInactiveTabCachingAsync(ctx).WaitAsync(TimeSpan.FromSeconds(10));
        var next = await svc.ExtendLayoutAsync(ctx, layout, additionalRows: 100);
        AssertPaintedRows(next, expected);
        Assert.True(GraphMetaStore.ProbeExisting(repo.Path, includeRemoteBranches: true).Ok,
            "with the job gone the foreground load writes again");
    }
}
