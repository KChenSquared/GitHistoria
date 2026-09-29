namespace Historia.Tests;

/// <summary>
/// Verifies that <see cref="GraphMetaStore"/> and <see cref="DiskCommitStore"/> support 32-byte (SHA-256)
/// object ids, reject mixed hash sizes, and map absent or zero header hash sizes to 20.
/// </summary>
[Trait("tier", "unit")]
public sealed class GraphMetaStoreSha256Tests : IDisposable
{
    private readonly StateDir _state = new();
    private const long Fp = 0x0123_4567_89ab_cdef;
    private const string Repo = @"C:\repos\sha256demo";

    public void Dispose() => _state.Dispose();

    private static GitObjectId Oid256(int i) => GitObjectId.Parse(i.ToString("x64"));
    private static GitObjectId Oid1(int i) => GitObjectId.Parse(i.ToString("x40"));

    private static CommitMetadata Row256(int i) => new()
    {
        Id = Oid256(i),
        MessageShort = $"commit number {i} — sha256 widget {(i % 3 == 0 ? "Alpha" : "beta")}",
        AuthorName = i % 2 == 0 ? "Ada Lovelace" : "Grace Hopper",
        AuthorEmail = $"dev{i}@example.com",
        AuthorWhen = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.FromHours(-8)).AddMinutes(i),
        CommitWhen = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.FromHours(-8)).AddMinutes(i + 1),
        ParentCount = i == 0 ? 0 : (i % 7 == 0 ? 2 : 1),
    };

    private static List<CommitMetadata> Rows256(int n) => Enumerable.Range(0, n).Select(Row256).ToList();

    private static RawCommit RawRow256(int i)
    {
        var m = Row256(i);
        IReadOnlyList<GitObjectId> parents = m.ParentCount switch
        {
            0 => [],
            2 => [Oid256(i - 1), Oid256(i - 2)],
            _ => [Oid256(i - 1)],
        };
        return new RawCommit(m.Id, parents, m);
    }

    private static List<RawCommit> RawRows256(int n) => Enumerable.Range(0, n).Select(RawRow256).ToList();

    private static CommitMetadata Row1(int i) => new()
    {
        Id = Oid1(i),
        MessageShort = $"commit number {i} — sha1 widget",
        AuthorName = "Dev One",
        AuthorEmail = "dev1@example.com",
        AuthorWhen = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        CommitWhen = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        ParentCount = 0,
    };

    private static void AssertRowEqual(CommitMetadata expected, CommitMetadata actual)
    {
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.Id.HashSize, actual.Id.HashSize);
        Assert.Equal(expected.MessageShort, actual.MessageShort);
        Assert.Equal(expected.AuthorName, actual.AuthorName);
        Assert.Equal(expected.AuthorEmail, actual.AuthorEmail);
        Assert.Equal(expected.AuthorWhen, actual.AuthorWhen);
        Assert.Equal(expected.CommitWhen, actual.CommitWhen);
        Assert.Equal(expected.ParentCount, actual.ParentCount);
    }

    private static void Write256(int n, bool exhausted, long fingerprint = Fp, bool includeRemotes = true)
    {
        var w = new GraphMetaWriter(Repo, fingerprint, includeRemotes);
        w.Reset();
        var rows = Rows256(n);
        w.Append(rows, 0, n);
        w.Commit(n, exhausted);
    }

    [Fact]
    public void Round_trips_every_sha256_row()
    {
        Write256(100, exhausted: true);

        using var store = GraphMetaStore.TryOpen(Repo, Fp, includeRemoteBranches: true, wantCount: 100);
        Assert.NotNull(store);
        Assert.Equal(32, store!.HashSize);
        Assert.Equal(100, store.Count);

        var src = Rows256(100);
        for (var i = 0; i < 100; i++)
            AssertRowEqual(src[i], store[i]);
    }

    [Fact]
    public void ReadRaw_round_trips_sha256_parent_shas()
    {
        var w = new GraphMetaWriter(Repo, Fp, true);
        w.Reset();
        var rows = RawRows256(30);
        w.Append(rows, 0, 30);
        w.Commit(30, exhausted: true);

        using var store = GraphMetaStore.TryOpen(Repo, Fp, true, 30)!;
        Assert.Equal(32, store.HashSize);
        for (var i = 0; i < 30; i++)
        {
            var raw = store.ReadRaw(i);
            Assert.Equal(rows[i].Id, raw.Id);
            Assert.Equal(32, raw.Id.HashSize);
            Assert.Equal(rows[i].ParentIds.Count, raw.ParentIds.Count);
            for (var p = 0; p < raw.ParentIds.Count; p++)
            {
                Assert.Equal(rows[i].ParentIds[p], raw.ParentIds[p]);
                Assert.Equal(32, raw.ParentIds[p].HashSize);
            }
            AssertRowEqual(rows[i].Metadata, raw.Metadata);
        }
    }

    [Fact]
    public void ReadRawRange_matches_ReadRaw_for_sha256()
    {
        var w = new GraphMetaWriter(Repo, Fp, true);
        w.Reset();
        var rows = RawRows256(50);
        w.Append(rows, 0, 50);
        w.Commit(50, exhausted: true);

        using var store = GraphMetaStore.TryOpen(Repo, Fp, true, 50)!;
        var range = store.ReadRawRange(10, 20);
        Assert.Equal(20, range.Count);
        for (var i = 0; i < range.Count; i++)
        {
            var expected = store.ReadRaw(10 + i);
            Assert.Equal(expected.Id, range[i].Id);
            Assert.Equal(32, range[i].Id.HashSize);
            Assert.Equal(expected.ParentIds, range[i].ParentIds);
            AssertRowEqual(expected.Metadata, range[i].Metadata);
        }
    }

    [Fact]
    public void Prefetch_and_Search_with_sha256()
    {
        Write256(64, exhausted: true);
        using var store = GraphMetaStore.TryOpen(Repo, Fp, true, 64)!;

        store.Prefetch(10, 30);
        var src = Rows256(64);
        for (var i = 10; i <= 30; i++)
            AssertRowEqual(src[i], store[i]);

        var mem = new ArrayCommitStore(src.ToArray());
        foreach (var term in new[] { "Alpha", "grace hopper", "widget beta", "commit number 42", Oid256(42).ToString(), Oid256(7).ToShortString(12) })
        {
            Assert.Equal(mem.Search(term), store.Search(term));
        }
    }

    [Fact]
    public void Tips_round_trip_with_sha256()
    {
        var w = new GraphMetaWriter(Repo, Fp, true);
        w.Reset();
        var rows = Rows256(10);
        w.Append(rows, 0, 10);
        w.Commit(10, exhausted: true);

        var tips = new[] { Oid256(3), Oid256(7), Oid256(9) };
        w.WriteTips(tips);

        var read = GraphMetaStore.ReadTips(Repo);
        Assert.NotNull(read);
        Assert.Equal(3, read!.Count);
        for (var i = 0; i < tips.Length; i++)
        {
            Assert.Equal(tips[i], read[i]);
            Assert.Equal(32, read[i].HashSize);
        }
    }

    [Fact]
    public void Reordering_round_trip_with_sha256()
    {
        var w = new GraphMetaWriter(Repo, Fp, true);
        w.Reset();
        var initial = RawRows256(20);
        w.Append(initial, 0, 20);
        w.Commit(20, exhausted: true);

        using var storeBefore = GraphMetaStore.TryOpen(Repo, Fp, true, 20)!;
        var shaIndex = storeBefore.BuildShaIndex();
        Assert.Equal(20, shaIndex.Count);

        for (var i = 0; i < 20; i++)
        {
            Assert.True(shaIndex.TryGetValue(initial[i].Id, out var rec));
            Assert.Equal(i, rec);
        }

        // New walk reverses the first 10 commits and introduces 5 brand-new commits.
        var newCommits = new List<RawCommit>();
        for (var i = 9; i >= 0; i--)
            newCommits.Add(initial[i]);
        for (var i = 20; i < 25; i++)
            newCommits.Add(RawRow256(i));

        var plan = GraphMetaReorder.Build(shaIndex, storeBefore.RecordCount, newCommits);
        Assert.Equal(15, plan.Order.Length);
        Assert.Equal(5, plan.NewRecords.Count);

        w.AppendReorderBatch(plan.NewRecords, Fp);
        var tips = new[] { newCommits[0].Id, newCommits[^1].Id };
        var reorderSuccess = w.TryCommitReorder(Fp, true, 0, 20, plan.NewRecords.Count, plan.Order, tips);
        Assert.True(reorderSuccess);

        using var storeAfter = GraphMetaStore.TryOpen(Repo, Fp, true, 15)!;
        Assert.Equal(15, storeAfter.Count);
        Assert.Equal(32, storeAfter.HashSize);
        for (var row = 0; row < 15; row++)
        {
            var expectedId = newCommits[row].Id;
            Assert.Equal(expectedId, storeAfter[row].Id);
        }
    }

    [Fact]
    public void Older_header_without_HashSize_opens_as_hash_size_20()
    {
        var w = new GraphMetaWriter(Repo, Fp, true);
        w.Reset();
        var rows = Enumerable.Range(0, 10).Select(i => Row1(i)).ToList();
        w.Append(rows, 0, 10);
        w.Commit(10, exhausted: true);

        var hdrPath = GraphCachePaths.File(Repo, GraphMetaStore.HdrExt);
        var jsonText = File.ReadAllText(hdrPath);
        var node = JsonNode.Parse(jsonText)!.AsObject();
        node.Remove("HashSize");
        File.WriteAllText(hdrPath, node.ToJsonString());

        using var store = GraphMetaStore.TryOpen(Repo, Fp, true, 10);
        Assert.NotNull(store);
        Assert.Equal(20, store!.HashSize);
        Assert.Equal(10, store.Count);
        Assert.Equal(rows[0].Id, store[0].Id);

        var probe = GraphMetaStore.Probe(Repo, Fp, true);
        Assert.True(probe.Ok);
        Assert.Equal(10, probe.RowCount);
    }

    [Fact]
    public void Older_header_with_explicit_zero_HashSize_opens_as_hash_size_20()
    {
        var w = new GraphMetaWriter(Repo, Fp, true);
        w.Reset();
        var rows = Enumerable.Range(0, 10).Select(i => Row1(i)).ToList();
        w.Append(rows, 0, 10);
        w.Commit(10, exhausted: true);

        var hdrPath = GraphCachePaths.File(Repo, GraphMetaStore.HdrExt);
        var jsonText = File.ReadAllText(hdrPath);
        var node = JsonNode.Parse(jsonText)!.AsObject();
        node["HashSize"] = 0;
        File.WriteAllText(hdrPath, node.ToJsonString());

        using var store = GraphMetaStore.TryOpen(Repo, Fp, true, 10);
        Assert.NotNull(store);
        Assert.Equal(20, store!.HashSize);
        Assert.Equal(10, store.Count);
    }

    [Fact]
    public void Append_mismatched_hash_size_to_sha1_store_throws_StoreInvalidException()
    {
        var w = new GraphMetaWriter(Repo, Fp, true);
        w.Reset();
        var sha1Rows = Enumerable.Range(0, 5).Select(i => Row1(i)).ToList();
        w.Append(sha1Rows, 0, 5);
        w.Commit(5, exhausted: false);

        var sha256Rows = Rows256(5);
        Assert.Throws<StoreInvalidException>(() => w.Append(sha256Rows, 0, 5));
    }

    [Fact]
    public void Append_mismatched_hash_size_to_sha256_store_throws_StoreInvalidException()
    {
        var w = new GraphMetaWriter(Repo, Fp, true);
        w.Reset();
        var sha256Rows = Rows256(5);
        w.Append(sha256Rows, 0, 5);
        w.Commit(5, exhausted: false);

        var sha1Rows = Enumerable.Range(0, 5).Select(i => Row1(i)).ToList();
        Assert.Throws<StoreInvalidException>(() => w.Append(sha1Rows, 0, 5));
    }

    [Fact]
    public void Append_mixed_hash_sizes_in_single_batch_throws_StoreInvalidException()
    {
        var w = new GraphMetaWriter(Repo, Fp, true);
        w.Reset();

        var mixedRows = new List<CommitMetadata> { Row1(0), Row256(1) };
        Assert.Throws<StoreInvalidException>(() => w.Append(mixedRows, 0, 2));
    }

    [Fact]
    public void Append_mismatched_parent_hash_size_throws_StoreInvalidException()
    {
        var w = new GraphMetaWriter(Repo, Fp, true);
        w.Reset();

        var m = Row256(1);
        IReadOnlyList<GitObjectId> badParents = [Oid1(1)];
        var badRaw = new List<RawCommit> { new(m.Id, badParents, m) };

        Assert.Throws<StoreInvalidException>(() => w.Append(badRaw, 0, 1));
    }

    [Fact]
    public void AppendSpillBatch_with_mismatched_hash_size_returns_conflict_when_not_from_zero()
    {
        var w = new GraphMetaWriter(Repo, Fp, true);
        w.Reset();
        var sha1Rows = Enumerable.Range(0, 5).Select(i => new RawCommit(Oid1(i), [], Row1(i))).ToList();
        w.Append(sha1Rows, 0, 5);
        w.Commit(5, exhausted: false);

        var sha256Rows = RawRows256(5);
        var result = w.AppendSpillBatch(sha256Rows, from: 5, to: 10, exhausted: true);
        Assert.Equal(SpillOutcome.Conflict, result.Outcome);
    }

    [Fact]
    public void WriteTips_with_mismatched_hash_size_throws_StoreInvalidException()
    {
        var w = new GraphMetaWriter(Repo, Fp, true);
        w.Reset();
        w.Append(Rows256(5), 0, 5);
        w.Commit(5, exhausted: true);

        var mixedTips = new[] { Oid256(1), Oid1(2) };
        Assert.Throws<StoreInvalidException>(() => w.WriteTips(mixedTips));
    }

    [Fact]
    public void Bare_CommitMetadata_append_zero_fills_sha256_parents()
    {
        var w = new GraphMetaWriter(Repo, Fp, true);
        w.Reset();
        var rows = Rows256(10);
        w.Append(rows, 0, 10);
        w.Commit(10, exhausted: true);

        using var store = GraphMetaStore.TryOpen(Repo, Fp, true, 10)!;
        var raw = store.ReadRaw(6);
        Assert.Equal(rows[6].ParentCount, raw.ParentIds.Count);
        Assert.All(raw.ParentIds, p =>
        {
            Assert.True(p.IsZero);
            Assert.Equal(32, p.HashSize);
        });
    }
}
