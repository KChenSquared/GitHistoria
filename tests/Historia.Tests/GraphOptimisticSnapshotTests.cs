namespace Historia.Tests;

/// <summary>§V10.1: the optimistic stale snapshot is a cold-launch-only crutch. Once any
/// verified compute has landed for a repo path this process, every later (re)bind — tab switch,
/// <see cref="GraphLayoutService.Forget"/> + reopen — must recompute directly rather than risk a
/// stale disk paint that a racing recompute could strand on screen.</summary>
public sealed class GraphOptimisticSnapshotTests : IAsyncDisposable
{
    private readonly MiniHost _host = new();
    private readonly TestRepositoryBuilder _repo;

    public GraphOptimisticSnapshotTests()
    {
        _repo = TestRepositoryBuilder.Create();
        for (var i = 0; i < 12; i++)
        {
            _repo.WriteFile("f.txt", $"{i}\n").Stage();
            _repo.Commit($"commit {i:D2}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        _repo.Dispose();
        await _host.DisposeAsync();
    }

    private static GraphQuery AllRefs => new() { RefScope = GraphRefScope.AllRefs };

    private static async Task WaitForWalkCacheAsync(string repoPath)
    {
        var path = GraphCachePaths.File(repoPath, ".json.gz");
        for (var i = 0; i < 100 && !File.Exists(path); i++)
            await Task.Delay(20);
        Assert.True(File.Exists(path), "walk cache was never persisted");
    }

    [Fact]
    public async Task Snapshot_is_suppressed_for_a_path_that_already_had_a_verified_compute()
    {
        var svc = new GraphLayoutService(_host.Settings);
        await using var ctx = _host.Open(_repo.Path);

        await svc.ComputeLayoutAsync(ctx, AllRefs);
        await WaitForWalkCacheAsync(_repo.Path);

        // A fresh service (a cold launch) still shows the stale snapshot from the same on-disk cache…
        var cold = new GraphLayoutService(_host.Settings);
        await using var coldCtx = _host.Open(_repo.Path);
        Assert.NotNull(cold.TryOptimisticSnapshot(coldCtx, AllRefs, maxRows: 50));

        // …but the service that already computed does not, even after Forget drops the in-memory
        // cache (mirrors a tab close / reopen or a RecomputeAsync that Forgot mid-rebind).
        svc.Forget(ctx);
        await using var rebindCtx = _host.Open(_repo.Path);
        Assert.Null(svc.TryOptimisticSnapshot(rebindCtx, AllRefs, maxRows: 50));
    }

    [Fact]
    public async Task A_cold_service_still_paints_the_snapshot_once()
    {
        var seed = new GraphLayoutService(_host.Settings);
        await using var seedCtx = _host.Open(_repo.Path);
        await seed.ComputeLayoutAsync(seedCtx, AllRefs);
        await WaitForWalkCacheAsync(_repo.Path);

        var svc = new GraphLayoutService(_host.Settings);
        await using var ctx = _host.Open(_repo.Path);

        var snap = svc.TryOptimisticSnapshot(ctx, AllRefs, maxRows: 50);

        Assert.NotNull(snap);
        Assert.True(snap!.Layout.RowCount > 0);
    }
}
