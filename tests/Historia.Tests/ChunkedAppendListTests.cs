namespace Historia.Tests;

/// <summary>
/// <see cref="ChunkedAppendList{T}"/> backs
/// <c>GraphLayoutState.Rows</c> so <c>DagLayoutEngine.Build</c> never has to copy the whole
/// accumulator into a fresh array on every Build/Extend call. These tests exercise it directly —
/// correctness across a chunk boundary, and the concurrent single-writer/many-reader contract its
/// own doc comment describes (a reader must never see a torn chunk or a stale count).
/// </summary>
[Trait("tier", "unit")]
public sealed class ChunkedAppendListTests
{
    [Fact]
    public void Sequential_appends_read_back_correctly_across_a_chunk_boundary()
    {
        var list = new ChunkedAppendList<int>();
        const int count = ChunkedAppendList<int>.ChunkSize * 2 + 137; // spans 3 chunks
        for (var i = 0; i < count; i++)
            list.Add(i * 2);

        Assert.Equal(count, list.Count);
        for (var i = 0; i < count; i++)
            Assert.Equal(i * 2, list[i]);
    }

    [Fact]
    public void Indexer_throws_out_of_range_past_count()
    {
        var list = new ChunkedAppendList<int>();
        list.Add(1);
        list.Add(2);

        Assert.Throws<ArgumentOutOfRangeException>(() => list[2]);
        Assert.Throws<ArgumentOutOfRangeException>(() => list[-1]);
    }

    [Fact]
    public void ToArray_matches_the_appended_sequence()
    {
        var list = new ChunkedAppendList<string>();
        var expected = Enumerable.Range(0, ChunkedAppendList<string>.ChunkSize + 5).Select(i => $"row-{i}").ToList();
        foreach (var s in expected)
            list.Add(s);

        Assert.Equal(expected, list.ToArray());
    }

    /// <summary>The type's own concurrency contract: a single writer appends while several reader
    /// threads, with NO lock, repeatedly read <see cref="ChunkedAppendList{T}.Count"/> and then
    /// index every row up to it — spanning several chunk-growth boundaries along the way. A
    /// reader observing <c>Count &gt; i</c> must always see the exact value the writer put at row
    /// <c>i</c>, never a default/zeroed struct or a value from a different row (a torn read).</summary>
    [Fact(Timeout = 30_000)]
    public async Task Concurrent_readers_never_see_a_torn_chunk_or_a_value_ahead_of_count()
    {
        var list = new ChunkedAppendList<GraphRow>();
        const int total = ChunkedAppendList<GraphRow>.ChunkSize * 3 + 500; // several chunk-growth boundaries
        var readerFailures = new ConcurrentBag<string>();
        using var cts = new CancellationTokenSource();

        GraphRow Row(int i) => new()
        {
            RowIndex = i,
            LaneIndex = i % 7,
            ColorIndex = i % 11,
            ParentCount = i % 3,
            Flags = RowFlags.None,
        };

        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            var rnd = new Random();
            while (!cts.IsCancellationRequested)
            {
                var count = list.Count;
                // Walk backwards from the just-observed count — the freshest rows are the most
                // likely to catch a publish-ordering bug, without re-checking every row every time.
                var start = Math.Max(0, count - 200);
                for (var i = start; i < count; i++)
                {
                    var row = list[i];
                    var expected = Row(i);
                    if (row.RowIndex != expected.RowIndex || row.LaneIndex != expected.LaneIndex
                        || row.ColorIndex != expected.ColorIndex || row.ParentCount != expected.ParentCount)
                    {
                        readerFailures.Add($"row {i}: expected {expected}, got {row}");
                        return;
                    }
                }
                if (rnd.Next(4) == 0)
                    Thread.Yield();
            }
        })).ToArray();

        for (var i = 0; i < total; i++)
            list.Add(Row(i));
        cts.Cancel();
        await Task.WhenAll(readers);

        Assert.Empty(readerFailures);
        Assert.Equal(total, list.Count);
        for (var i = 0; i < total; i++)
            Assert.Equal(Row(i), list[i]);
    }
}
