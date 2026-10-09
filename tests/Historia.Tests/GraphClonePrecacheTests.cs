namespace Historia.Tests;
[Trait("tier", "git")]

public sealed class GraphClonePrecacheTests : IAsyncDisposable
{
    private readonly MiniHost _host = new();
    private readonly TestRepositoryBuilder _repo;

    private const int CommitCount = 20;

    public GraphClonePrecacheTests()
    {
        _repo = TestRepositoryBuilder.FromTemplate("linear-20", b => b.LinearHistory(CommitCount));
    }

    public async ValueTask DisposeAsync()
    {
        _repo.Dispose();
        await _host.DisposeAsync();
    }

    private async Task<GraphLayoutService> DiskBackedServiceAsync(IBackgroundWorkGate? gate = null)
    {
        await _host.Settings.UpdateAsync(s => s with { GraphMetadataFromDisk = true });
        var locator = _host.Provider.GetRequiredService<IGitExecutableLocator>();
        var cli = _host.Provider.GetRequiredService<IGitCliService>();
        return gate is not null
            ? new GraphLayoutService(_host.Settings, locator, cli, gate)
            : new GraphLayoutService(_host.Settings, locator, cli);
    }

    private static GraphQuery Q => new() { RefScope = GraphRefScope.AllRefs, IncludeRemoteBranches = true };

    [Fact]
    public async Task Precache_leaves_an_exhausted_store_the_tab_walk_reuses()
    {
        var svc = await DiskBackedServiceAsync();
        await using var ctx = _host.Open(_repo.Path);
        await svc.PrecacheClonedRepositoryAsync(ctx, Q, null, CancellationToken.None);

        var datPath = GraphCachePaths.File(_repo.Path, GraphMetaStore.DatExt);
        Assert.True(File.Exists(datPath));
        var before = File.GetLastWriteTimeUtc(datPath);
        var beforeBytes = await File.ReadAllBytesAsync(datPath);

        await using var ctx2 = _host.Open(_repo.Path);
        await svc.RunHeadlessCacheAsync(ctx2, Q, CancellationToken.None);

        var after = File.GetLastWriteTimeUtc(datPath);
        var afterBytes = await File.ReadAllBytesAsync(datPath);
        Assert.Equal(before, after);
        Assert.Equal(beforeBytes, afterBytes);
    }

    [Fact]
    public async Task Precache_writes_a_commit_graph_below_30k_commits()
    {
        var svc = await DiskBackedServiceAsync();
        await using var ctx = _host.Open(_repo.Path);

        Assert.False(GraphBackgroundWriter.HasCommitGraph(ctx.GitDirectory));

        await svc.PrecacheClonedRepositoryAsync(ctx, Q, null, CancellationToken.None);

        Assert.True(GraphBackgroundWriter.HasCommitGraph(ctx.GitDirectory));
    }

    [Fact]
    public async Task Precache_does_not_wait_for_a_closed_background_gate()
    {
        var closedGate = new BlockingGate();
        var svc = await DiskBackedServiceAsync(closedGate);
        await using var ctx = _host.Open(_repo.Path);

        var precacheTask = svc.PrecacheClonedRepositoryAsync(ctx, Q, null, CancellationToken.None);
        var completed = await Task.WhenAny(precacheTask, Task.Delay(TimeSpan.FromSeconds(10)));

        Assert.Same(precacheTask, completed);
        await precacheTask;

        var hdrPath = GraphCachePaths.File(_repo.Path, GraphMetaStore.HdrExt);
        Assert.True(File.Exists(hdrPath));
    }

    [Fact]
    public async Task Precache_cancelled_mid_spool_keeps_the_durable_prefix()
    {
        GraphLayoutService.TestSpillBatchRowsOverride = 5;
        try
        {
            var svc = await DiskBackedServiceAsync();
            await using var ctx = _host.Open(_repo.Path);

            using var cts = new CancellationTokenSource();
            var progress = new SynchronousProgress<GitProgress>(p =>
            {
                if (p.Phase == CloneStatus.CachingPhase && p.ObjectsDone > 0)
                    cts.Cancel();
            });

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => svc.PrecacheClonedRepositoryAsync(ctx, Q, progress, cts.Token));

            var hdrPath = GraphCachePaths.File(_repo.Path, GraphMetaStore.HdrExt);
            Assert.True(File.Exists(hdrPath));

            using var doc = JsonDocument.Parse(File.ReadAllBytes(hdrPath));
            Assert.False(doc.RootElement.GetProperty("Exhausted").GetBoolean());
            Assert.True(doc.RootElement.GetProperty("RowCount").GetInt32() > 0);
        }
        finally
        {
            GraphLayoutService.TestSpillBatchRowsOverride = 0;
        }
    }

    [Fact]
    public async Task Precache_reports_commit_graph_then_caching_phases_in_order()
    {
        var svc = await DiskBackedServiceAsync();
        await using var ctx = _host.Open(_repo.Path);

        var reports = new List<GitProgress>();
        var progress = new SynchronousProgress<GitProgress>(p => reports.Add(p));

        await svc.PrecacheClonedRepositoryAsync(ctx, Q, progress, CancellationToken.None);

        Assert.True(reports.Count >= 2);
        Assert.Equal(CloneStatus.CommitGraphPhase, reports[0].Phase);
        Assert.Equal(CloneStatus.CachingPhase, reports[1].Phase);
        Assert.Equal(0, reports[1].ObjectsDone);

        for (var i = 2; i < reports.Count; i++)
        {
            Assert.Equal(CloneStatus.CachingPhase, reports[i].Phase);
            Assert.True(reports[i].ObjectsDone > 0);
        }
    }

    [Fact]
    public async Task Precache_on_a_shallow_clone_succeeds_without_a_commit_graph()
    {
        var cli = _host.Provider.GetRequiredService<IGitCliService>();
        var shallowDir = TestScratch.NewPath("historia-shallow-");
        try
        {
            var cloneRes = await cli.RunAsync(new GitCliRequest
            {
                Arguments = ["clone", "--depth", "1", $"file://{_repo.Path.Replace('\\', '/')}", shallowDir],
                WorkingDirectory = Path.GetTempPath(),
            });
            Assert.True(cloneRes.IsSuccess);

            await using var ctx = _host.Open(shallowDir);
            var svc = await DiskBackedServiceAsync();
            await svc.PrecacheClonedRepositoryAsync(ctx, Q, null, CancellationToken.None);

            Assert.False(GraphBackgroundWriter.HasCommitGraph(ctx.GitDirectory));
            var hdrPath = GraphCachePaths.File(shallowDir, GraphMetaStore.HdrExt);
            Assert.True(File.Exists(hdrPath));
        }
        finally
        {
            TestScratch.Delete(shallowDir);
        }
    }

    private sealed class BlockingGate : IBackgroundWorkGate
    {
        private readonly TaskCompletionSource _tcs = new();
        public bool IsOpen => _tcs.Task.IsCompleted;
        public Task WaitUntilOpenAsync(CancellationToken ct = default) => _tcs.Task.WaitAsync(ct);
        public void ReportGraphSettled(RepositoryContext context) { }
        public void Open() => _tcs.TrySetResult();
    }
}
