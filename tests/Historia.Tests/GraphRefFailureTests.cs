namespace Historia.Tests;

/// <summary>
/// A repo whose refs can't be read (here: a corrupt <c>packed-refs</c>, which makes every
/// <c>git for-each-ref</c> fail) never makes a public <see cref="GraphLayoutService"/> method
/// throw: a fresh walk is an empty graph, rows already painted stay, and headless caching writes
/// nothing. Also: a walk step whose refs moved while it waited for the repo gate primes from the
/// refs as they are now, not as they were before the wait.
/// </summary>
[Trait("tier", "git")]
public sealed class GraphRefFailureTests : IAsyncDisposable
{
    private readonly MiniHost _host = new();
    private readonly List<TestRepositoryBuilder> _repos = [];

    public async ValueTask DisposeAsync()
    {
        foreach (var r in _repos)
            r.Dispose();
        await _host.DisposeAsync();
    }

    private bool GitMissing => !_host.Locator.Current.Found;

    private TestRepositoryBuilder NewRepo(int commits)
    {
        var repo = TestRepositoryBuilder.Create().LinearHistory(commits);
        _repos.Add(repo);
        return repo;
    }

    private RepositoryContext Open(TestRepositoryBuilder repo)
    {
        var ctx = _host.Open(repo.Path);
        ctx.MarkFirstGraphPaintDone(); ctx.MarkFirstFullLoadDone();
        return ctx;
    }

    /// <summary>git refuses to read any ref once <c>packed-refs</c> has a line it can't parse
    /// (<c>fatal: unexpected line in .git/packed-refs</c>, verified on git 2.51).</summary>
    private static void CorruptPackedRefs(TestRepositoryBuilder repo)
    {
        repo.Git("pack-refs", "--all");
        File.AppendAllText(Path.Combine(repo.Path, ".git", "packed-refs"), "garbage line\n");
    }

    private static string[] CacheFilesFor(string repoPath)
    {
        var hdr = GraphCachePaths.File(repoPath, GraphMetaStore.HdrExt);
        var dir = Path.GetDirectoryName(hdr)!;
        return Directory.Exists(dir)
            ? Directory.GetFiles(dir, GraphCachePaths.Stem(repoPath) + "*")
            : [];
    }

    private static List<GitObjectId> Ids(GraphLayoutResult r) =>
        Enumerable.Range(0, r.Layout.RowCount).Select(i => r.Layout.Commits[i].Id).ToList();

    [Fact]
    public async Task Unreadable_refs_give_an_empty_graph_without_throwing()
    {
        if (GitMissing)
            return;
        var repo = NewRepo(30);
        CorruptPackedRefs(repo);

        await using var ctx = Open(repo);
        var svc = new GraphLayoutService(_host.Settings, _host.Locator);
        var query = new GraphQuery();

        var result = await svc.ComputeLayoutAsync(ctx, query);
        Assert.Equal(0, result.Layout.RowCount);

        var extended = await svc.ExtendLayoutAsync(ctx, result, additionalRows: 100);
        Assert.Equal(0, extended.Layout.RowCount);

        // A layout that did read refs once, asked to recompute while they can't be read: kept.
        var painted = result with { Fingerprint = 42 };
        Assert.Same(painted, await svc.RecomputeAsync(ctx, query, painted, InvalidationReason.RefsChanged));
    }

    [Fact]
    public async Task Refs_becoming_unreadable_keep_the_painted_rows()
    {
        if (GitMissing)
            return;
        var repo = NewRepo(30);

        await using var ctx = Open(repo);
        var svc = new GraphLayoutService(_host.Settings, _host.Locator);
        var query = new GraphQuery();
        var before = await svc.ComputeLayoutAsync(ctx, query);
        Assert.Equal(30, before.Layout.RowCount);

        CorruptPackedRefs(repo);

        var again = await svc.ComputeLayoutAsync(ctx, query);
        Assert.Equal(Ids(before), Ids(again));
        Assert.Same(again, await svc.RecomputeAsync(ctx, query, again, InvalidationReason.RefsChanged));
    }

    [Fact]
    public async Task Headless_cache_with_unreadable_refs_writes_nothing()
    {
        if (GitMissing)
            return;
        var repo = NewRepo(30);
        CorruptPackedRefs(repo);
        await _host.Settings.UpdateAsync(s => s with { GraphMetadataFromDisk = true });

        await using var ctx = Open(repo);
        var svc = new GraphLayoutService(_host.Settings, _host.Locator);
        await svc.RunHeadlessCacheAsync(ctx, new GraphQuery(), CancellationToken.None);

        Assert.Empty(CacheFilesFor(repo.Path));
    }

    private static object GetCache(GraphLayoutService svc, RepositoryContext ctx)
    {
        var cwt = typeof(GraphLayoutService).GetField("_caches", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(svc)!;
        var args = new object?[] { ctx, null };
        Assert.True((bool)cwt.GetType().GetMethod("TryGetValue")!.Invoke(cwt, args)!);
        return args[1]!;
    }

    private static async Task<long> CurrentWalkKeyAsync(RepositoryContext ctx, GraphQuery query)
    {
        var method = typeof(GraphLayoutService).GetMethod("TryGetRefStateAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
        var task = (Task)method.Invoke(null, [ctx, query, false, CancellationToken.None])!;
        await task;
        var state = (System.Runtime.CompilerServices.ITuple)task.GetType().GetProperty("Result")!.GetValue(task)!;
        return (long)state[3]!;
    }

    [Fact]
    public async Task A_ref_change_during_the_gate_wait_primes_from_the_new_refs()
    {
        if (GitMissing)
            return;
        var repo = TestRepositoryBuilder.FromTemplate("linear-3000", b => b.LinearHistory(3_000));
        _repos.Add(repo);
        await _host.Settings.UpdateAsync(s => s with { GraphMetadataFromDisk = false });

        await using var ctx = Open(repo);
        var svc = new GraphLayoutService(_host.Settings, _host.Locator);
        var query = new GraphQuery { RefScope = GraphRefScope.AllRefs };
        await svc.ComputeLayoutAsync(ctx, query, maxRows: 100); // warms the ref snapshot

        // A fresh cache (a live one would supersede, not re-prime) and a new tip. Read the
        // snapshot now so the compute below finds it cached and goes straight to the gate.
        svc.Forget(ctx);
        repo.Git("update-ref", "refs/heads/a", "HEAD~10");
        await ctx.RefSnapshots.GetAsync();

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = GraphLayoutService.GetGateFor(ctx).ExecuteAsync(async () =>
        {
            entered.SetResult();
            await release.Task;
        });
        await entered.Task;

        Task<GraphLayoutResult> compute;
        try
        {
            compute = svc.ComputeLayoutAsync(ctx, query, maxRows: 100);
            await Task.Delay(500); // the compute reads its (now cached) snapshot and queues on the gate
            repo.Git("update-ref", "refs/heads/b", "HEAD~20");
        }
        finally
        {
            release.SetResult();
        }
        await compute;

        var cache = GetCache(svc, ctx);
        var liveKey = (long)cache.GetType().GetProperty("LiveWalkFingerprint")!.GetValue(cache)!;
        Assert.Equal(await CurrentWalkKeyAsync(ctx, query), liveKey);
    }
}
