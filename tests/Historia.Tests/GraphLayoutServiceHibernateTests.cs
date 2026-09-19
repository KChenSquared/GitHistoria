using System.Reflection;

namespace Historia.Tests;

/// <summary><see cref="IGraphLayoutService.Hibernate"/>
/// releases everything a tab's graph holds in memory on switch-away, but leaves any on-disk cache
/// in place so a re-focus is a normal, faster-than-cold reopen.</summary>
[Trait("tier", "git")]
public sealed class GraphLayoutServiceHibernateTests : IAsyncDisposable
{
    private readonly MiniHost _host = new();
    private readonly TestRepositoryBuilder _repo;
    private const int CommitCount = 20;

    public GraphLayoutServiceHibernateTests()
    {
        _repo = TestRepositoryBuilder.FromTemplate("linear-20", b => b.LinearHistory(CommitCount));
    }

    public async ValueTask DisposeAsync()
    {
        _repo.Dispose();
        await _host.DisposeAsync();
    }

    private static bool HasCacheEntry(GraphLayoutService svc, RepositoryContext ctx)
    {
        var cachesField = typeof(GraphLayoutService).GetField("_caches", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var cwt = cachesField.GetValue(svc)!;
        var tryGetValue = cwt.GetType().GetMethod("TryGetValue")!;
        var args = new object?[] { ctx, null };
        return (bool)tryGetValue.Invoke(cwt, args)!;
    }

    [Fact]
    public async Task Hibernate_drops_the_repo_cache_entry()
    {
        await _host.Settings.UpdateAsync(s => s with { GraphMetadataFromDisk = true });
        var svc = new GraphLayoutService(_host.Settings);
        await using var ctx = _host.Open(_repo.Path);
        ctx.MarkFirstGraphPaintDone();
        ctx.MarkFirstFullLoadDone();

        await svc.ComputeLayoutAsync(ctx, new GraphQuery { RefScope = GraphRefScope.AllRefs });
        Assert.True(HasCacheEntry(svc, ctx));

        await svc.Hibernate(ctx);

        Assert.False(HasCacheEntry(svc, ctx));
    }

    [Fact]
    public async Task Hibernate_on_a_context_with_no_cache_is_a_no_op()
    {
        var svc = new GraphLayoutService(_host.Settings);
        await using var ctx = _host.Open(_repo.Path);

        await svc.Hibernate(ctx); // must not throw
        Assert.False(HasCacheEntry(svc, ctx));
    }

    [Fact]
    public async Task Refocus_after_hibernate_produces_the_same_layout()
    {
        await _host.Settings.UpdateAsync(s => s with { GraphMetadataFromDisk = true });
        var svc = new GraphLayoutService(_host.Settings);
        await using var ctx = _host.Open(_repo.Path);
        ctx.MarkFirstGraphPaintDone();
        ctx.MarkFirstFullLoadDone();

        var before = await svc.ComputeLayoutAsync(ctx, new GraphQuery { RefScope = GraphRefScope.AllRefs });
        // Give the background spill a moment to persist the whole history to disk before we
        // drop the in-memory state — otherwise the re-focus below has nothing to resume from.
        await Task.Delay(1000);
        // Read the "before" row ids BEFORE hibernating: Layout.Commits may be the very
        // DiskCommitStore instance Hibernate disposes, same as a real bound control would need
        // to AddRef() it (§7.6 leases) to keep reading after the tab's cache is released.
        var beforeIds = Enumerable.Range(0, before.Layout.RowCount).Select(i => before.Layout.Commits[i].Id).ToList();

        await svc.Hibernate(ctx);

        var after = await svc.ComputeLayoutAsync(ctx, new GraphQuery { RefScope = GraphRefScope.AllRefs });

        Assert.Equal(beforeIds.Count, after.Layout.RowCount);
        for (var i = 0; i < beforeIds.Count; i++)
            Assert.Equal(beforeIds[i], after.Layout.Commits[i].Id);
    }
}
