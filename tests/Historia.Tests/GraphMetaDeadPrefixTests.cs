namespace Historia.Tests;

/// <summary>
/// / R5, R7: tests that <see cref="GraphMetaWriter.Reset"/> retires existing cache files
/// instead of appending after them, leaving no dead prefix in <c>.meta.dat</c> and reporting
/// <see cref="SpillOutcome.Conflict"/> if retirement is blocked by an unshared foreign handle.
/// </summary>
[Trait("tier", "unit")]
public sealed class GraphMetaDeadPrefixTests : IDisposable
{
    private readonly StateDir _state = new();
    private const long KeyA = 0x1111_2222_3333_4444;
    private const long KeyB = 0x5555_6666_7777_8888;
    private const string Repo = @"C:\repos\dead-prefix-demo";

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
    public void A_respill_under_a_live_reader_leaves_no_dead_prefix()
    {
        var rowsA = Enumerable.Range(0, 10).Select(i => Commit(i, i == 0 ? [] : [i - 1])).ToList();
        var writerA = new GraphMetaWriter(Repo, KeyA, true);
        var outcomeA = writerA.AppendSpillBatch(rowsA, 0, 10, exhausted: true);
        Assert.Equal(SpillOutcome.Appended, outcomeA.Outcome);

        var storeA = GraphMetaStore.TryOpen(Repo, KeyA, true, 10)!;
        Assert.NotNull(storeA);
        var paintedLease = storeA.AddRef()!;
        storeA.Dispose();

        var rowsB = Enumerable.Range(100, 15).Select(i => Commit(i, i == 100 ? [] : [i - 1])).ToList();
        var writerB = new GraphMetaWriter(Repo, KeyB, true);
        var outcomeB = writerB.AppendSpillBatch(rowsB, 0, 15, exhausted: true);
        Assert.Equal(SpillOutcome.Appended, outcomeB.Outcome);

        var prefix = GraphMetaStore.ProbeDeadPrefix(Repo);
        Assert.Equal(0, prefix.DeadBytes);
        Assert.True(prefix.DatLength > 0);

        Assert.Equal(10, paintedLease.Count);
        for (var i = 0; i < 10; i++)
            Assert.Equal(rowsA[i].Id, paintedLease[i].Id);

        using (var storeB = GraphMetaStore.TryOpen(Repo, KeyB, true, 15)!)
        {
            Assert.NotNull(storeB);
            Assert.Equal(15, storeB.Count);
            Assert.Equal(rowsB[0].Id, storeB[0].Id);
        }

        paintedLease.Dispose();

        var stem = GraphCachePaths.Stem(Repo);
        var retiredFiles = Directory.GetFiles(AppPaths.CacheDirectory, $"{stem}.meta.*.retired");
        Assert.Empty(retiredFiles);
    }

    [Fact]
    public void A_reset_blocked_by_a_foreign_handle_reports_conflict_and_leaves_the_old_store_intact()
    {
        if (!OperatingSystem.IsWindows())
            return; // On Unix platforms, open files can always be renamed and unlinked regardless of sharing flags.

        var rowsA = Enumerable.Range(0, 5).Select(i => Commit(i, i == 0 ? [] : [i - 1])).ToList();
        var writerA = new GraphMetaWriter(Repo, KeyA, true);
        writerA.AppendSpillBatch(rowsA, 0, 5, exhausted: true);

        var idxPath = GraphCachePaths.File(Repo, GraphMetaStore.IdxExt);
        var idxBytesBefore = File.ReadAllBytes(idxPath);

        // Hold .meta.idx open with plain FileShare.Read (no FileShare.Delete), simulating a foreign handle (indexer, AV, etc.)
        using (var foreignHandle = new FileStream(idxPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var rowsB = Enumerable.Range(10, 5).Select(i => Commit(i, i == 10 ? [] : [i - 1])).ToList();
            var writerB = new GraphMetaWriter(Repo, KeyB, true);
            var outcome = writerB.AppendSpillBatch(rowsB, 0, 5, exhausted: true);

            Assert.Equal(SpillOutcome.Conflict, outcome.Outcome);
        }

        // Header still names KeyA
        var probe = GraphMetaStore.ProbeExisting(Repo, true);
        Assert.True(probe.Ok);
        Assert.Equal(KeyA, probe.WalkKey);

        // .meta.idx bytes are unchanged
        var idxBytesAfter = File.ReadAllBytes(idxPath);
        Assert.Equal(idxBytesBefore, idxBytesAfter);
    }

    [Fact]
    public void Reset_sweeps_leftover_retired_files_for_its_own_stem_only()
    {
        Directory.CreateDirectory(AppPaths.CacheDirectory);
        var stemThis = GraphCachePaths.Stem(Repo);
        const string OtherRepo = @"C:\repos\other-repo";
        var stemOther = GraphCachePaths.Stem(OtherRepo);

        var retiredThis = Path.Combine(AppPaths.CacheDirectory, $"{stemThis}.meta.dat.{Guid.NewGuid():N}.retired");
        var retiredOther = Path.Combine(AppPaths.CacheDirectory, $"{stemOther}.meta.dat.{Guid.NewGuid():N}.retired");

        File.WriteAllBytes(retiredThis, [1, 2, 3]);
        File.WriteAllBytes(retiredOther, [4, 5, 6]);

        var rows = Enumerable.Range(0, 5).Select(i => Commit(i, i == 0 ? [] : [i - 1])).ToList();
        var writer = new GraphMetaWriter(Repo, KeyA, true);
        writer.AppendSpillBatch(rows, 0, 5, exhausted: true);

        Assert.False(File.Exists(retiredThis), "Reset must sweep leftover .retired files for its own stem");
        Assert.True(File.Exists(retiredOther), "Reset must NOT sweep leftover .retired files belonging to other stems");
    }

    [Fact]
    public void ProbeDeadPrefix_reports_the_bytes_before_record_0()
    {
        Assert.Equal((0, 0), GraphMetaStore.ProbeDeadPrefix(Repo));

        // Create .dat with K junk bytes
        const int K = 512;
        var datPath = GraphCachePaths.File(Repo, GraphMetaStore.DatExt);
        Directory.CreateDirectory(AppPaths.CacheDirectory);
        File.WriteAllBytes(datPath, new byte[K]);

        // Append rows through writer WITHOUT calling Reset:
        var w = new GraphMetaWriter(Repo, KeyA, true);
        var rows = Enumerable.Range(0, 5).Select(i => Commit(i, i == 0 ? [] : [i - 1])).ToList();
        w.Append(rows, 0, 5);
        w.Commit(5, exhausted: true);

        var prefix = GraphMetaStore.ProbeDeadPrefix(Repo);
        Assert.Equal(K, prefix.DeadBytes);
        Assert.True(prefix.DatLength > K);

        // A fresh store (created with Reset) reports 0
        w.Reset();
        w.Append(rows, 0, 5);
        w.Commit(5, exhausted: true);

        var freshPrefix = GraphMetaStore.ProbeDeadPrefix(Repo);
        Assert.Equal(0, freshPrefix.DeadBytes);
    }
}
