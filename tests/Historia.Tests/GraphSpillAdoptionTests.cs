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
                Id = c.Id.ToGitObjectId(),
                MessageShort = c.MessageShort,
                AuthorName = c.Author.Name,
                AuthorEmail = c.Author.Email,
                AuthorWhen = c.Author.When,
                CommitWhen = c.Committer.When,
                ParentCount = c.Parents.Count(),
            };
            return new RawCommit(c.Id.ToGitObjectId(), c.Parents.Select(p => p.Id.ToGitObjectId()).ToArray(), meta);
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
        var writerB = new GraphMetaWriter(repo.Path, walkKey, includeRemoteBranches: true);
        var (outcomeB, countB) = writerB.AppendSpillBatch(rawCommits.Take(25).ToList(), 0, 25, exhausted: false);
        Assert.Equal(SpillOutcome.Appended, outcomeB);
        Assert.Equal(25, countB);

        // Prepare a RepoCache with a live walk enumerator
        var repoCacheType = typeof(GraphLayoutService).GetNestedType("RepoCache", System.Reflection.BindingFlags.NonPublic)!;
        var cache = Activator.CreateInstance(repoCacheType)!;
        repoCacheType.GetProperty("WalkKey")!.SetValue(cache, walkKey);
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
        var writerB = new GraphMetaWriter(repo.Path, walkKey, includeRemoteBranches: true);
        var (outcomeB, countB) = writerB.AppendSpillBatch(rawCommits, 0, commitCount, exhausted: true);
        Assert.Equal(SpillOutcome.Appended, outcomeB);
        Assert.Equal(commitCount, countB);

        // Prepare a RepoCache with a live walk enumerator
        var repoCacheType = typeof(GraphLayoutService).GetNestedType("RepoCache", System.Reflection.BindingFlags.NonPublic)!;
        var cache = Activator.CreateInstance(repoCacheType)!;
        repoCacheType.GetProperty("WalkKey")!.SetValue(cache, walkKey);
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
        await (Task)retireMethod.Invoke(null, [cache])!;
        Assert.Null(repoCacheType.GetProperty("LiveWalk")!.GetValue(cache));
    }

    [Fact(Timeout = 30_000)]
    public async Task Retiring_the_live_walk_leaves_no_git_log_process()
    {
        const int commitCount = 20;
        using var repo = TestRepositoryBuilder.FromTemplate("linear-20", b => b.LinearHistory(commitCount));

        await _host.Settings.UpdateAsync(s => s with { GraphMetadataFromDisk = true });

        var svc = new GraphLayoutService(_host.Settings, _host.Locator);
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

    private const string SyntheticRepo = @"C:\repos\spill-adoption-demo";

    private static GitObjectId Oid(int i) => GitObjectId.Parse(i.ToString("x40"));

    private static RawCommit Raw(int i) => new(
        Oid(i),
        i == 0 ? [] : [Oid(i - 1)],
        new CommitMetadata
        {
            Id = Oid(i),
            MessageShort = $"commit {i}",
            AuthorName = "A",
            AuthorEmail = "a@x",
            AuthorWhen = DateTimeOffset.UnixEpoch.AddMinutes(i),
            CommitWhen = DateTimeOffset.UnixEpoch.AddMinutes(i),
            ParentCount = i == 0 ? 0 : 1,
        });

    private static List<RawCommit> RawRange(int from, int count) =>
        Enumerable.Range(from, count).Select(Raw).ToList();

    private static Type RepoCacheType =>
        typeof(GraphLayoutService).GetNestedType("RepoCache", System.Reflection.BindingFlags.NonPublic)!;

    private static object NewCache(long walkKey, DiskCommitStore store, int residentRows)
    {
        var cache = Activator.CreateInstance(RepoCacheType)!;
        RepoCacheType.GetProperty("WalkKey")!.SetValue(cache, walkKey);
        RepoCacheType.GetProperty("MetaStore")!.SetValue(cache, store);
        ((List<RawCommit>)RepoCacheType.GetProperty("Commits")!.GetValue(cache)!).AddRange(RawRange(0, residentRows));
        return cache;
    }

    private static SpillOutcome InvokeSpillRange(object cache, int from, int to)
    {
        var spillRange = typeof(GraphLayoutService).GetMethod("SpillRange", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var spillSync = RepoCacheType.GetField("SpillSync")!.GetValue(cache)!;
        lock (spillSync)
        {
            return (SpillOutcome)spillRange.Invoke(null, [cache, SyntheticRepo, true, from, to, false, null, null])!;
        }
    }

    private static (List<RawCommit> Rows, bool Complete) InvokeReadWindow(object cache, int start, int count)
    {
        var readWindow = typeof(GraphLayoutService).GetMethod("ReadWindow", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var read = readWindow.Invoke(null, [cache, start, count])!;
        var rows = (List<RawCommit>)read.GetType().GetProperty("Rows")!.GetValue(read)!;
        var complete = (bool)read.GetType().GetProperty("Complete")!.GetValue(read)!;
        return (rows, complete);
    }

    private static int SpilledRowsOf(object cache) => (int)RepoCacheType.GetProperty("SpilledRows")!.GetValue(cache)!;

    private static int ResidentRowsOf(object cache) =>
        ((List<RawCommit>)RepoCacheType.GetProperty("Commits")!.GetValue(cache)!).Count;

    [Fact]
    public void Adopting_past_a_reordered_store_keeps_unserved_rows_resident()
    {
        const long oldKey = 0x1111_2222_3333_4444;
        const long reorderedKey = 0x5555_6666_7777_8888;
        const int storeRows = 10;

        // A spilled, exhausted store, then an identity-permutation reorder committed under a new
        // walk key so the store opens with an order file.
        var original = new GraphMetaWriter(SyntheticRepo, oldKey, includeRemoteBranches: true);
        original.AppendSpillBatch(RawRange(0, storeRows), 0, storeRows, exhausted: true);
        var before = GraphMetaStore.ProbeExisting(SyntheticRepo, includeRemoteBranches: true);
        var reorderWriter = new GraphMetaWriter(SyntheticRepo, reorderedKey, includeRemoteBranches: true);
        Assert.True(reorderWriter.TryCommitReorder(
            before.WalkKey, before.Exhausted, before.OrderVersion, before.RecordCount,
            newRecordCount: 0, order: [.. Enumerable.Range(0, storeRows)], sortedTips: [Oid(storeRows - 1)]));

        using var store = GraphMetaStore.TryOpen(SyntheticRepo, reorderedKey, includeRemoteBranches: true, storeRows)!;
        Assert.NotNull(store);
        Assert.Equal(storeRows, store.Count);

        // Another writer on the same walk grows the on-disk store past what the open one serves.
        var grower = new GraphMetaWriter(SyntheticRepo, reorderedKey, includeRemoteBranches: true);
        var (grown, headerRows) = grower.AppendSpillBatch(RawRange(storeRows, 20), storeRows, storeRows + 20, exhausted: false);
        Assert.Equal(SpillOutcome.Appended, grown);
        Assert.Equal(storeRows + 20, headerRows);

        var cache = NewCache(reorderedKey, store, residentRows: 25);
        var outcome = InvokeSpillRange(cache, 0, 25);

        Assert.Equal(SpillOutcome.Adopted, outcome);
        Assert.Equal(storeRows, store.Count);
        Assert.Equal(storeRows, SpilledRowsOf(cache));
        Assert.Equal(25 - storeRows, ResidentRowsOf(cache));
        var (rows, complete) = InvokeReadWindow(cache, 0, 25);
        Assert.True(complete);
        Assert.Equal(Enumerable.Range(0, 25).Select(Oid), rows.Select(r => r.Id));
    }

    [Fact]
    public void Adopting_with_a_short_open_store_trims_only_what_it_can_serve()
    {
        const long walkKey = 0x2468_1357_9BDF_ECA0;
        const int storeRows = 10;

        var writer = new GraphMetaWriter(SyntheticRepo, walkKey, includeRemoteBranches: true);
        writer.AppendSpillBatch(RawRange(0, storeRows), 0, storeRows, exhausted: false);
        using var store = DiskCommitStore.Open(SyntheticRepo, storeRows);
        // Pin the store to an explicit order so it cannot grow in place, as a reordered store.
        typeof(DiskCommitStore).GetField("_order", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(store, Enumerable.Range(0, storeRows).ToArray());

        // The on-disk store then grows and is marked exhausted by another writer.
        var (grown, _) = writer.AppendSpillBatch(RawRange(storeRows, 20), storeRows, storeRows + 20, exhausted: true);
        Assert.Equal(SpillOutcome.Appended, grown);

        var cache = NewCache(walkKey, store, residentRows: 25);
        var outcome = InvokeSpillRange(cache, 0, 25);

        Assert.Equal(SpillOutcome.Adopted, outcome);
        Assert.Equal(storeRows, store.Count);
        Assert.Equal(storeRows, SpilledRowsOf(cache));
        Assert.Equal(25 - storeRows, ResidentRowsOf(cache));
        var (rows, complete) = InvokeReadWindow(cache, 0, 25);
        Assert.True(complete);
        Assert.Equal(Enumerable.Range(0, 25).Select(Oid), rows.Select(r => r.Id));

        // The store cannot serve the whole exhausted history, so the cache is not marked complete.
        Assert.False((bool)RepoCacheType.GetProperty("MetaComplete")!.GetValue(cache)!);
        Assert.False((bool)RepoCacheType.GetProperty("Exhausted")!.GetValue(cache)!);
    }
}
