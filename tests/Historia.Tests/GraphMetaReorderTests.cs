namespace Historia.Tests;

/// <summary>
/// Commit records are split from row order.
/// <see cref="GraphMetaReorder"/> is the pure planning half (no file/git access — see its own
/// doc comment); these tests exercise it directly, then exercise the on-disk order indirection
/// it feeds (<see cref="GraphMetaWriter.WriteOrder"/> / <see cref="GraphMetaWriter.CommitReorder"/>
/// / <c>DiskCommitStore</c> reading through an explicit order) end to end against real files.
/// </summary>
[Trait("tier", "unit")]
public sealed class GraphMetaReorderTests : IDisposable
{
    private readonly StateDir _state = new();
    private const long WalkKey = 0x0123_4567_89ab_cdef;
    private const string Repo = @"C:\repos\reorder-demo";
    private const WalkEngine Engine = WalkEngine.LibGit2;

    public void Dispose() => _state.Dispose();

    private static ObjectId Oid(int i) => new(i.ToString("x40"));

    private static RawCommit Commit(int i, params int[] parents) => new(
        Oid(i),
        [.. parents.Select(Oid)],
        new CommitMetadata
        {
            Id = Oid(i),
            MessageShort = $"commit {i}",
            AuthorName = "Ada Lovelace",
            AuthorEmail = "ada@example.com",
            AuthorWhen = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(i),
            CommitWhen = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(i),
            ParentCount = parents.Length,
        });

    // ── GraphMetaReorder.Build (pure, no I/O) ──

    /// <summary><see cref="GraphMetaReorder.Build"/> keys existing records on
    /// <see cref="CompactShaIndex"/> (an 8-byte id prefix, verified against the full id) instead
    /// of a 40-char hex string — this builds one from the same (id, record) pairs the old
    /// <c>Dictionary&lt;string,int&gt;</c> literals in these tests used to express.</summary>
    private static CompactShaIndex Existing(params (int Id, int Record)[] pairs)
    {
        var index = new CompactShaIndex();
        foreach (var (id, record) in pairs)
            index.Add(Oid(id), record);
        return index;
    }

    [Fact]
    public void Build_keeps_existing_records_and_appends_only_genuinely_new_commits()
    {
        // Old order (identity, 5 rows): 0,1,2,3,4 — records 0..4.
        var existing = Existing((0, 0), (1, 1), (2, 2), (3, 3), (4, 4));

        // New walk: two brand-new commits land on top of the old trunk (a fetch advancing HEAD).
        var newWalk = new List<RawCommit> { Commit(5, 4), Commit(6, 5), Commit(4), Commit(3, 4), Commit(2, 3), Commit(1, 2), Commit(0, 1) };

        var plan = GraphMetaReorder.Build(existing, existingRecordCount: 5, newWalk);

        Assert.Equal(2, plan.NewRecords.Count);
        Assert.Equal(Oid(5), plan.NewRecords[0].Id);
        Assert.Equal(Oid(6), plan.NewRecords[1].Id);

        // New records get the next record indices in walk-encounter order.
        Assert.Equal([5, 6, 4, 3, 2, 1, 0], plan.Order);
    }

    [Fact]
    public void Build_is_a_no_op_reuse_when_the_new_walk_is_identical_to_the_old_one()
    {
        var existing = Existing((0, 0), (1, 1), (2, 2));
        var sameWalk = new List<RawCommit> { Commit(2, 1), Commit(1, 0), Commit(0) };

        var plan = GraphMetaReorder.Build(existing, existingRecordCount: 3, sameWalk);

        Assert.Empty(plan.NewRecords);
        Assert.Equal([2, 1, 0], plan.Order);
    }

    [Fact]
    public void Build_drops_rows_for_commits_no_longer_in_the_new_walk()
    {
        // Old history had a branch (commits 10, 11) that's gone from the new walk (deleted / not
        // reachable any more) — Build only ever emits rows for what's IN newWalkCommits; the
        // caller decides row count, this just never references dead records.
        var existing = Existing((0, 0), (10, 1), (11, 2));
        var newWalk = new List<RawCommit> { Commit(0) };

        var plan = GraphMetaReorder.Build(existing, existingRecordCount: 3, newWalk);

        Assert.Single(plan.Order);
        Assert.Equal(0, plan.Order[0]);
        Assert.Empty(plan.NewRecords);
    }

