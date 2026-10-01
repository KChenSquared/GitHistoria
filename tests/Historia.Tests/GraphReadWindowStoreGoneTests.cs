namespace Historia.Tests;

/// <summary>
/// Rows that live on disk cannot be read back once the cache has lost its store (another writer's
/// reset made it drop the store, or a tab switch tore it down). A window that needs them is
/// incomplete — never a complete-looking window built from the resident rows alone, which painted
/// the walk's row <c>SpilledRows</c> as row 0.
/// </summary>
[Trait("tier", "git")]
public sealed class GraphReadWindowStoreGoneTests : IAsyncDisposable
{
    private readonly MiniHost _host = new();
    private readonly List<TestRepositoryBuilder> _repos = [];

    private static readonly GraphQuery Q = new() { RefScope = GraphRefScope.AllRefs };

    public async ValueTask DisposeAsync()
    {
        foreach (var r in _repos)
            r.Dispose();
        await _host.DisposeAsync();
    }

    private sealed class PaintOnlyGate : IBackgroundWorkGate
    {
        public bool IsOpen => true;
        public Task WaitUntilOpenAsync(CancellationToken ct = default) => Task.Delay(Timeout.Infinite, ct);
        public void ReportGraphSettled(RepositoryContext context) { }
    }

    private static GitObjectId Oid(int i) => new((i + 1).ToString("x40"));

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

    private static Type RepoCacheType =>
        typeof(GraphLayoutService).GetNestedType("RepoCache", BindingFlags.NonPublic)!;

    private static (List<RawCommit> Rows, bool Complete) ReadWindow(object cache, int start, int count)
    {
        var method = typeof(GraphLayoutService).GetMethod("ReadWindow", BindingFlags.NonPublic | BindingFlags.Static)!;
        var read = method.Invoke(null, [cache, start, count])!;
        return ((List<RawCommit>)read.GetType().GetProperty("Rows")!.GetValue(read)!,
                (bool)read.GetType().GetProperty("Complete")!.GetValue(read)!);
    }

    [Fact]
    public void A_window_that_needs_disk_rows_is_incomplete_when_the_cache_holds_no_store()
    {
        var cache = Activator.CreateInstance(RepoCacheType)!;
        RepoCacheType.GetProperty("SpilledRows")!.SetValue(cache, 50);
        ((List<RawCommit>)RepoCacheType.GetProperty("Commits")!.GetValue(cache)!).AddRange(Enumerable.Range(50, 30).Select(Raw));

        var (_, wholeComplete) = ReadWindow(cache, 0, 80);
        Assert.False(wholeComplete);
        var (_, straddlingComplete) = ReadWindow(cache, 40, 30);
        Assert.False(straddlingComplete);

        var (tail, tailComplete) = ReadWindow(cache, 50, 30);
        Assert.True(tailComplete);
        Assert.Equal(Enumerable.Range(50, 30).Select(Oid), tail.Select(r => r.Id));
    }

    [Fact(Timeout = 60_000)]
    public async Task A_reset_by_another_writer_never_paints_a_shifted_layout()
    {
        const long foreignKey = 0x0F0F_1234_5678_9ABC;
        var repo = TestRepositoryBuilder.FromTemplate("linear-3000", b => b.LinearHistory(3_000));
        _repos.Add(repo);

        await _host.Settings.UpdateAsync(s => s with { GraphMetadataFromDisk = false });
        var memSvc = new GraphLayoutService(_host.Settings, _host.Locator);
        await using var memCtx = _host.Open(repo.Path);
        var memory = await memSvc.ComputeLayoutAsync(memCtx, Q);
        var expected = Enumerable.Range(0, memory.Layout.RowCount).Select(i => memory.Layout.Commits[i].Id).ToList();

        await _host.Settings.UpdateAsync(s => s with { GraphMetadataFromDisk = true });
        var svc = new GraphLayoutService(
            _host.Settings, _host.Locator, _host.Provider.GetRequiredService<IGitCliService>(), new PaintOnlyGate());
        await using var ctx = _host.Open(repo.Path);
        ctx.MarkFirstGraphPaintDone();
        ctx.MarkFirstFullLoadDone();

        var layout = await svc.ComputeLayoutAsync(ctx, Q, 100);
        Assert.Equal(100, layout.Layout.RowCount);

        // Another writer resets the store to a different walk: the extend's spill conflicts and the
        // cache drops the store it held, with 100 rows only on disk.
        var foreign = Enumerable.Range(0, 10).Select(Raw).ToList();
        Assert.Equal(SpillOutcome.Appended, new GraphMetaWriter(repo.Path, foreignKey, true).AppendSpillBatch(foreign, 0, 10, exhausted: false).Outcome);

        var extended = await svc.ExtendLayoutAsync(ctx, layout, additionalRows: 500);

        var store = extended.Layout.Commits;
        for (var i = 0; i < store.Count; i++)
            Assert.True(store[i].Id == expected[i] || store[i].Id == GitObjectId.Zero, $"row {i} paints a different commit");
    }
}
