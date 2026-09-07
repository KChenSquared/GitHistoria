namespace Historia.Tests;

/// <summary>§7.6: the disk-backed <see cref="ICommitStore"/> — round-trip, append/grow,
/// graceful misses, and Ctrl+F search parity with the in-memory store.</summary>
public sealed class GraphMetaStoreTests : IDisposable
{
    private readonly StateDir _state = new();
    private const long Fp = 0x0123_4567_89ab_cdef;
    private const string Repo = @"C:\repos\demo";

    public void Dispose() => _state.Dispose();

    private static ObjectId Oid(int i) => new(i.ToString("x40"));

    private static CommitMetadata Row(int i) => new()
    {
        Id = Oid(i),
        MessageShort = $"commit number {i} — fix widget {(i % 3 == 0 ? "Alpha" : "beta")}",
        AuthorName = i % 2 == 0 ? "Ada Lovelace" : "Grace Hopper",
        AuthorEmail = $"dev{i}@example.com",
        AuthorWhen = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.FromHours(-8)).AddMinutes(i),
        CommitWhen = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.FromHours(-8)).AddMinutes(i + 1),
        ParentCount = i == 0 ? 0 : (i % 7 == 0 ? 2 : 1),
    };

    private static List<CommitMetadata> Rows(int n) => Enumerable.Range(0, n).Select(Row).ToList();

    private static void AssertRowEqual(CommitMetadata expected, CommitMetadata actual)
    {
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.MessageShort, actual.MessageShort);
        Assert.Equal(expected.AuthorName, actual.AuthorName);
        Assert.Equal(expected.AuthorEmail, actual.AuthorEmail);
        Assert.Equal(expected.AuthorWhen, actual.AuthorWhen);
        Assert.Equal(expected.CommitWhen, actual.CommitWhen);
        Assert.Equal(expected.ParentCount, actual.ParentCount);
    }

    private static void Write(int n, bool exhausted, long fingerprint = Fp, bool includeRemotes = true)
    {
        var w = new GraphMetaWriter(Repo, fingerprint, includeRemotes);
        w.Reset();
        var rows = Rows(n);
        w.Append(rows, 0, n);
        w.Commit(n, exhausted);
    }

    [Fact]
    public void Round_trips_every_row()
    {
        Write(200, exhausted: true);

        using var store = GraphMetaStore.TryOpen(Repo, Fp, includeRemoteBranches: true, wantCount: 200);
        Assert.NotNull(store);
        Assert.Equal(200, store!.Count);

        var src = Rows(200);
        for (var i = 0; i < 200; i++)
            AssertRowEqual(src[i], store[i]);
    }

    [Fact]
    public void Prefetch_then_index_returns_the_same_records()
    {
        Write(64, exhausted: true);
        using var store = GraphMetaStore.TryOpen(Repo, Fp, true, 64)!;

        store.Prefetch(10, 40);
        var src = Rows(64);
        for (var i = 10; i <= 40; i++)
            AssertRowEqual(src[i], store[i]);
    }

    [Fact]
    public void Append_grows_the_store_across_reopen()
    {
        var w = new GraphMetaWriter(Repo, Fp, true);
        w.Reset();
        w.Append(Rows(10), 0, 10);
        w.Commit(10, exhausted: false);

        w.Append(Rows(15), 10, 5);   // rows 10..14
        w.Commit(15, exhausted: true);

        using var store = GraphMetaStore.TryOpen(Repo, Fp, true, 15)!;
        Assert.Equal(15, store.Count);
        AssertRowEqual(Row(12), store[12]);
        AssertRowEqual(Row(0), store[0]);
    }

    [Fact]
    public void SetCount_adopts_rows_appended_after_open()
    {
        var w = new GraphMetaWriter(Repo, Fp, true);
        w.Reset();
        w.Append(Rows(10), 0, 10);
        w.Commit(10, exhausted: false);

        using var store = GraphMetaStore.TryOpen(Repo, Fp, true, 10)!;
        Assert.Equal(10, store.Count);

        w.Append(Rows(15), 10, 5);
        w.Commit(15, exhausted: true);
        store.SetCount(15);

        Assert.Equal(15, store.Count);
        AssertRowEqual(Row(13), store[13]);
    }

    [Fact]
    public void TryOpen_misses_on_fingerprint_or_flag_mismatch()
    {
        Write(50, exhausted: true);

        Assert.Null(GraphMetaStore.TryOpen(Repo, Fp + 1, true, 50));
        Assert.Null(GraphMetaStore.TryOpen(Repo, Fp, includeRemoteBranches: false, 50));
        Assert.Null(GraphMetaStore.TryOpen(@"C:\repos\other", Fp, true, 50));
    }

    [Fact]
    public void TryOpen_misses_when_short_of_want_count_and_not_exhausted()
    {
        Write(50, exhausted: false);

        Assert.Null(GraphMetaStore.TryOpen(Repo, Fp, true, 100));
        Assert.NotNull(GraphMetaStore.TryOpen(Repo, Fp, true, 40));
    }

    [Fact]
    public void Missing_index_file_is_a_clean_miss_not_a_throw()
    {
        Write(20, exhausted: true);
        File.Delete(GraphCachePaths.File(Repo, GraphMetaStore.IdxExt));

        Assert.Null(GraphMetaStore.TryOpen(Repo, Fp, true, 20));
    }

    [Fact]
    public void Search_matches_the_in_memory_store()
    {
        Write(300, exhausted: true);
        var mem = new ArrayCommitStore(Rows(300).ToArray());
        using var disk = GraphMetaStore.TryOpen(Repo, Fp, true, 300)!;

        foreach (var term in new[] { "Alpha", "grace hopper", "widget beta", "commit number 42", Oid(42).Sha, Oid(7).Sha[..8] })
        {
            Assert.Equal(mem.Search(term), disk.Search(term));
        }
    }

    [Fact]
    public void Search_matches_sha_prefix()
    {
        Write(40, exhausted: true);
        using var disk = GraphMetaStore.TryOpen(Repo, Fp, true, 40)!;

        // Every Oid(i).Sha is zero-padded, so a "0000" prefix hits every row while the full
        // 40-char SHA hits exactly one — same as the in-memory store.
        Assert.Equal(40, disk.Search("0000").Count);
        Assert.Equal(new[] { 31 }, disk.Search(Oid(31).Sha));
    }

    [Fact]
    public void Lease_keeps_the_store_readable_until_the_last_lease_is_released()
    {
        Write(20, exhausted: true);
        var store = GraphMetaStore.TryOpen(Repo, Fp, true, 20)!;   // opens holding one lease

        var extra = store.AddRef();                                // a second lease
        Assert.Same(store, extra);

        store.Dispose();                                           // drop the first — still leased
        AssertRowEqual(Row(5), store[5]);                          // reads keep working

        extra!.Dispose();                                          // drop the last — now closed
        Assert.Equal(ObjectId.Zero, store[5].Id);                  // reads serve the closed stub
    }

    [Fact]
    public void AddRef_refuses_a_closed_store_and_never_resurrects_it()
    {
        Write(10, exhausted: true);
        var store = GraphMetaStore.TryOpen(Repo, Fp, true, 10)!;

        store.Dispose();                        // last lease gone -> closed
        Assert.Null(store.AddRef());            // a closed store cannot hand out a new lease
        Assert.Equal(ObjectId.Zero, store[3].Id);

        store.Dispose();                        // over-release is tolerated (no throw)
        Assert.Null(store.AddRef());            // still refuses — the race can't revive it
    }

    [Fact]
    public void In_memory_store_lease_never_fails()
    {
        var mem = new ArrayCommitStore(Rows(3).ToArray());
        Assert.Same(mem, mem.AddRef());
        mem.Dispose();
        Assert.Same(mem, mem.AddRef());         // nothing to close, so AddRef never returns null
        AssertRowEqual(Row(1), mem[1]);
    }

    [Fact]
    public void Wipe_removes_every_meta_file()
    {
        Write(10, exhausted: true);
        Assert.True(File.Exists(GraphCachePaths.File(Repo, GraphMetaStore.HdrExt)));

        var removed = GraphMetaStore.Wipe();

        Assert.True(removed >= 4);
        Assert.False(File.Exists(GraphCachePaths.File(Repo, GraphMetaStore.DatExt)));
    }
}
