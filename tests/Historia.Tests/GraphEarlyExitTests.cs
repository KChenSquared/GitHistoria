namespace Historia.Tests;

/// <summary>
/// A walk step whose rows are already walked must cost nothing on the repo gate. The silent
/// crawler asks for +5,000 rows right after the background spill asked for +25,000, so by the
/// time the crawler's step reaches the gate the rows are usually there — and on a repo with tens
/// of thousands of refs a ref scan run first (fingerprint + tip resolution) would hold the
/// gate for ~6 s to return "done". Two concurrent resumes must also never pull more than the
/// larger of their two targets, and must land rows in stream order.
/// </summary>
[Trait("tier", "git")]
public sealed class GraphEarlyExitTests : IAsyncDisposable
{
    private const int CommitCount = 3_000;

    private readonly MiniHost _host = new();
    private readonly TestRepositoryBuilder _repo;

    public GraphEarlyExitTests()
    {
        _repo = TestRepositoryBuilder.FromTemplate("linear-3000", b => b.LinearHistory(CommitCount));
    }

    public async ValueTask DisposeAsync()
    {
        _repo.Dispose();
        await _host.DisposeAsync();
    }

    private static object GetCache(GraphLayoutService svc, RepositoryContext ctx)
    {
        var cachesField = typeof(GraphLayoutService).GetField("_caches", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var cwt = cachesField.GetValue(svc)!;
        var args = new object?[] { ctx, null };
        Assert.True((bool)cwt.GetType().GetMethod("TryGetValue")!.Invoke(cwt, args)!, "expected a RepoCache for this context");
        return args[1]!;
    }

    private static T Get<T>(object target, string property) =>
        (T)target.GetType().GetProperty(property)!.GetValue(target)!;

    private async Task<(GraphLayoutService Svc, RepositoryContext Ctx, GraphLayoutResult Initial)> OpenWalkedAsync()
    {
        // Memory mode: cache.Commits stays "every row walked", which the assertions below read,
        // and no background spill races the pulls under test.
        await _host.Settings.UpdateAsync(s => s with { GraphMetadataFromDisk = false });
        var svc = new GraphLayoutService(_host.Settings, _host.Locator);
        var ctx = _host.Open(_repo.Path);
        ctx.MarkFirstGraphPaintDone();
        ctx.MarkFirstFullLoadDone();

        // 100 rows + the 2,000-row overscan: 2,100 of 3,000 walked, the git log stream still live.
        var initial = await svc.ComputeLayoutAsync(ctx, new GraphQuery { RefScope = GraphRefScope.AllRefs }, maxRows: 100);
        var cache = GetCache(svc, ctx);
        Assert.False(Get<bool>(cache, "Exhausted"));
        Assert.Equal(2_100, Get<int>(cache, "LiveWalkPos"));
        return (svc, ctx, initial);
    }

    [Fact]
    public async Task Satisfied_step_on_the_gate_does_no_ref_scan()
    {
        if (!_host.Locator.Current.Found)
            return; // git.exe not discoverable — the CLI walk never engages

        var (svc, ctx, _) = await OpenWalkedAsync();
        await using var _ctx = ctx;
        var cache = GetCache(svc, ctx);
        var walkCache = typeof(GraphLayoutService).GetField("_walkCache", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(svc);
        var prepareWalk = typeof(GraphLayoutService).GetMethod("PrepareWalk", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var fingerprintBefore = Get<long>(cache, "Fingerprint");
        var snapshot = await ctx.RefSnapshots.GetAsync();
        var scansBefore = GraphLayoutService.RefScanCountForTesting(ctx.RepositoryPath);

        // Exactly what the crawler's step sees once the spill's pull landed: want <= have.
        var task = (Task)prepareWalk.Invoke(null,
            [ctx.RepositoryPath, new GraphQuery { RefScope = GraphRefScope.AllRefs }, 1_500, null, cache, snapshot, false, walkCache, ctx.RemoteConfig, CancellationToken.None])!;
        await task;
        var prep = task.GetType().GetProperty("Result")!.GetValue(task)!;

        Assert.Equal("Done", Get<object>(prep, "Kind").ToString());
        Assert.Equal(0L, Get<long>(prep, "Fingerprint"));
        Assert.Equal(scansBefore, GraphLayoutService.RefScanCountForTesting(ctx.RepositoryPath));
        Assert.Equal(fingerprintBefore, Get<long>(cache, "Fingerprint"));
    }

    [Fact]
    public async Task Concurrent_resumes_pull_only_to_the_larger_target_in_stream_order()
    {
        if (!_host.Locator.Current.Found)
            return;

        var (svc, ctx, _) = await OpenWalkedAsync();
        await using var _ctx = ctx;
        var cache = GetCache(svc, ctx);

        // Two walk steps that both read "have 2,100" before either pulls: hold the walk lock so
        // each gets as far as waiting on it, then let them go. Whichever pulls second must size
        // its pull from what the first already walked, so the stream ends at 2,600 — not 2,900
        // (both sized from the 2,100 they read up front).
        var walkLock = (SemaphoreSlim)cache.GetType().GetField("WalkLock")!.GetValue(cache)!;
        var ensureWalked = typeof(GraphLayoutService).GetMethod("EnsureWalkedAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var query = new GraphQuery { RefScope = GraphRefScope.AllRefs };
        Task Step(int want) => (Task)ensureWalked.Invoke(svc, [ctx, query, cache, want, CancellationToken.None, true, false])!;

        await walkLock.WaitAsync();
        var first = Task.Run(() => Step(2_400));
        var second = Task.Run(() => Step(2_600));
        await WaitUntilAsync(() => walkLock.CurrentCount == 0 && Waiters(walkLock) >= 2);
        walkLock.Release();
        await Task.WhenAll(first, second);

        Assert.Equal(2_600, Get<int>(cache, "LiveWalkPos"));

        var walked = Get<System.Collections.IList>(cache, "Commits").Cast<RawCommit>().Select(c => c.Id.ToString()).ToList();
        var expected = _repo.Git("rev-list", "--date-order", "main")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Take(2_600)
            .ToList();
        Assert.Equal(expected, walked);
    }

    /// <summary>How many callers are queued on <paramref name="sem"/> — SemaphoreSlim keeps its
    /// async waiters in a private linked list; counted by walking it.</summary>
    private static int Waiters(SemaphoreSlim sem)
    {
        var head = typeof(SemaphoreSlim).GetField("m_asyncHead", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(sem);
        var n = 0;
        for (var node = head; node is not null; node = node.GetType().GetField("Next", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)!.GetValue(node))
            n++;
        return n;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out waiting for both walk steps to queue on the walk lock");
            await Task.Delay(10);
        }
    }
}
