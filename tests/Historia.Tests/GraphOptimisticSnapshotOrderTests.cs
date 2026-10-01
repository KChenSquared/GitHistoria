namespace Historia.Tests;

/// <summary>
/// The optimistic first frame reads a store the way its header says to: a store a headless reorder
/// rewrote is laid out in the reorder's row order, not in record order.
/// </summary>
[Trait("tier", "git")]
public sealed class GraphOptimisticSnapshotOrderTests : IAsyncDisposable
{
    private readonly MiniHost _host = new();
    private readonly TestRepositoryBuilder _repo = TestRepositoryBuilder.FromTemplate("linear-20", b => b.LinearHistory(CommitCount));

    private const int CommitCount = 20;
    private static readonly GraphQuery Q = new() { RefScope = GraphRefScope.AllRefs };

    public async ValueTask DisposeAsync()
    {
        _repo.Dispose();
        await _host.DisposeAsync();
    }

    [Fact(Timeout = 60_000)]
    public async Task Snapshot_of_a_reordered_store_follows_the_order_file()
    {
        await _host.Settings.UpdateAsync(s => s with { GraphMetadataFromDisk = true });
        var svc = new GraphLayoutService(_host.Settings, _host.Locator);
        await using (var ctx = _host.Open(_repo.Path))
            await svc.RunHeadlessCacheAsync(ctx, Q, CancellationToken.None);

        _repo.Checkout("feature", create: true);
        _repo.WriteFile("f.txt", "feature 1\n").Stage().Commit("feature commit 1");
        _repo.WriteFile("f.txt", "feature 2\n").Stage().Commit("feature commit 2");
        await using (var ctx2 = _host.Open(_repo.Path))
            await svc.RunHeadlessCacheAsync(ctx2, Q, CancellationToken.None);

        var probe = GraphMetaStore.ProbeExisting(_repo.Path, includeRemoteBranches: true);
        Assert.True(probe.Ok);
        Assert.True(probe.OrderVersion > 0, "expected the reorder's order file to be in effect");

        await _host.Settings.UpdateAsync(s => s with { GraphMetadataFromDisk = false });
        var memSvc = new GraphLayoutService(_host.Settings, _host.Locator);
        await using var memCtx = _host.Open(_repo.Path);
        var expected = await memSvc.ComputeLayoutAsync(memCtx, Q);
        Assert.Equal(CommitCount + 2, expected.Layout.RowCount);

        // A fresh service has no walk for this context, so the first frame comes from the store.
        await _host.Settings.UpdateAsync(s => s with { GraphMetadataFromDisk = true });
        var freshSvc = new GraphLayoutService(_host.Settings, _host.Locator);
        await using var freshCtx = _host.Open(_repo.Path);
        var snapshot = freshSvc.TryOptimisticSnapshot(freshCtx, Q, maxRows: 100);

        Assert.NotNull(snapshot);
        Assert.Equal(expected.Layout.RowCount, snapshot.Layout.RowCount);
        Assert.Equal(
            Enumerable.Range(0, expected.Layout.RowCount).Select(i => expected.Layout.Commits[i].Id),
            Enumerable.Range(0, snapshot.Layout.RowCount).Select(i => snapshot.Layout.Commits[i].Id));
        Assert.Equal(expected.Layout.EdgeCount, snapshot.Layout.EdgeCount);
    }
}
