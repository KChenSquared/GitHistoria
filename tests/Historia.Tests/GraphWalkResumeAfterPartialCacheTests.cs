namespace Historia.Tests;

/// <summary>
/// A fresh CLI-engine prime that starts with
/// <c>cache.Commits</c> already partially seeded (a warm relaunch from a non-exhausted on-disk
/// walk cache) must re-skip those already-cached rows (<c>skip: have</c>). A
/// <c>PrimeCliWalkAsync</c> that pumped with <c>skip: 0</c> would land the freshly primed walk's
/// rows <c>[0, need)</c> appended AFTER the <c>have</c> cached rows instead of continuing from
/// them — duplicate commits in the layout. This test drives the real <see cref="GraphLayoutService"/> code path (not a mock),
/// only using reflection to put the private <c>RepoCache</c> into the "partial warm reload" state
/// that a genuinely large repo's on-disk cache would produce — reproducing that with real files
/// would need a repo far larger than a unit test should build.
/// </summary>
[Trait("tier", "git")]
public sealed class GraphWalkResumeAfterPartialCacheTests : IAsyncDisposable
{
    private readonly MiniHost _host = new();
    private readonly TestRepositoryBuilder _repo;
    private const int CommitCount = 10;

    public GraphWalkResumeAfterPartialCacheTests()
    {
        _repo = TestRepositoryBuilder.FromTemplate("linear-10", b => b.LinearHistory(CommitCount));
    }

    public async ValueTask DisposeAsync()
    {
        _repo.Dispose();
        await _host.DisposeAsync();
    }

    private static object GetCache(GraphLayoutService svc, RepositoryContext ctx)
    {
        var cachesField = typeof(GraphLayoutService).GetField("_caches", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var cwt = cachesField.GetValue(svc)!;
        var tryGetValue = cwt.GetType().GetMethod("TryGetValue")!;
        var args = new object?[] { ctx, null };
        var found = (bool)tryGetValue.Invoke(cwt, args)!;
        Assert.True(found, "expected an existing RepoCache for this context");
        return args[1]!;
    }

    private static T Get<T>(object cache, string property) =>
        (T)cache.GetType().GetProperty(property, BindingFlags.Public | BindingFlags.Instance)!.GetValue(cache)!;

    private static void Set(object cache, string property, object? value) =>
        cache.GetType().GetProperty(property, BindingFlags.Public | BindingFlags.Instance)!.SetValue(cache, value);

    [Fact]
    public async Task Cli_prime_resuming_from_a_partial_warm_cache_yields_no_duplicate_rows()
    {
        var locator = _host.Provider.GetRequiredService<IGitExecutableLocator>();
        if (!locator.Current.Found)
            return; // git.exe not discoverable — the CLI engine never engages (see class doc elsewhere)

        // With disk-backed metadata on (the
        // default), a background spill trims confirmed-durable rows out of cache.Commits, so
        // "cache.Commits.Count == every row ever walked" no longer holds — that's covered by its
        // own tests (GraphSpoolTests). This test is specifically about the CLI prime's resume
        // skip logic, which is orthogonal to disk spilling — keep it in memory mode
        // so cache.Commits still means "the whole walked history", as the assertions below assume.
        await _host.Settings.UpdateAsync(s => s with { GraphMetadataFromDisk = false });
        var svc = new GraphLayoutService(_host.Settings, locator);
        await using var ctx = _host.Open(_repo.Path);
        ctx.MarkFirstGraphPaintDone();
        ctx.MarkFirstFullLoadDone();

        var query = new GraphQuery { RefScope = GraphRefScope.AllRefs };

        // A normal small compute: the 2,000-row walk overscan means cache.Commits ends up
        // holding the WHOLE (10-commit) history and Exhausted=true, even though only 3 rows are
        // laid out. Real data, real fingerprint, real engine — only the "partial, non-exhausted"
        // shape below is synthetic, standing in for a warm relaunch that only got partway through
        // walking a much larger repo before the app last closed.
        var initial = await svc.ComputeLayoutAsync(ctx, query, maxRows: 3);
        Assert.Equal(3, initial.Layout.RowCount);

        var cache = GetCache(svc, ctx);
        var commits = Get<System.Collections.IList>(cache, "Commits");
        Assert.Equal(CommitCount, commits.Count);

        // Truncate to the first 5 rows and mark the walk as NOT exhausted — a partial disk cache.
        while (commits.Count > 5)
            commits.RemoveAt(commits.Count - 1);
        Set(cache, "Exhausted", false);
        Set(cache, "LiveWalk", null);
        Set(cache, "LiveWalkPos", 0);
        Set(cache, "LiveWalkFingerprint", 0L);

        // Extending forces EnsureWalkedAsync to want more than the 5 resident rows. With no live
        // walk to resume, this takes the "prime a fresh CLI walk" branch — the path under
        // test — and it must re-skip the 5 rows already in
        // cache.Commits rather than re-emit the whole history from row 0.
        var extended = await svc.ExtendLayoutAsync(ctx, initial, additionalRows: 5);

        Assert.Equal(CommitCount, commits.Count);
        var ids = commits.Cast<RawCommit>().Select(c => c.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());

        var rowIds = Enumerable.Range(0, extended.Layout.RowCount).Select(i => extended.Layout.Commits[i].Id).ToList();
        Assert.Equal(rowIds.Count, rowIds.Distinct().Count());
    }

    /// <summary><c>ResolveTipShas</c> (the CLI engine's tip resolver) must hand back an
    /// ordinally sorted array — git's own topological sort depends on the order tips are fed to
    /// it (measured), and the walk fingerprint already sorts refs by name, so an unsorted,
    /// enumeration-order tip list would let a `git gc`/`pack-refs` that only reorders how refs
    /// enumerate silently change the walk's row order under an unchanged fingerprint.</summary>
    [Fact]
    public async Task ResolveTipShas_returns_tips_in_ordinal_order()
    {
        _repo.Git("branch", "zzz-branch");
        _repo.Git("branch", "aaa-branch");
        _repo.Git("branch", "mmm-branch");

        await using var ctx = _host.Open(_repo.Path);
        var method = typeof(GraphLayoutService).GetMethod("ResolveTipShas", BindingFlags.NonPublic | BindingFlags.Static)!;
        var query = new GraphQuery { RefScope = GraphRefScope.AllRefs };

        var snapshot = await ctx.RefSnapshots.GetAsync();
        var tips = (string[])method.Invoke(null, [snapshot, query])!;

        Assert.NotEmpty(tips);
        var sorted = tips.OrderBy(s => s, StringComparer.Ordinal).ToArray();
        Assert.Equal(sorted, tips);
    }
}
