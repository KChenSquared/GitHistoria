namespace Historia.Tests;

/// <summary>
/// Phase 4 of the v10 graph memory footprint plan: <see cref="IGraphLayoutService.RunHeadlessCacheAsync"/>
/// brings an INACTIVE tab's on-disk graph cache up to date with no live layout and no lasting
/// entry in the service's own cache table. This also exercises Phase 3c's live reorder trigger —
/// the headless pipeline is where it was deferred to, since there's no concurrent foreground
/// reader to protect mid-reorder here.
/// </summary>
public sealed class GraphHeadlessCacheTests : IAsyncDisposable
{
    private readonly MiniHost _host = new();
    private readonly TestRepositoryBuilder _repo;

    private const int CommitCount = 20;

    public GraphHeadlessCacheTests()
    {
        _repo = TestRepositoryBuilder.Create();
        for (var i = 0; i < CommitCount; i++)
            _repo.WriteFile("f.txt", $"{i}\n").Stage().Commit($"commit {i:D2}");
    }

    public async ValueTask DisposeAsync()
    {
        _repo.Dispose();
        await _host.DisposeAsync();
    }

    private async Task<GraphLayoutService> DiskBackedServiceAsync()
    {
        await _host.Settings.UpdateAsync(s => s with { GraphMetadataFromDisk = true });
        return new GraphLayoutService(_host.Settings);
    }

    private static GraphQuery Q => new() { RefScope = GraphRefScope.AllRefs };

