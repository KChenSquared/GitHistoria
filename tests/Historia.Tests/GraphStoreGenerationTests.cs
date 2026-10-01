namespace Historia.Tests;

/// <summary>
/// A <see cref="DiskCommitStore"/> stays on the incarnation of the files it opened: a reset by
/// another writer must not change what it searches, grows into or decodes, and every way a record
/// can fail to parse reads as a stub instead of throwing.
/// </summary>
[Trait("tier", "unit")]
public sealed class GraphStoreGenerationTests : IDisposable
{
    private readonly StateDir _state = new();
    private const long KeyA = 0x1111_2222_3333_4444;
    private const long KeyB = 0x5555_6666_7777_8888;
    private const string Repo = @"C:\repos\store-generation-demo";

    public void Dispose() => _state.Dispose();

    private static GitObjectId Oid(int i) => new(i.ToString("x40"));

    private static RawCommit Commit(int i, string message) => new(
        Oid(i),
        i % 1000 == 0 ? [] : [Oid(i - 1)],
        new CommitMetadata
        {
            Id = Oid(i),
            MessageShort = message,
            AuthorName = "Ada Lovelace",
            AuthorEmail = "ada@example.com",
            AuthorWhen = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(i),
            CommitWhen = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(i),
            ParentCount = i % 1000 == 0 ? 0 : 1,
        });

    private static List<RawCommit> RowsA(int n) => [.. Enumerable.Range(0, n).Select(i => Commit(i, $"alpha {i}"))];

    private static List<RawCommit> RowsB(int n) =>
        [.. Enumerable.Range(1000, n).Select(i => Commit(i, $"beta with a much longer message {i}"))];

    private static void Spill(long key, List<RawCommit> rows, bool exhausted = true) =>
        Assert.Equal(SpillOutcome.Appended, new GraphMetaWriter(Repo, key, true).AppendSpillBatch(rows, 0, rows.Count, exhausted).Outcome);

    private static string HeaderGeneration()
    {
        var probe = GraphMetaStore.ProbeGeneration(Repo, includeRemoteBranches: true);
        Assert.True(probe.Ok);
        return probe.Generation;
    }

    [Fact]
    public void Search_on_a_store_opened_before_a_reset_finds_its_own_rows()
    {
        Spill(KeyA, RowsA(10));
        using var old = GraphMetaStore.TryOpen(Repo, KeyA, true, 10)!;

        Spill(KeyB, RowsB(10));

        Assert.Equal(10, old.Search("alpha").Count);
        Assert.Empty(old.Search("beta"));
    }

    [Fact]
    public void Search_reads_a_line_split_across_reads_and_multibyte_text()
    {
        var rows = Enumerable.Range(0, 4_000).Select(i => Commit(i, i % 500 == 0 ? $"naïve café {i} — ünïcode" : $"plain {i}")).ToList();
        Spill(KeyA, rows);
        using var store = GraphMetaStore.TryOpen(Repo, KeyA, true, rows.Count)!;

        Assert.Equal(Enumerable.Range(0, 8).Select(i => i * 500), store.Search("café"));
        Assert.Single(store.Search(Oid(3_999).ToString()));
    }

    [Fact]
    public void SetCount_after_a_reset_does_not_grow_from_the_new_files()
    {
        var rowsA = RowsA(10);
        Spill(KeyA, rowsA, exhausted: false);
        using var old = DiskCommitStore.Open(Repo, 5);

        Spill(KeyB, RowsB(10), exhausted: false);
        old.SetCount(10);

        Assert.True(old.Count >= 5);
        for (var r = 0; r < old.Count; r++)
            Assert.Equal(rowsA[r].Id, old.ReadRaw(r).Id);
    }

    private static long[] ReadIdx()
    {
        var bytes = File.ReadAllBytes(GraphCachePaths.File(Repo, GraphMetaStore.IdxExt));
        return [.. Enumerable.Range(0, bytes.Length / 8).Select(i => BitConverter.ToInt64(bytes, i * 8))];
    }

