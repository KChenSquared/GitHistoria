namespace Historia.Tests;

/// <summary>
/// / R4, R5: pins that a leased <see cref="ICommitStore"/> (the painted graph's lease)
/// keeps reading correct, uncorrupted rows even while the on-disk cache is rewritten underneath
/// it — either by a from-row-0 re-spill at a new walk key (which calls <c>Reset</c>) or by an
/// append-only reorder commit.
/// </summary>
[Trait("tier", "unit")]
public sealed class GraphStoreLiveReaderTests : IDisposable
{
    private readonly StateDir _state = new();
    private const long KeyA = 0x1111_2222_3333_4444;
    private const long KeyB = 0x5555_6666_7777_8888;
    private const string Repo = @"C:\repos\live-reader-demo";

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

    [Fact]
    public void A_leased_store_keeps_reading_its_rows_after_a_from_row_0_respill()
    {
        var rowsA = Enumerable.Range(0, 10).Select(i => Commit(i, i == 0 ? [] : [i - 1])).ToList();
        var writerA = new GraphMetaWriter(Repo, KeyA, true);
        var outcomeA = writerA.AppendSpillBatch(rowsA, 0, 10, exhausted: true);
        Assert.Equal(SpillOutcome.Appended, outcomeA.Outcome);

        var storeA = GraphMetaStore.TryOpen(Repo, KeyA, true, 10)!;
        Assert.NotNull(storeA);
        var paintedLease = storeA.AddRef()!;
        storeA.Dispose(); // opener drops its lease; paintedLease keeps store open

        // Spill 10 DIFFERENT rows under walk key B from row 0 (which calls Reset())
        var rowsB = Enumerable.Range(100, 10).Select(i => Commit(i, i == 100 ? [] : [i - 1])).ToList();
        var writerB = new GraphMetaWriter(Repo, KeyB, true);
        var outcomeB = writerB.AppendSpillBatch(rowsB, 0, 10, exhausted: true);
        Assert.Equal(SpillOutcome.Appended, outcomeB.Outcome);

        // Old store still reads its original rows through the painted lease
        Assert.Equal(10, paintedLease.Count);
        for (var i = 0; i < 10; i++)
        {
            var meta = paintedLease[i];
            Assert.Equal(rowsA[i].Id, meta.Id);
            Assert.Equal(rowsA[i].Metadata.MessageShort, meta.MessageShort);
        }

        // Store freshly opened at key B reads the new rows
        using (var storeB = GraphMetaStore.TryOpen(Repo, KeyB, true, 10)!)
        {
            Assert.NotNull(storeB);
            Assert.Equal(10, storeB.Count);
            for (var i = 0; i < 10; i++)
            {
                var meta = storeB[i];
                Assert.Equal(rowsB[i].Id, meta.Id);
                Assert.Equal(rowsB[i].Metadata.MessageShort, meta.MessageShort);
            }
        }

        // Dispose the old painted lease
        paintedLease.Dispose();

        // Re-check the new store opened at key B
        using (var storeB2 = GraphMetaStore.TryOpen(Repo, KeyB, true, 10)!)
        {
            Assert.NotNull(storeB2);
            Assert.Equal(10, storeB2.Count);
            Assert.Equal(rowsB[0].Id, storeB2[0].Id);
        }
    }

    [Fact]
    public void A_leased_store_keeps_reading_its_rows_after_a_reorder_commits()
    {
        var rows = Enumerable.Range(0, 5).Select(i => Commit(i, i == 0 ? [] : [i - 1])).ToList();
        var writerA = new GraphMetaWriter(Repo, KeyA, true);
        var outcomeA = writerA.AppendSpillBatch(rows, 0, 5, exhausted: true);
        Assert.Equal(SpillOutcome.Appended, outcomeA.Outcome);

        var storeA = GraphMetaStore.TryOpen(Repo, KeyA, true, 5)!;
        Assert.NotNull(storeA);
        var paintedLease = storeA.AddRef()!;
        storeA.Dispose();

        // Commit a reorder under KeyB: reverse the 5 commits plus add commit 5 at the top
        var newWalk = new List<RawCommit> { Commit(5, 4) }
            .Concat(Enumerable.Range(0, 5).Reverse().Select(i => Commit(i, i == 0 ? [] : [i - 1])))
            .ToList();
        var existing = storeA.BuildShaIndex();
        var plan = GraphMetaReorder.Build(existing, storeA.RecordCount, newWalk);

        var writerB = new GraphMetaWriter(Repo, KeyB, true);
        writerB.Append(plan.NewRecords, 0, plan.NewRecords.Count);
        Assert.True(writerB.WriteOrder(1, plan.Order));
        writerB.WriteTips([Oid(5)]);
        writerB.CommitReorder(rowCount: plan.Order.Length, recordCount: storeA.RecordCount + plan.NewRecords.Count, orderVersion: 1, exhausted: true);

        // Old store still returns original identity order and metadata
        Assert.Equal(5, paintedLease.Count);
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(Oid(i), paintedLease[i].Id);
            Assert.Equal($"commit {i}", paintedLease[i].MessageShort);
        }

        // Store opened at KeyB reads the new reordered rows
        using (var storeB = GraphMetaStore.TryOpen(Repo, KeyB, true, 6)!)
        {
            Assert.NotNull(storeB);
            Assert.Equal(6, storeB.Count);
            Assert.Equal(Oid(5), storeB[0].Id);
            Assert.Equal(Oid(4), storeB[1].Id);
            Assert.Equal(Oid(0), storeB[5].Id);
        }

        paintedLease.Dispose();
    }
}
