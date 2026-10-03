namespace Historia.Tests;

/// <summary>
/// A cache that is thrown away — <c>Forget</c> on a refs change, a forced reset when the query
/// shape changes — must not leave its <c>git log</c> process running beside the walk that replaces
/// it, and must not kill that process under a pull that is still reading from it.
/// </summary>
[Trait("tier", "git")]
public sealed class GraphOrphanWalkTests : IAsyncDisposable
{
    private readonly StateDir _state = new();
    private readonly MiniHost _host = new();
    private readonly TestRepositoryBuilder _repo =
        TestRepositoryBuilder.FromTemplate("linear-6000", b => b.LinearHistory(6_000));

    private static readonly GraphQuery AllRefs = new() { RefScope = GraphRefScope.AllRefs };

    public async ValueTask DisposeAsync()
    {
        _repo.Dispose();
        await _host.DisposeAsync();
        _state.Dispose();
    }

    /// <summary>No disk spill: its background pass walks the whole history and disposes the walk
    /// itself — under load, before the test looks — hiding whether teardown does.</summary>
    private async Task<GraphLayoutService> NewServiceAsync()
    {
        await _host.Settings.UpdateAsync(s => s with { GraphMetadataFromDisk = false });
        return new GraphLayoutService(_host.Settings, _host.Locator);
    }

    private RepositoryContext Open()
    {
        var ctx = _host.Open(_repo.Path);
        ctx.MarkFirstGraphPaintDone();
        ctx.MarkFirstFullLoadDone();
        return ctx;
    }

    private static async Task<bool> EventuallyAsync(Func<bool> condition, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (condition())
                return true;
            await Task.Delay(20);
        }
        return condition();
    }

    [Fact(Timeout = 60000)]
    public async Task Forget_disposes_the_live_walk()
    {
        await using var ctx = Open();
        var svc = await NewServiceAsync();

        await svc.ComputeLayoutAsync(ctx, AllRefs, maxRows: 200);
        Assert.True(svc.GetCacheStateForTesting(ctx) is { HasLiveWalk: true }, "expected a primed, unfinished walk");
        Assert.Equal(1, GitLogStream.LiveCountForTesting(ctx.RepositoryPath));

        svc.Forget(ctx);

        Assert.True(await EventuallyAsync(() => GitLogStream.LiveCountForTesting(ctx.RepositoryPath) == 0, TimeSpan.FromSeconds(5)),
            "the forgotten walk's git process is still alive");
    }

    [Fact(Timeout = 60000)]
    public async Task A_forced_cache_reset_disposes_the_old_walk()
    {
        await using var ctx = Open();
        var svc = await NewServiceAsync();

        await svc.ComputeLayoutAsync(ctx, AllRefs, maxRows: 200);
        Assert.Equal(1, GitLogStream.LiveCountForTesting(ctx.RepositoryPath));

        // The reset a stale fingerprint, a superseded walk or a short disk read triggers.
        var reset = typeof(GraphLayoutService).GetMethod("GetOrResetCache", BindingFlags.NonPublic | BindingFlags.Instance)!;
        reset.Invoke(svc, [ctx, AllRefs, true]);

        Assert.True(await EventuallyAsync(() => GitLogStream.LiveCountForTesting(ctx.RepositoryPath) == 0, TimeSpan.FromSeconds(5)),
            "the reset cache's git process is still alive");
        svc.Forget(ctx);
    }

    [Fact(Timeout = 60000)]
    public async Task Forget_during_a_pull_lets_the_pull_finish_then_disposes()
    {
        await using var ctx = Open();
        var svc = await NewServiceAsync();
        var first = await svc.ComputeLayoutAsync(ctx, AllRefs, maxRows: 200);

        var extend = Task.Run(() => svc.ExtendLayoutAsync(ctx, first, 3_500));
        svc.Forget(ctx);

        try
        {
            await extend;
        }
        catch (OperationCanceledException)
        {
            // The cache it ran on was torn down — a cancelled extend is the expected outcome.
        }

        Assert.True(await EventuallyAsync(() => GitLogStream.LiveCountForTesting(ctx.RepositoryPath) == 0, TimeSpan.FromSeconds(5)),
            "the forgotten walk's git process is still alive");
    }
}
