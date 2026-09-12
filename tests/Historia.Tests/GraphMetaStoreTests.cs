namespace Historia.Tests;

/// <summary>§7.6: the disk-backed <see cref="ICommitStore"/> — round-trip, append/grow,
/// graceful misses, and Ctrl+F search parity with the in-memory store.</summary>
public sealed class GraphMetaStoreTests : IDisposable
{
    private readonly StateDir _state = new();
    private const long Fp = 0x0123_4567_89ab_cdef;
    private const string Repo = @"C:\repos\demo";
    private const WalkEngine Engine = WalkEngine.LibGit2;

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

    // Row(i)'s parent i-1 (or i-1 and i-2 when ParentCount == 2) — a real chain, so ReadRaw can
    // assert actual parent SHAs round-trip, not just ParentCount.
    private static RawCommit RawRow(int i)
    {
        var m = Row(i);
        IReadOnlyList<ObjectId> parents = m.ParentCount switch
        {
            0 => [],
            2 => [Oid(i - 1), Oid(i - 2)],
            _ => [Oid(i - 1)],
        };
        return new RawCommit(m.Id, parents, m);
    }

    private static List<RawCommit> RawRows(int n) => Enumerable.Range(0, n).Select(RawRow).ToList();

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
        var w = new GraphMetaWriter(Repo, fingerprint, includeRemotes, Engine);
        w.Reset();
        var rows = Rows(n);
        w.Append(rows, 0, n);
        w.Commit(n, exhausted);
    }

    [Fact]
    public void Round_trips_every_row()
    {
        Write(200, exhausted: true);

        using var store = GraphMetaStore.TryOpen(Repo, Fp, includeRemoteBranches: true, wantCount: 200, Engine);
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
        using var store = GraphMetaStore.TryOpen(Repo, Fp, true, 64, Engine)!;

        store.Prefetch(10, 40);
        var src = Rows(64);
        for (var i = 10; i <= 40; i++)
            AssertRowEqual(src[i], store[i]);
    }

    [Fact]
    public void Append_grows_the_store_across_reopen()
    {
        var w = new GraphMetaWriter(Repo, Fp, true, Engine);
        w.Reset();
        w.Append(Rows(10), 0, 10);
        w.Commit(10, exhausted: false);

        w.Append(Rows(15), 10, 5);   // rows 10..14
        w.Commit(15, exhausted: true);

        using var store = GraphMetaStore.TryOpen(Repo, Fp, true, 15, Engine)!;
        Assert.Equal(15, store.Count);
        AssertRowEqual(Row(12), store[12]);
        AssertRowEqual(Row(0), store[0]);
    }

    [Fact]
    public void SetCount_adopts_rows_appended_after_open()
    {
        var w = new GraphMetaWriter(Repo, Fp, true, Engine);
        w.Reset();
        w.Append(Rows(10), 0, 10);
        w.Commit(10, exhausted: false);

        using var store = GraphMetaStore.TryOpen(Repo, Fp, true, 10, Engine)!;
        Assert.Equal(10, store.Count);

        w.Append(Rows(15), 10, 5);
        w.Commit(15, exhausted: true);
        store.SetCount(15);

        Assert.Equal(15, store.Count);
        AssertRowEqual(Row(13), store[13]);
    }

    /// <summary>Phase 3a: <c>SetCount</c> reads only the <c>.idx</c> tail appended since the
    /// last call (a background spill calls it once per batch) instead of re-reading the whole
    /// file every time. This is a correctness check across several incremental calls — every
    /// previously-adopted row must still resolve correctly after each new tail read merges in.</summary>
    [Fact]
    public void SetCount_called_repeatedly_across_many_small_appends_stays_correct()
    {
        var w = new GraphMetaWriter(Repo, Fp, true, Engine);
        w.Reset();
        w.Append(Rows(5), 0, 5);
        w.Commit(5, exhausted: false);

        using var store = GraphMetaStore.TryOpen(Repo, Fp, true, 5, Engine)!;
        Assert.Equal(5, store.Count);

        for (var batch = 1; batch <= 6; batch++)
        {
            var from = 5 * batch;
            w.Append(Rows(from + 5), from, 5);
            w.Commit(from + 5, exhausted: batch == 6);
            store.SetCount(from + 5);
            Assert.Equal(from + 5, store.Count);

            // Every row adopted so far — not just the newest batch — must still decode
            // correctly; a tail-only re-read that mishandles the merge would corrupt earlier
            // offsets.
            AssertRowEqual(Row(0), store[0]);
            AssertRowEqual(Row(from + 2), store[from + 2]);
            AssertRowEqual(Row(from + 4), store[from + 4]);
        }
    }

    [Fact]
    public void TryOpen_misses_on_fingerprint_or_flag_mismatch()
    {
        Write(50, exhausted: true);

        Assert.Null(GraphMetaStore.TryOpen(Repo, Fp + 1, true, 50, Engine));
        Assert.Null(GraphMetaStore.TryOpen(Repo, Fp, includeRemoteBranches: false, 50, Engine));
        Assert.Null(GraphMetaStore.TryOpen(@"C:\repos\other", Fp, true, 50, Engine));
    }

    [Fact]
    public void TryOpen_misses_on_engine_mismatch()
    {
        Write(50, exhausted: true);

        Assert.Null(GraphMetaStore.TryOpen(Repo, Fp, true, 50, WalkEngine.Cli));
        Assert.NotNull(GraphMetaStore.TryOpen(Repo, Fp, true, 50, WalkEngine.LibGit2));
    }

    [Fact]
    public void TryOpen_misses_when_short_of_want_count_and_not_exhausted()
    {
        Write(50, exhausted: false);

        Assert.Null(GraphMetaStore.TryOpen(Repo, Fp, true, 100, Engine));
        Assert.NotNull(GraphMetaStore.TryOpen(Repo, Fp, true, 40, Engine));
    }

    [Fact]
    public void Missing_index_file_is_a_clean_miss_not_a_throw()
    {
        Write(20, exhausted: true);
        File.Delete(GraphCachePaths.File(Repo, GraphMetaStore.IdxExt));

        Assert.Null(GraphMetaStore.TryOpen(Repo, Fp, true, 20, Engine));
    }

    [Fact]
    public void Search_matches_the_in_memory_store()
    {
        Write(300, exhausted: true);
        var mem = new ArrayCommitStore(Rows(300).ToArray());
        using var disk = GraphMetaStore.TryOpen(Repo, Fp, true, 300, Engine)!;

        foreach (var term in new[] { "Alpha", "grace hopper", "widget beta", "commit number 42", Oid(42).Sha, Oid(7).Sha[..8] })
        {
            Assert.Equal(mem.Search(term), disk.Search(term));
        }
    }

    [Fact]
    public void Search_matches_sha_prefix()
    {
        Write(40, exhausted: true);
        using var disk = GraphMetaStore.TryOpen(Repo, Fp, true, 40, Engine)!;

        // Every Oid(i).Sha is zero-padded, so a "0000" prefix hits every row while the full
        // 40-char SHA hits exactly one — same as the in-memory store.
        Assert.Equal(40, disk.Search("0000").Count);
        Assert.Equal(new[] { 31 }, disk.Search(Oid(31).Sha));
    }

    [Fact]
    public void Lease_keeps_the_store_readable_until_the_last_lease_is_released()
    {
        Write(20, exhausted: true);
        var store = GraphMetaStore.TryOpen(Repo, Fp, true, 20, Engine)!;   // opens holding one lease

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
        var store = GraphMetaStore.TryOpen(Repo, Fp, true, 10, Engine)!;

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
    public void ReadRaw_round_trips_real_parent_shas_from_a_RawCommit_spill()
    {
        var w = new GraphMetaWriter(Repo, Fp, true, Engine);
        w.Reset();
        var rows = RawRows(20);
        w.Append(rows, 0, 20);
        w.Commit(20, exhausted: true);

        using var store = GraphMetaStore.TryOpen(Repo, Fp, true, 20, Engine)!;
        for (var i = 0; i < 20; i++)
        {
            var raw = store.ReadRaw(i);
            Assert.Equal(rows[i].Id, raw.Id);
            Assert.Equal(rows[i].ParentIds, raw.ParentIds);
            AssertRowEqual(rows[i].Metadata, raw.Metadata);
        }
    }

    [Fact]
    public void ReadRawRange_matches_ReadRaw_for_every_row_in_a_contiguous_span()
    {
        var w = new GraphMetaWriter(Repo, Fp, true, Engine);
        w.Reset();
        var rows = RawRows(50);
        w.Append(rows, 0, 50);
        w.Commit(50, exhausted: true);

        using var store = GraphMetaStore.TryOpen(Repo, Fp, true, 50, Engine)!;
        var range = store.ReadRawRange(10, 20); // rows [10, 30)
        Assert.Equal(20, range.Count);
        for (var i = 0; i < range.Count; i++)
        {
            var expected = store.ReadRaw(10 + i);
            Assert.Equal(expected.Id, range[i].Id);
            Assert.Equal(expected.ParentIds, range[i].ParentIds);
            AssertRowEqual(expected.Metadata, range[i].Metadata);
        }
    }

    [Fact]
    public void ReadRawRange_clamps_to_the_store_count()
    {
        var w = new GraphMetaWriter(Repo, Fp, true, Engine);
        w.Reset();
        var rows = RawRows(10);
        w.Append(rows, 0, 10);
        w.Commit(10, exhausted: true);

        using var store = GraphMetaStore.TryOpen(Repo, Fp, true, 10, Engine)!;
        var range = store.ReadRawRange(5, 100); // asks for far more than exists
        Assert.Equal(5, range.Count); // rows [5, 10)
        for (var i = 0; i < range.Count; i++)
            Assert.Equal(rows[5 + i].Id, range[i].Id);
    }

    [Fact]
    public void ReadRawRange_returns_empty_for_an_out_of_range_or_empty_request()
    {
        var w = new GraphMetaWriter(Repo, Fp, true, Engine);
        w.Reset();
        w.Append(RawRows(5), 0, 5);
        w.Commit(5, exhausted: true);

        using var store = GraphMetaStore.TryOpen(Repo, Fp, true, 5, Engine)!;
        Assert.Empty(store.ReadRawRange(5, 10));  // starts at Count
        Assert.Empty(store.ReadRawRange(0, 0));   // zero count
        Assert.Empty(store.ReadRawRange(2, -1));  // negative count
    }

    [Fact]
    public void ReadRaw_zero_fills_parents_written_via_the_CommitMetadata_only_overload()
    {
        // The bare-CommitMetadata Append overload (no RawCommit available) has no real parent
        // SHAs to write — ReadRaw must come back with ParentCount zero-SHA placeholders, not
        // throw or truncate the record.
        Write(10, exhausted: true);

        using var store = GraphMetaStore.TryOpen(Repo, Fp, true, 10, Engine)!;
        var raw = store.ReadRaw(6); // Row(6).ParentCount == 1 (6 % 7 != 0)
        Assert.Equal(Row(6).ParentCount, raw.ParentIds.Count);
        Assert.All(raw.ParentIds, p => Assert.Equal(ObjectId.Zero, p));
    }

    [Fact]
    public void A_version_1_style_header_is_rejected_not_misread()
    {
        // Simulates a store written before v10.1.2 added parent bytes to .dat: same layout, one
        // version number lower. TryOpen/Probe/ProbeAny must all treat it as absent, never try to
        // decode records in the old (parent-less) shape.
        Write(10, exhausted: true);
        var hdrPath = GraphCachePaths.File(Repo, GraphMetaStore.HdrExt);
        var hdr = JsonSerializer.Deserialize<GraphMetaStore.Header>(File.ReadAllBytes(hdrPath), GraphMetaStore.Json)!;
        hdr.Version = GraphMetaStore.Version - 1;
        File.WriteAllBytes(hdrPath, JsonSerializer.SerializeToUtf8Bytes(hdr, GraphMetaStore.Json));

        Assert.Null(GraphMetaStore.TryOpen(Repo, Fp, true, 10, Engine));
        Assert.Equal((false, 0, false), GraphMetaStore.Probe(Repo, Fp, true, Engine));
        Assert.Equal((false, 0), GraphMetaStore.ProbeAny(Repo, true, Engine));
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