    [Fact]
    public void Build_never_appends_a_duplicate_record_for_a_sha_seen_twice_in_one_walk()
    {
        // Defensive: a real walk never revisits a commit, but Build must not misbehave if it did.
        var existing = new CompactShaIndex();
        var newWalk = new List<RawCommit> { Commit(0), Commit(0) };

        var plan = GraphMetaReorder.Build(existing, existingRecordCount: 0, newWalk);

        Assert.Single(plan.NewRecords);
        Assert.Equal([0, 0], plan.Order);
    }

    // ── End to end: write an order, read it back through DiskCommitStore ──

    private static GraphMetaWriter FreshIdentityStore(int n)
    {
        var w = new GraphMetaWriter(Repo, WalkKey, true, Engine);
        w.Reset();
        var rows = Enumerable.Range(0, n).Select(i => Commit(i, i == 0 ? [] : [i - 1])).ToList();
        w.Append(rows, 0, n);
        w.Commit(n, exhausted: true);
        return w;
    }

    [Fact]
    public void Reordered_store_serves_rows_in_the_new_order_reusing_old_records()
    {
        var w = FreshIdentityStore(5); // records/rows 0..4, in that order

        using (var store = GraphMetaStore.TryOpen(Repo, WalkKey, true, 5, Engine)!)
        {
            var existing = store.BuildShaIndex();
            Assert.Equal(5, existing.Count);

            var newWalk = new List<RawCommit> { Commit(5, 4), Commit(4), Commit(3, 4), Commit(2, 3), Commit(1, 2), Commit(0, 1) };
            var plan = GraphMetaReorder.Build(existing, store.RecordCount, newWalk);

            w.Append(plan.NewRecords, 0, plan.NewRecords.Count);
            Assert.True(w.WriteOrder(version: 1, plan.Order));
            w.WriteTips([Oid(5)]);
            w.CommitReorder(rowCount: plan.Order.Length, recordCount: store.RecordCount + plan.NewRecords.Count, orderVersion: 1, exhausted: true);
        }

        using var reordered = GraphMetaStore.TryOpen(Repo, WalkKey, true, 6, Engine)!;
        Assert.Equal(6, reordered.Count);
        Assert.Equal(6, reordered.RecordCount); // 5 old + 1 new (commit 5)

        // Rows now read back in the NEW walk's order.
        Assert.Equal(Oid(5), reordered.ReadRaw(0).Id);
        Assert.Equal(Oid(4), reordered.ReadRaw(1).Id);
        Assert.Equal(Oid(3), reordered.ReadRaw(2).Id);
        Assert.Equal(Oid(0), reordered.ReadRaw(5).Id);
        AssertMetadataEqual(Commit(5, 4).Metadata, reordered[0]);
    }

    [Fact]
    public void ReadRawRange_over_a_reordered_store_matches_ReadRaw_per_row()
    {
        var w = FreshIdentityStore(6);
        using (var store = GraphMetaStore.TryOpen(Repo, WalkKey, true, 6, Engine)!)
        {
            var existing = store.BuildShaIndex();
            // Reverse the order — no new commits, pure reshuffle.
            var newWalk = Enumerable.Range(0, 6).Reverse().Select(i => Commit(i)).ToList();
            var plan = GraphMetaReorder.Build(existing, store.RecordCount, newWalk);
            Assert.Empty(plan.NewRecords);
            w.WriteOrder(1, plan.Order);
            w.WriteTips([Oid(5)]);
            w.CommitReorder(6, store.RecordCount, 1, exhausted: true);
        }

        using var reordered = GraphMetaStore.TryOpen(Repo, WalkKey, true, 6, Engine)!;
        var range = reordered.ReadRawRange(0, 6);
        Assert.Equal(6, range.Count);
        for (var row = 0; row < 6; row++)
            Assert.Equal(reordered.ReadRaw(row).Id, range[row].Id);
        // Confirms the reversal actually took effect (not accidentally still identity).
        Assert.Equal(Oid(5), range[0].Id);
        Assert.Equal(Oid(0), range[5].Id);
    }