    [Fact]
    public async Task Headless_cache_spools_a_fresh_repo_to_an_exhausted_matching_store()
    {
        var svc = await DiskBackedServiceAsync();
        await using var ctx = _host.Open(_repo.Path);

        await svc.RunHeadlessCacheAsync(ctx, Q, CancellationToken.None);

        var hdrPath = GraphCachePaths.File(_repo.Path, GraphMetaStore.HdrExt);
        Assert.True(File.Exists(hdrPath));

        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(hdrPath));
        Assert.True(doc.RootElement.GetProperty("Exhausted").GetBoolean());
        Assert.Equal(CommitCount, doc.RootElement.GetProperty("RowCount").GetInt32());
    }

    [Fact]
    public async Task Headless_cache_never_creates_a_live_layout_or_RepoCache_entry()
    {
        var svc = await DiskBackedServiceAsync();
        await using var ctx = _host.Open(_repo.Path);

        await svc.RunHeadlessCacheAsync(ctx, Q, CancellationToken.None);

        var cachesField = typeof(GraphLayoutService).GetField("_caches", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var cwt = cachesField.GetValue(svc)!;
        var tryGetValue = cwt.GetType().GetMethod("TryGetValue")!;
        var args = new object?[] { ctx, null };
        var found = (bool)tryGetValue.Invoke(cwt, args)!;

        Assert.False(found, "a headless cache pass must not leave a RepoCache entry for this context");
    }

    [Fact]
    public async Task Headless_cache_is_a_no_op_once_already_exhausted_and_matching()
    {
        var svc = await DiskBackedServiceAsync();
        await using var ctx = _host.Open(_repo.Path);
        await svc.RunHeadlessCacheAsync(ctx, Q, CancellationToken.None);

        var datPath = GraphCachePaths.File(_repo.Path, GraphMetaStore.DatExt);
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
    public async Task A_branch_created_after_the_first_headless_cache_pass_triggers_a_reorder_not_a_full_respill()
    {
        var svc = await DiskBackedServiceAsync();
        await using (var ctx = _host.Open(_repo.Path))
            await svc.RunHeadlessCacheAsync(ctx, Q, CancellationToken.None);

        var datPath = GraphCachePaths.File(_repo.Path, GraphMetaStore.DatExt);
        var beforeLength = new FileInfo(datPath).Length;

        // A fetch/branch-create that moves the walk key: two new commits on a new branch whose
        // parent is the old trunk tip.
        _repo.Checkout("feature", create: true);
        _repo.WriteFile("f.txt", "feature 1\n").Stage().Commit("feature commit 1");
        _repo.WriteFile("f.txt", "feature 2\n").Stage().Commit("feature commit 2");

        await using (var ctx2 = _host.Open(_repo.Path))
            await svc.RunHeadlessCacheAsync(ctx2, Q, CancellationToken.None);

        var afterLength = new FileInfo(datPath).Length;
        var hdrPath = GraphCachePaths.File(_repo.Path, GraphMetaStore.HdrExt);
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(hdrPath));

        // A reorder appends only the new commits' records; a full re-spill would roughly double
        // .meta.dat (rewriting all 20 old records plus the 2 new ones instead of just adding 2).
        Assert.True(doc.RootElement.GetProperty("OrderVersion").GetInt32() > 0, "expected a reorder, not identity order");
        Assert.Equal(CommitCount + 2, doc.RootElement.GetProperty("RowCount").GetInt32());
        Assert.Equal(CommitCount + 2, doc.RootElement.GetProperty("RecordCount").GetInt32());
        var grew = afterLength - beforeLength;
        Assert.True(grew > 0 && grew < (beforeLength / 2), $"expected a small append (~2 new records), grew by {grew} bytes from {beforeLength}");
    }

    [Fact]
    public async Task Rows_after_a_headless_reorder_read_back_in_the_new_walks_order()
    {
        var svc = await DiskBackedServiceAsync();
        await using (var ctx = _host.Open(_repo.Path))
            await svc.RunHeadlessCacheAsync(ctx, Q, CancellationToken.None);

        _repo.Checkout("feature", create: true);
        _repo.WriteFile("f.txt", "feature 1\n").Stage().Commit("feature commit 1");

        await using (var ctx2 = _host.Open(_repo.Path))
            await svc.RunHeadlessCacheAsync(ctx2, Q, CancellationToken.None);

        // Ground truth: a plain in-memory compute of the SAME (now-checked-out) history.
        await _host.Settings.UpdateAsync(s => s with { GraphMetadataFromDisk = false });
        var memSvc = new GraphLayoutService(_host.Settings);
        await using var memCtx = _host.Open(_repo.Path);
        var expected = await memSvc.ComputeLayoutAsync(memCtx, Q);
        Assert.Equal(CommitCount + 1, expected.Layout.RowCount);

        // The headlessly-reordered disk store: ProbeExisting (not ProbeAny/Probe) so this reads
        // through the actual OrderVersion/RecordCount the reorder wrote, not the identity order.
        var probe = GraphMetaStore.ProbeExisting(_repo.Path, includeRemoteBranches: true, WalkEngine.LibGit2);
        Assert.True(probe.Ok);
        Assert.True(probe.OrderVersion > 0, "expected the reorder's order file to be in effect");
        using var store = DiskCommitStore.Open(_repo.Path, probe.RowCount, probe.RecordCount, probe.OrderVersion);
        Assert.Equal(CommitCount + 1, store.Count);
        for (var i = 0; i < store.Count; i++)
            Assert.Equal(expected.Layout.Commits[i].Id, store.ReadRaw(i).Id);
    }

    [Fact]
    public async Task Headless_cache_does_nothing_when_disk_backed_metadata_is_off()
    {
        await _host.Settings.UpdateAsync(s => s with { GraphMetadataFromDisk = false });
        var svc = new GraphLayoutService(_host.Settings);
        await using var ctx = _host.Open(_repo.Path);

        await svc.RunHeadlessCacheAsync(ctx, Q, CancellationToken.None);

        Assert.False(File.Exists(GraphCachePaths.File(_repo.Path, GraphMetaStore.HdrExt)));
    }

    [Fact]
    public async Task Headless_cache_is_cancellable_before_it_starts_without_touching_disk()
    {
        var svc = await DiskBackedServiceAsync();
        await using var ctx = _host.Open(_repo.Path);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => svc.RunHeadlessCacheAsync(ctx, Q, cts.Token));

        Assert.False(File.Exists(GraphCachePaths.File(_repo.Path, GraphMetaStore.HdrExt)));
    }
}
