namespace Historia.Tests;

/// <summary>
/// §10.1.3 M1.5, finding #7: <c>GraphLayoutService.ReadWindow</c> stitches confirmed-on-disk rows
/// and the resident pending buffer into one absolute-row-indexed list, and must never splice a
/// short disk read (a store disposed mid-read by <c>Forget</c>/<c>Hibernate</c>, an
/// <see cref="IOException"/>, or a concurrent from-row-0 re-spill all return fewer rows than
/// asked) — doing so would serve row <c>start+k</c> the commit that belongs at
/// <c>SpilledRows+k</c>. <c>ReadWindow</c> and its <c>RepoCache</c> input are private
/// implementation details of <see cref="GraphLayoutService"/>, not part of its public surface, so
/// this drives them directly via reflection rather than through the full async orchestration.
/// </summary>
public sealed class GraphReadWindowTests : IDisposable
{
    private readonly StateDir _state = new();
    private const long WalkKey = 0x1122_3344_5566_7788;
    private const string Repo = @"C:\repos\readwindow-demo";
    private const WalkEngine Engine = WalkEngine.LibGit2;

    public void Dispose() => _state.Dispose();

    private static ObjectId Oid(int i) => new(i.ToString("x40"));

    private static CommitMetadata Meta(int i) => new()
    {
        Id = Oid(i),
        MessageShort = $"commit {i}",
        AuthorName = "A",
        AuthorEmail = "a@x",
        AuthorWhen = DateTimeOffset.UnixEpoch.AddMinutes(i),
        CommitWhen = DateTimeOffset.UnixEpoch.AddMinutes(i),
        ParentCount = i == 0 ? 0 : 1,
    };

    private static RawCommit Raw(int i) => new(Oid(i), i == 0 ? [] : [Oid(i - 1)], Meta(i));

    private static List<RawCommit> RawRange(int from, int count) =>
        Enumerable.Range(from, count).Select(Raw).ToList();

    // ── reflection plumbing onto GraphLayoutService's private ReadWindow / RepoCache / WindowRead ──

    private static readonly Type CacheType =
        typeof(GraphLayoutService).GetNestedType("RepoCache", System.Reflection.BindingFlags.NonPublic)!;