    [Fact]
    public void Search_maps_record_hits_back_to_rows_after_a_reorder()
    {
        var w = FreshIdentityStore(4); // "commit 0".."commit 3", records 0..3
        using (var store = GraphMetaStore.TryOpen(Repo, WalkKey, true, 4, Engine)!)
        {
            var existing = store.BuildShaIndex();
            var newWalk = new List<RawCommit> { Commit(3), Commit(1), Commit(2), Commit(0) }; // reshuffled
            var plan = GraphMetaReorder.Build(existing, store.RecordCount, newWalk);
            w.WriteOrder(1, plan.Order);
            w.WriteTips([Oid(3)]);
            w.CommitReorder(4, store.RecordCount, 1, exhausted: true);
        }

        using var reordered = GraphMetaStore.TryOpen(Repo, WalkKey, true, 4, Engine)!;
        // "commit 2" is record 2, which the new order places at row 2.
        Assert.Equal([2], reordered.Search("commit 2"));
        // A term matching everything should hit every row, sorted.
        Assert.Equal([0, 1, 2, 3], reordered.Search("commit"));
    }

    [Fact]
    public void A_reorder_cancelled_before_CommitReorder_leaves_the_old_identity_order_intact()
    {
        var w = FreshIdentityStore(3);
        using (var store = GraphMetaStore.TryOpen(Repo, WalkKey, true, 3, Engine)!)
        {
            var existing = store.BuildShaIndex();
            var plan = GraphMetaReorder.Build(existing, store.RecordCount, [Commit(2), Commit(0), Commit(1)]);
            // Simulate a cancelled reorder: the order file gets written (or not — either way,
            // since CommitReorder is never called) but the header is never switched over.
            w.WriteOrder(1, plan.Order);
        }

        using var stillIdentity = GraphMetaStore.TryOpen(Repo, WalkKey, true, 3, Engine)!;
        Assert.Equal(Oid(0), stillIdentity.ReadRaw(0).Id);
        Assert.Equal(Oid(1), stillIdentity.ReadRaw(1).Id);
        Assert.Equal(Oid(2), stillIdentity.ReadRaw(2).Id);
    }

    [Fact]
    public void Version_5_header_round_trips_RecordCount_and_OrderVersion()
    {
        var w = FreshIdentityStore(3);
        using (var store = GraphMetaStore.TryOpen(Repo, WalkKey, true, 3, Engine)!)
        {
            var plan = GraphMetaReorder.Build(store.BuildShaIndex(), store.RecordCount, [Commit(2), Commit(1), Commit(0)]);
            w.WriteOrder(1, plan.Order);
            w.CommitReorder(3, store.RecordCount, 1, exhausted: true);
        }

        var probe = GraphMetaStore.Probe(Repo, WalkKey, true, Engine);
        Assert.True(probe.Ok);
        Assert.Equal(3, probe.RowCount);
    }

    [Fact]
    public void A_version_4_style_header_is_rejected_not_misread()
    {
        FreshIdentityStore(3);
        var hdrPath = GraphCachePaths.File(Repo, GraphMetaStore.HdrExt);
        var json = $$"""{"Version":4,"Fingerprint":{{WalkKey}},"IncludeRemoteBranches":true,"RowCount":3,"Exhausted":true,"WalkEngine":0}""";
        File.WriteAllText(hdrPath, json);

        Assert.Null(GraphMetaStore.TryOpen(Repo, WalkKey, true, 3, Engine));
    }

    private static void AssertMetadataEqual(CommitMetadata expected, CommitMetadata actual)
    {
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.MessageShort, actual.MessageShort);
        Assert.Equal(expected.AuthorWhen, actual.AuthorWhen);
        Assert.Equal(expected.CommitWhen, actual.CommitWhen);
    }
}
