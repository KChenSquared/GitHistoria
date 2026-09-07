namespace Historia.Tests;

/// <summary>§7.6: with "Read commit details from disk" on, the layout service spills per-commit
/// metadata and points <see cref="GraphLayout.Commits"/> at the paged <c>DiskCommitStore</c>,
/// while the rows still read back correctly across a re-layout.</summary>
public sealed class GraphDiskMetaIntegrationTests : IAsyncDisposable
{
    private readonly MiniHost _host = new();
    private readonly TestRepositoryBuilder _repo;

    private const int CommitCount = 40;

    public GraphDiskMetaIntegrationTests()
    {
        _repo = TestRepositoryBuilder.Create();
        for (var i = 0; i < CommitCount; i++)
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

    private async Task<GraphLayoutService> DiskBackedServiceAsync()
    {
        await _host.Settings.UpdateAsync(s => s with { GraphMetadataFromDisk = true });
        return new GraphLayoutService(_host.Settings);
    }

    [Fact]
    public async Task Compute_spills_metadata_and_serves_it_from_disk()
    {
        var svc = await DiskBackedServiceAsync();
        await using var ctx = _host.Open(_repo.Path);

        var result = await svc.ComputeLayoutAsync(ctx, new GraphQuery { RefScope = GraphRefScope.AllRefs });

        Assert.IsType<DiskCommitStore>(result.Layout.Commits);
        Assert.Equal(CommitCount, result.Layout.RowCount);
        Assert.Equal($"commit {CommitCount - 1:D2}", result.Layout.Commits[0].MessageShort);
        Assert.Equal("commit 00", result.Layout.Commits[CommitCount - 1].MessageShort);

        Assert.True(File.Exists(GraphCachePaths.File(_repo.Path, GraphMetaStore.HdrExt)));
        svc.Forget(ctx);
    }

    [Fact]
    public async Task Background_spill_caches_the_whole_history_without_scrolling()
    {
        var svc = await DiskBackedServiceAsync();
        await using var ctx = _host.Open(_repo.Path);

        // Small visible window — the rest must reach disk on its own.
        await svc.ComputeLayoutAsync(ctx, new GraphQuery { RefScope = GraphRefScope.AllRefs }, maxRows: 5);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        (bool Ok, int RowCount, bool Exhausted) probe = default;
        while (DateTime.UtcNow < deadline)
        {
            probe = GraphMetaStore.Probe(_repo.Path, FingerprintOf(svc, ctx), includeRemoteBranches: true);
            if (probe is { Ok: true, Exhausted: true })
                break;
            await Task.Delay(100);
        }

        Assert.True(probe.Ok, "meta store header should exist");
        Assert.True(probe.Exhausted, "background spill should have walked the whole history");
        Assert.Equal(CommitCount, probe.RowCount);
    }

    // The service computes the fingerprint privately; re-derive it the same way the store keys on
    // by opening any store file — simplest is to read it back from the header the spill just wrote.
    private static long FingerprintOf(GraphLayoutService svc, RepositoryContext ctx)
    {
        var hdr = GraphCachePaths.File(ctx.RepositoryPath, GraphMetaStore.HdrExt);
        if (!File.Exists(hdr))
            return 0;
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(hdr));
        return doc.RootElement.GetProperty("Fingerprint").GetInt64();
    }

    [Fact]
    public async Task Search_over_the_disk_store_finds_message_and_sha()
    {
        var svc = await DiskBackedServiceAsync();
        await using var ctx = _host.Open(_repo.Path);
        var result = await svc.ComputeLayoutAsync(ctx, new GraphQuery { RefScope = GraphRefScope.AllRefs });

        var store = result.Layout.Commits;
        Assert.Equal(CommitCount, store.Search("commit").Count);
        Assert.Single(store.Search("commit 07"));
        var sha = store[3].Id.Sha;
        Assert.Equal(new[] { 3 }, store.Search(sha));
        Assert.Equal(new[] { 3 }, store.Search(sha[..10]));
    }

    [Fact]
    public async Task Falls_back_to_in_memory_when_the_setting_is_off()
    {
        await _host.Settings.UpdateAsync(s => s with { GraphMetadataFromDisk = false });
        var svc = new GraphLayoutService(_host.Settings);
        await using var ctx = _host.Open(_repo.Path);

        var result = await svc.ComputeLayoutAsync(ctx, new GraphQuery { RefScope = GraphRefScope.AllRefs });

        Assert.IsType<ArrayCommitStore>(result.Layout.Commits);
        Assert.False(File.Exists(GraphCachePaths.File(_repo.Path, GraphMetaStore.HdrExt)));
    }
}
