namespace Historia.Tests;

[Trait("tier", "git")]
public sealed class GraphSpillAdoptionTests : IAsyncDisposable
{
    private readonly StateDir _state = new();
    private readonly MiniHost _host = new();

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
        _state.Dispose();
    }

    private static object GetCache(GraphLayoutService svc, RepositoryContext ctx)
    {
        var cachesField = typeof(GraphLayoutService).GetField("_caches", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var cwt = cachesField.GetValue(svc)!;
        var tryGetValue = cwt.GetType().GetMethod("TryGetValue")!;
        var args = new object?[] { ctx, null };
        var found = (bool)tryGetValue.Invoke(cwt, args)!;
        Assert.True(found, "expected an existing RepoCache for this context");
        return args[1]!;
    }

    private static object? GetLiveWalk(GraphLayoutService svc, RepositoryContext ctx)
    {
        var cache = GetCache(svc, ctx);
        return cache.GetType().GetProperty("LiveWalk", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)!.GetValue(cache);
    }

    private static bool GetExhausted(GraphLayoutService svc, RepositoryContext ctx)
    {
        var cache = GetCache(svc, ctx);
        return (bool)cache.GetType().GetProperty("Exhausted", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)!.GetValue(cache)!;
    }

    private static bool GetMetaComplete(GraphLayoutService svc, RepositoryContext ctx)
    {
        var cache = GetCache(svc, ctx);
        return (bool)cache.GetType().GetProperty("MetaComplete", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)!.GetValue(cache)!;
    }

    private static List<RawCommit> LoadRawCommits(string repoPath, int count)
    {
        using var gitRepo = new Repository(repoPath);
        return gitRepo.Commits.QueryBy(new CommitFilter { IncludeReachableFrom = gitRepo.Refs }).Take(count).Select(c =>
        {
            var meta = new CommitMetadata
            {
                Id = c.Id,
                MessageShort = c.MessageShort,
                AuthorName = c.Author.Name,
                AuthorEmail = c.Author.Email,
                AuthorWhen = c.Author.When,
                CommitWhen = c.Committer.When,
                ParentCount = c.Parents.Count(),
            };
            return new RawCommit(c.Id, c.Parents.Select(p => p.Id).ToArray(), meta);
        }).ToList();
    }

    [Fact(Timeout = 30_000)]
    public async Task Adopted_spill_keeps_the_live_walk_when_the_store_is_not_exhausted()
    {
        const int commitCount = 60;
        using var repo = TestRepositoryBuilder.FromTemplate("linear-60", b => b.LinearHistory(commitCount));
        await _host.Settings.UpdateAsync(s => s with { GraphMetadataFromDisk = true });

        await using var ctx = _host.Open(repo.Path);
        var rawCommits = LoadRawCommits(repo.Path, commitCount);
        const long walkKey = 0x1234_5678_9ABC_DEF0;

        // Writer B pre-populates the on-disk store with 25 rows (not exhausted)
        var writerB = new GraphMetaWriter(repo.Path, walkKey, includeRemoteBranches: true, WalkEngine.LibGit2);
        var (outcomeB, countB) = writerB.AppendSpillBatch(rawCommits.Take(25).ToList(), 0, 25, exhausted: false);
        Assert.Equal(SpillOutcome.Appended, outcomeB);
        Assert.Equal(25, countB);

        // Prepare a RepoCache with a live walk enumerator
        var repoCacheType = typeof(GraphLayoutService).GetNestedType("RepoCache", System.Reflection.BindingFlags.NonPublic)!;
        var cache = Activator.CreateInstance(repoCacheType)!;
        repoCacheType.GetProperty("WalkKey")!.SetValue(cache, walkKey);
        repoCacheType.GetProperty("Engine")!.SetValue(cache, WalkEngine.LibGit2);
        using var dummyWalk = rawCommits.GetEnumerator();
        repoCacheType.GetProperty("LiveWalk")!.SetValue(cache, dummyWalk);
        var commitsList = (List<RawCommit>)repoCacheType.GetProperty("Commits")!.GetValue(cache)!;
        commitsList.AddRange(rawCommits.Take(20));

        var spillRangeMethod = typeof(GraphLayoutService).GetMethod("SpillRange", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var spillSync = repoCacheType.GetField("SpillSync")!.GetValue(cache)!;

        // Foreground spill attempts to spill 0..20, adopting the store that is ahead at 25 rows
        SpillOutcome outcome;
        lock (spillSync)
        {
            outcome = (SpillOutcome)spillRangeMethod.Invoke(null, [cache, repo.Path, true, 0, 20, false, null, null])!;
        }

        Assert.Equal(SpillOutcome.Adopted, outcome);
        // LiveWalk must NOT be disposed because the store is not exhausted
        Assert.NotNull(repoCacheType.GetProperty("LiveWalk")!.GetValue(cache));
        Assert.False((bool)repoCacheType.GetProperty("Exhausted")!.GetValue(cache)!);
        Assert.False((bool)repoCacheType.GetProperty("MetaComplete")!.GetValue(cache)!);
    }

    [Fact(Timeout = 30_000)]
    public async Task Adopted_spill_marks_the_cache_complete_when_the_store_is_exhausted()
    {
        const int commitCount = 30;
        using var repo = TestRepositoryBuilder.FromTemplate("linear-30", b => b.LinearHistory(commitCount));
        await _host.Settings.UpdateAsync(s => s with { GraphMetadataFromDisk = true });

        await using var ctx = _host.Open(repo.Path);
        var rawCommits = LoadRawCommits(repo.Path, commitCount);
        const long walkKey = 0x2468_1357_9BDF_ECA0;

        // Writer B pre-populates the on-disk store with all 30 rows (exhausted)
        var writerB = new GraphMetaWriter(repo.Path, walkKey, includeRemoteBranches: true, WalkEngine.LibGit2);
        var (outcomeB, countB) = writerB.AppendSpillBatch(rawCommits, 0, commitCount, exhausted: true);
        Assert.Equal(SpillOutcome.Appended, outcomeB);
        Assert.Equal(commitCount, countB);

        // Prepare a RepoCache with a live walk enumerator
        var repoCacheType = typeof(GraphLayoutService).GetNestedType("RepoCache", System.Reflection.BindingFlags.NonPublic)!;
        var cache = Activator.CreateInstance(repoCacheType)!;
        repoCacheType.GetProperty("WalkKey")!.SetValue(cache, walkKey);
        repoCacheType.GetProperty("Engine")!.SetValue(cache, WalkEngine.LibGit2);
        using var dummyWalk = rawCommits.GetEnumerator();
        repoCacheType.GetProperty("LiveWalk")!.SetValue(cache, dummyWalk);
        var commitsList = (List<RawCommit>)repoCacheType.GetProperty("Commits")!.GetValue(cache)!;
        commitsList.AddRange(rawCommits.Take(20));

        var spillRangeMethod = typeof(GraphLayoutService).GetMethod("SpillRange", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var spillSync = repoCacheType.GetField("SpillSync")!.GetValue(cache)!;

        // Foreground spill attempts to spill 0..20, adopting the store that is complete
        SpillOutcome outcome;
        lock (spillSync)
        {
            outcome = (SpillOutcome)spillRangeMethod.Invoke(null, [cache, repo.Path, true, 0, 20, false, null, null])!;
        }

        Assert.Equal(SpillOutcome.Adopted, outcome);
        // MetaComplete and Exhausted are marked true
        Assert.True((bool)repoCacheType.GetProperty("MetaComplete")!.GetValue(cache)!);
        Assert.True((bool)repoCacheType.GetProperty("Exhausted")!.GetValue(cache)!);

        // SpillRange left the walk untouched; now retire it safely via RetireLiveWalkAsync
        var retireMethod = typeof(GraphLayoutService).GetMethod("RetireLiveWalkAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        await (Task)retireMethod.Invoke(null, [ctx, cache])!;
        Assert.Null(repoCacheType.GetProperty("LiveWalk")!.GetValue(cache));
    }

    [Fact(Timeout = 30_000)]
    public async Task Retiring_the_live_walk_leaves_no_git_log_process()
    {
        const int commitCount = 20;
        using var repo = TestRepositoryBuilder.FromTemplate("linear-20", b => b.LinearHistory(commitCount));

        await _host.Settings.UpdateAsync(s => s with { GraphMetadataFromDisk = true });

        var svc = new GraphLayoutService(_host.Settings);
        await using var ctx = _host.Open(repo.Path);
        ctx.MarkFirstGraphPaintDone();
        ctx.MarkFirstFullLoadDone();

        var query = new GraphQuery { RefScope = GraphRefScope.AllRefs };
        var result = await svc.ComputeLayoutAsync(ctx, query, maxRows: commitCount);

        Assert.Equal(commitCount, result.Layout.RowCount);
        Assert.True(result.Layout.IsComplete);

        // At exhaustion the walk has been retired safely
        Assert.Null(GetLiveWalk(svc, ctx));
        Assert.True(GetExhausted(svc, ctx));
        Assert.True(GetMetaComplete(svc, ctx));
    }
}