    private static readonly System.Reflection.MethodInfo ReadWindowMethod =
        typeof(GraphLayoutService).GetMethod(
            "ReadWindow", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;

    private static object NewCache(int spilledRows, List<RawCommit> resident, DiskCommitStore? store)
    {
        var cache = Activator.CreateInstance(CacheType, nonPublic: true)!;
        ((List<RawCommit>)CacheType.GetProperty("Commits")!.GetValue(cache)!).AddRange(resident);
        CacheType.GetProperty("SpilledRows")!.SetValue(cache, spilledRows);
        CacheType.GetProperty("MetaStore")!.SetValue(cache, store);
        return cache;
    }

    private static (List<RawCommit> Rows, bool Complete) ReadWindow(object cache, int start, int count)
    {
        var boxed = ReadWindowMethod.Invoke(null, [cache, start, count])!;
        var t = boxed.GetType();
        var rows = (List<RawCommit>)t.GetProperty("Rows")!.GetValue(boxed)!;
        var complete = (bool)t.GetProperty("Complete")!.GetValue(boxed)!;
        return (rows, complete);
    }

    private static DiskCommitStore OpenStoreWithRows(int rowCount, bool exhausted)
    {
        var writer = new GraphMetaWriter(Repo, WalkKey, includeRemoteBranches: true, Engine);
        writer.Reset();
        var rows = RawRange(0, rowCount);
        writer.Append(rows, 0, rows.Count);
        writer.Commit(rowCount, exhausted);
        return GraphMetaStore.TryOpen(Repo, WalkKey, includeRemoteBranches: true, wantCount: rowCount, Engine)!;
    }

    [Fact]
    public void Complete_read_stitches_disk_and_resident_rows_with_every_row_at_its_walk_index()
    {
        using var store = OpenStoreWithRows(rowCount: 20, exhausted: false);
        Assert.NotNull(store);

        var resident = RawRange(20, 10); // rows [20, 30) still pending, not yet spilled
        var cache = NewCache(spilledRows: 20, resident, store);

        var (rows, complete) = ReadWindow(cache, start: 5, count: 20); // spans the disk/resident boundary
        Assert.True(complete);
        Assert.Equal(20, rows.Count);
        for (var i = 0; i < rows.Count; i++)
            Assert.Equal(Oid(5 + i), rows[i].Id); // every returned row equals the walk's row at that index
    }

    [Fact]
    public void A_disk_read_that_comes_back_short_is_reported_incomplete_and_never_spliced()
    {
        var store = OpenStoreWithRows(rowCount: 20, exhausted: false);
        Assert.NotNull(store);
        store.Dispose(); // simulates the store closing mid-read (Forget/Hibernate racing an extend)

        var resident = RawRange(20, 10);
        var cache = NewCache(spilledRows: 20, resident, store);

        // Still spans the (now-dead) disk range [5, 20) plus the resident tail.
        var (rows, complete) = ReadWindow(cache, start: 5, count: 20);
        Assert.False(complete);
        // The closed store's ReadRawRange returns nothing, and — the whole point of #7 — the
        // resident tail must NOT be appended after a short disk read (that would serve it at the
        // wrong absolute row).
        Assert.Empty(rows);
    }

    [Fact]
    public void A_window_entirely_within_the_resident_buffer_is_always_complete()
    {
        var resident = RawRange(0, 10);
        var cache = NewCache(spilledRows: 0, resident, store: null);

        var (rows, complete) = ReadWindow(cache, start: 2, count: 5);
        Assert.True(complete);
        Assert.Equal(5, rows.Count);
        for (var i = 0; i < rows.Count; i++)
            Assert.Equal(Oid(2 + i), rows[i].Id);
    }

    [Fact]
    public void Requesting_more_than_everything_walked_so_far_is_still_complete()
    {
        // Not enough has been walked yet — a normal, expected shortfall (e.g. a small repo), NOT
        // the disk-read fault #7 guards against: Complete stays true, the caller just gets fewer
        // rows than it asked for.
        var resident = RawRange(0, 3);
        var cache = NewCache(spilledRows: 0, resident, store: null);

        var (rows, complete) = ReadWindow(cache, start: 0, count: 1000);
        Assert.True(complete);
        Assert.Equal(3, rows.Count);
    }

    [Fact]
    public void An_exhausted_disk_only_store_serves_every_row_with_no_resident_buffer()
    {
        using var store = OpenStoreWithRows(rowCount: 15, exhausted: true);
        var cache = NewCache(spilledRows: 15, resident: [], store);

        var (rows, complete) = ReadWindow(cache, start: 0, count: 15);
        Assert.True(complete);
        Assert.Equal(15, rows.Count);
        for (var i = 0; i < rows.Count; i++)
            Assert.Equal(Oid(i), rows[i].Id);
    }

    /// <summary>End-to-end version of the two unit tests above, through the real public
    /// <see cref="GraphLayoutService.ExtendLayoutAsync"/> entry point: a disposed backing store
    /// must leave the layout completely unchanged (same reference) rather than hand back a
    /// misaligned one. The cache is injected via reflection into the service's private
    /// <c>_caches</c> table with <c>Exhausted: true</c> so <c>EnsureWalkedAsync</c> short-circuits
    /// before touching the repo at all — this test is purely about the ReadWindow/Extend path.
    /// </summary>
    [Fact]
    public async Task Extend_with_a_disposed_backing_store_returns_the_existing_layout_unchanged()
    {
        await using var host = new MiniHost();
        using var repo = TestRepositoryBuilder.Create();
        repo.WriteFile("f.txt", "x\n").Stage().Commit("initial");
        await using var ctx = host.Open(repo.Path);

        // 5 rows already laid out; 15 more confirmed "on disk" behind a store we then dispose —
        // simulating Forget/Hibernate racing this very extend (finding #7).
        using var store = OpenStoreWithRows(rowCount: 20, exhausted: false);
        store.Dispose();
        var cache = NewCache(spilledRows: 20, resident: [], store);
        CacheType.GetProperty("Exhausted")!.SetValue(cache, true);

        var svc = new GraphLayoutService();
        var cachesField = typeof(GraphLayoutService).GetField(
            "_caches", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var caches = cachesField.GetValue(svc)!;
        caches.GetType().GetMethod("Add")!.Invoke(caches, [ctx, cache]);

        var existing = new DagLayoutEngine().Compute(RawRange(0, 5), null, maxRows: 5);
        Assert.Equal(5, existing.State.Rows.Count);

        var result = await svc.ExtendLayoutAsync(ctx, existing, additionalRows: 5);

        Assert.Same(existing, result); // unchanged: the short disk read must never be spliced in
    }
}