    private static void PatchFile(string ext, long position, byte[] bytes)
    {
        using var fs = new FileStream(GraphCachePaths.File(Repo, ext), FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        fs.Position = position;
        fs.Write(bytes);
    }

    [Fact]
    public void A_record_read_at_the_wrong_offset_reads_as_a_stub()
    {
        Spill(KeyA, RowsA(10));
        var offsets = ReadIdx();
        // Point record 3 at the last 10 bytes of its own record: too short to hold an id.
        PatchFile(GraphMetaStore.IdxExt, 3 * 8, BitConverter.GetBytes(offsets[4] - 10));
        using var store = GraphMetaStore.TryOpen(Repo, KeyA, true, 10)!;

        Assert.Equal(GitObjectId.Zero, store[3].Id);
        Assert.Equal(GitObjectId.Zero, store.ReadRaw(3).Id);
        Assert.True(store.ReadRawRange(0, 10).Count < 10);
        store.Prefetch(0, 9);
        Assert.Equal(Oid(2), store[2].Id);
    }

    [Fact]
    public void An_impossible_parent_count_is_invalid_data_not_an_allocation()
    {
        Spill(KeyA, RowsA(10));
        var offsets = ReadIdx();
        // sha (20) + three length-prefixed strings + two 10-byte timestamps precede the count.
        var countAt = offsets[3] + 20 + (1 + "alpha 3".Length) + (1 + "Ada Lovelace".Length) + (1 + "ada@example.com".Length) + 10 + 10;
        PatchFile(GraphMetaStore.DatExt, countAt, BitConverter.GetBytes(int.MaxValue));
        using var store = GraphMetaStore.TryOpen(Repo, KeyA, true, 10)!;

        Assert.Equal(GitObjectId.Zero, store[3].Id);
        Assert.Equal(GitObjectId.Zero, store.ReadRaw(3).Id);
        Assert.True(store.ReadRawRange(0, 10).Count < 10);
        Assert.Equal(Oid(4), store[4].Id);
    }

    [Fact]
    public async Task TryOpen_never_pairs_a_header_with_another_generations_files()
    {
        var aIds = RowsA(10).Select(c => c.Id).ToHashSet();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var writer = Task.Run(() =>
        {
            var flip = false;
            while (!stop.IsCancellationRequested)
            {
                if (flip) new GraphMetaWriter(Repo, KeyA, true).AppendSpillBatch(RowsA(10), 0, 10, exhausted: true);
                else new GraphMetaWriter(Repo, KeyB, true).AppendSpillBatch(RowsB(10), 0, 10, exhausted: true);
                flip = !flip;
            }
        });
        int opened = 0, torn = 0;
        while (!stop.IsCancellationRequested)
        {
            using var s = GraphMetaStore.TryOpen(Repo, KeyA, true, 1);
            if (s is null)
                continue;
            opened++;
            for (var r = 0; r < s.Count; r++)
                if (!aIds.Contains(s.ReadRaw(r).Id)) { torn++; break; }
        }
        await writer;

        Assert.Equal(0, torn);
    }

    [Fact]
    public void Reset_mints_a_generation_and_appends_keep_it()
    {
        var writer = new GraphMetaWriter(Repo, KeyA, true);
        var rows = RowsA(10);
        Assert.Equal(SpillOutcome.Appended, writer.AppendSpillBatch(rows.GetRange(0, 5), 0, 5, exhausted: false).Outcome);
        var first = HeaderGeneration();
        Assert.NotEmpty(first);
        Assert.Equal(first, writer.Generation);

        Assert.Equal(SpillOutcome.Appended, writer.AppendSpillBatch(rows.GetRange(5, 5), 5, 10, exhausted: true).Outcome);
        Assert.Equal(first, HeaderGeneration());

        // A second writer under the same key continues the same files.
        var other = new GraphMetaWriter(Repo, KeyA, true);
        Assert.Equal(SpillOutcome.Adopted, other.AppendSpillBatch(rows.GetRange(0, 5), 0, 5, exhausted: false).Outcome);
        Assert.Equal(first, other.Generation);

        // A different walk key starts over, under a new generation.
        Spill(KeyB, RowsB(10));
        var second = HeaderGeneration();
        Assert.NotEqual(first, second);

        // Returning to the first key resets again rather than resurrecting the old generation.
        Spill(KeyA, rows);
        Assert.NotEqual(first, HeaderGeneration());
        Assert.NotEqual(second, HeaderGeneration());
    }

    [Fact]
    public void A_store_records_the_generation_and_order_version_it_opened_with()
    {
        Spill(KeyA, RowsA(10));
        using var store = GraphMetaStore.TryOpen(Repo, KeyA, true, 10)!;

        Assert.Equal(HeaderGeneration(), store.Generation);
        Assert.Equal(0, store.OrderVersion);
    }

    [Fact]
    public void A_reorder_keeps_the_generation_and_bumps_the_order_version()
    {
        Spill(KeyA, RowsA(10));
        var generation = HeaderGeneration();
        var before = GraphMetaStore.ProbeExisting(Repo, includeRemoteBranches: true);

        var reorder = new GraphMetaWriter(Repo, KeyB, true);
        Assert.True(reorder.TryCommitReorder(
            before.WalkKey, before.Exhausted, before.OrderVersion, before.RecordCount,
            newRecordCount: 0, order: [.. Enumerable.Range(0, 10).Reverse()], sortedTips: [Oid(9)]));

        var probe = GraphMetaStore.ProbeGeneration(Repo, includeRemoteBranches: true);
        Assert.True(probe.Ok);
        Assert.Equal(KeyB, probe.WalkKey);
        Assert.Equal(generation, probe.Generation);
        Assert.Equal(1, probe.OrderVersion);

        using var store = GraphMetaStore.TryOpen(Repo, KeyB, true, 10)!;
        Assert.Equal(generation, store.Generation);
        Assert.Equal(1, store.OrderVersion);
        Assert.Equal(Oid(9), store.ReadRaw(0).Id);
    }

    [Fact]
    public void A_header_without_a_generation_still_opens()
    {
        Spill(KeyA, RowsA(10));
        var hdrPath = GraphCachePaths.File(Repo, GraphMetaStore.HdrExt);
        var node = JsonNode.Parse(File.ReadAllBytes(hdrPath))!.AsObject();
        Assert.True(node.Remove("Generation"));
        File.WriteAllBytes(hdrPath, Encoding.UTF8.GetBytes(node.ToJsonString()));

        using var store = GraphMetaStore.TryOpen(Repo, KeyA, true, 10);

        Assert.NotNull(store);
        Assert.Equal("", store.Generation);
        Assert.Equal(Oid(4), store.ReadRaw(4).Id);
        Assert.Single(store.Search("alpha 4"));
    }
}
