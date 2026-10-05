namespace Historia.Tests;

/// <summary>
/// <c>DagLayoutEngine.Build</c> hands <see
/// cref="GraphLayout.RowByCommitId"/> the accumulator's own, still-growing dictionary instead of
/// a per-snapshot copy. <c>CommitGraphControl.ResolveRow</c> / <c>FindSubject</c> read it from
/// the render thread, while a background <c>ExtendLayoutAsync</c> inserts
/// into that very same dictionary under <c>lock (existing.State)</c>. An unsynchronized
/// <see cref="Dictionary{TKey,TValue}"/> read concurrent with an insert that resizes it is
/// unsupported: it can return a wrong row or throw on the UI thread. These tests exercise the
/// reader-side guard — <see cref="GraphLayout.TryGetRow"/> and the wrapped <see
/// cref="GraphLayout.RowByCommitId"/> property both lock on <c>SourceState</c> and bound the
/// result to the snapshot's own <c>RowCount</c> — the same way a real extend is locked
/// (<c>GraphLayoutService.ExtendLayoutAsync</c>).
/// </summary>
[Trait("tier", "unit")]
public class GraphLayoutRowLookupTests
{
    private readonly DagLayoutEngine _engine = new();

    private static GitObjectId Sha(int n) => GitObjectId.Parse(n.ToString("x40"));

    private static CommitMetadata Meta(int n, int parentCount) => new()
    {
        Id = Sha(n),
        MessageShort = $"commit {n}",
        AuthorName = "A",
        AuthorEmail = "a@x",
        AuthorWhen = DateTimeOffset.UnixEpoch.AddMinutes(n),
        CommitWhen = DateTimeOffset.UnixEpoch.AddMinutes(n),
        ParentCount = parentCount,
    };

    /// <summary>A straight-line chain: row 0 is the tip, row <c>count-1</c> is the root —
    /// children before parents, as the engine requires.</summary>
    private static List<RawCommit> Chain(int count)
    {
        var list = new List<RawCommit>(count);
        for (var n = 0; n < count; n++)
        {
            IReadOnlyList<GitObjectId> parents = n + 1 < count
                ? [Sha(n + 1)]
                : [];
            list.Add(new RawCommit(Sha(n), parents, Meta(n, parents.Count)));
        }
        return list;
    }

    [Fact]
    public async Task Concurrent_extend_and_row_lookups_never_throw_and_stay_in_bounds()
    {
        const int total = 5_000;
        const int firstBatch = 100;
        const int batchSize = 100;
        var chain = Chain(total);

        var initial = _engine.Compute(chain, null, maxRows: firstBatch);
        var state = initial.State;
        // Fixed, never-reassigned snapshot: its RowCount stays firstBatch for the whole test even
        // though `state`'s own dictionary keeps growing underneath it via the writer below.
        var oldest = initial.Layout;

        Exception? readerFailure = null;
        Exception? writerFailure = null;
        var stop = false;

        var writer = Task.Run(() =>
        {
            try
            {
                var current = initial;
                var consumed = firstBatch;
                while (consumed < total)
                {
                    var take = Math.Min(batchSize, total - consumed);
                    // The same lock GraphLayoutService.ExtendLayoutAsync takes around a mutating
                    // Extend call — the reader-side lock has nothing to mediate against
                    // without it.
                    lock (state)
                    {
                        var next = chain.GetRange(consumed, chain.Count - consumed);
                        current = _engine.Extend(current, next, null, take, CancellationToken.None);
                    }
                    consumed += take;
                }
            }
            catch (Exception ex)
            {
                writerFailure = ex;
            }
            finally
            {
                stop = true;
            }
        });

        var reader = Task.Run(() =>
        {
            try
            {
                var probeIds = Enumerable.Range(0, total).Select(Sha).ToArray();
                var i = 0;
                while (!stop)
                {
                    var id = probeIds[i % probeIds.Length];
                    i++;

                    if (oldest.RowByCommitId.TryGetValue(id, out var viaDict))
                        Assert.True(viaDict < oldest.RowCount);

                    if (oldest.TryGetRow(id, out var viaMethod))
                        Assert.True(viaMethod < oldest.RowCount);
                }
            }
            catch (Exception ex)
            {
                readerFailure = ex;
            }
        });

        await Task.WhenAll(writer, reader);

        Assert.Null(writerFailure);
        Assert.Null(readerFailure);
    }

    [Fact]
    public void An_older_snapshot_ignores_rows_a_later_extend_appended()
    {
        var chain = Chain(20);
        var first = _engine.Compute(chain, null, maxRows: 5);
        var second = _engine.Extend(first, chain.GetRange(5, 15), null, additionalRows: 5);

        // The commit at row 7 now exists in the shared state's dictionary...
        Assert.True(second.Layout.TryGetRow(Sha(7), out var row7));
        Assert.Equal(7, row7);
        Assert.True(second.Layout.RowByCommitId.TryGetValue(Sha(7), out var row7ViaDict));
        Assert.Equal(7, row7ViaDict);

        // ...but the older snapshot must still refuse it: it is past `first`'s own RowCount (5),
        // even though `first.Layout.RowByCommitId` is the very same shared dictionary instance
        // `second` just read from.
        Assert.False(first.Layout.TryGetRow(Sha(7), out var missRow));
        Assert.Equal(-1, missRow);
        Assert.False(first.Layout.RowByCommitId.TryGetValue(Sha(7), out _));

        // A row inside the bound still resolves through the old snapshot.
        Assert.True(first.Layout.TryGetRow(Sha(2), out var row2));
        Assert.Equal(2, row2);
    }

    [Fact]
    public void TryGetRow_agrees_with_the_RowByCommitId_property_for_a_miss()
    {
        var chain = Chain(5);
        var layout = _engine.Compute(chain, null, maxRows: 5).Layout;

        Assert.False(layout.TryGetRow(Sha(999), out var viaMethod));
        Assert.Equal(-1, viaMethod);
        Assert.False(layout.RowByCommitId.TryGetValue(Sha(999), out var viaDict));
        Assert.Equal(-1, viaDict);
    }

    [Fact]
    public void Empty_layout_never_resolves_a_row()
    {
        Assert.False(GraphLayout.Empty.TryGetRow(Sha(0), out _));
        Assert.False(GraphLayout.Empty.RowByCommitId.TryGetValue(Sha(0), out _));
        Assert.Empty(GraphLayout.Empty.RowByCommitId);
    }
}
