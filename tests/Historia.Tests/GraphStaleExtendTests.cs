namespace Historia.Tests;

/// <summary>
/// A layout still on screen when a RefsChanged re-walk resets the graph cache (an auto-fetch
/// landing new commits while the user scrolls) must not be extended from the new walk: the new
/// walk's row N is a different commit, so the extension would duplicate commits and shift every
/// row's metadata by the number of new commits.
/// </summary>
[Trait("tier", "git")]
public sealed class GraphStaleExtendTests : IAsyncDisposable
{
    private readonly MiniHost _host = new();
    private readonly List<TestRepositoryBuilder> _repos = [];

    public async ValueTask DisposeAsync()
    {
        foreach (var r in _repos)
            r.Dispose();
        await _host.DisposeAsync();
    }

    private static List<ObjectId> Ids(GraphLayoutResult r) =>
        Enumerable.Range(0, r.Layout.RowCount).Select(i => r.Layout.Commits[i].Id).ToList();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_layout_from_before_a_rewalk_is_not_extended_from_the_new_walk(bool metaFromDisk)
    {
        if (!_host.Locator.Current.Found)
            return;
        var repo = TestRepositoryBuilder.Create().LinearHistory(600);
        _repos.Add(repo);
        await _host.Settings.UpdateAsync(s => s with { GraphMetadataFromDisk = metaFromDisk });

        await using var ctx = _host.Open(repo.Path);
        ctx.MarkFirstGraphPaintDone(); ctx.MarkFirstFullLoadDone();
        var svc = new GraphLayoutService(_host.Settings, _host.Locator);
        var query = new GraphQuery();

        var stale = await svc.ComputeLayoutAsync(ctx, query, maxRows: 100);
        var staleIds = Ids(stale);
        // What CommitGraphControl.SetLayout holds for the painted layout: keeps its disk store
        // readable after the re-walk below drops the service's own reference.
        using var lease = stale.Layout.Commits.AddRef();

        for (var i = 0; i < 6; i++)
            repo.Git("commit", "--allow-empty", "-m", $"fetched {i}", "--no-gpg-sign");

        var fresh = await svc.RecomputeAsync(ctx, query, stale, InvalidationReason.RefsChanged, maxRows: 100);
        Assert.NotEqual(stale.Fingerprint, fresh.Fingerprint);

        // The extend pump picked up the old layout before the new one was applied.
        var extended = await svc.ExtendLayoutAsync(ctx, stale, additionalRows: 100);
        Assert.Same(stale, extended);
        Assert.Equal(staleIds, Ids(stale));

        // The current layout still extends, with each commit exactly once.
        var grown = await svc.ExtendLayoutAsync(ctx, fresh, additionalRows: 100);
        Assert.Equal(200, grown.Layout.RowCount);
        var ids = Ids(grown);
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.Equal(staleIds.Take(94), ids.Skip(6).Take(94));
    }
}
