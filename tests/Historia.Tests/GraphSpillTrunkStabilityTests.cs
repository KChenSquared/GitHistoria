namespace Historia.Tests;

/// <summary>with "Read commit details from disk" on, the first layout spills
/// per-commit metadata and stubs the resident copy. That stub must keep <c>CommitWhen</c> — the
/// tip-priority pre-pass sorts candidate tips by it, and zeroing it would make re-layouts after
/// the first spill sort tips by SHA and pin the trunk to a random lane.</summary>
[Trait("tier", "git")]
public sealed class GraphSpillTrunkStabilityTests : IAsyncDisposable
{
    private readonly MiniHost _host = new();
    private readonly TestRepositoryBuilder _repo;

    public GraphSpillTrunkStabilityTests()
    {
        _repo = TestRepositoryBuilder.FromTemplate("graph-spill-trunk-stability", repo =>
        {
            // main gets the bulk of history and the NEWEST tip; several feature branches fork off it
            // at earlier points, each with an older tip. SeedTipPriority must keep main (newest,
            // and checked out) on lane 0 — both on the first compute and on every re-layout after
            // the spill stubs the resident metadata. Each commit is a minute after the last, so the
            // tips' order never depends on how many commits fit in one wall-clock second.
            var when = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
            string Commit(string message)
            {
                when = when.AddMinutes(1);
                return repo.Stage().CommitAt(message, when);
            }

            void Trunk(int from, int to)
            {
                for (var i = from; i < to; i++)
                {
                    repo.WriteFile("f.txt", $"{i}\n");
                    Commit($"main {i:D3}");
                }
            }

            Trunk(0, 6);
            for (var b = 0; b < 6; b++)
            {
                repo.Git("checkout", "-b", $"feat-{b}");
                repo.WriteFile($"feat{b}.txt", "x");
                Commit($"feat {b} tip");
                repo.Git("checkout", "main");
                Trunk(6 + b * 4, 10 + b * 4);
            }
            // main's final (newest) commit
            repo.WriteFile("f.txt", "final\n");
            Commit("main HEAD newest");
        });
    }

    public async ValueTask DisposeAsync()
    {
        _repo.Dispose();
        await _host.DisposeAsync();
    }

    private static GraphQuery Q => new() { RefScope = GraphRefScope.AllRefs };

    private static int[] Lanes(GraphLayoutResult r, int n) =>
        Enumerable.Range(0, Math.Min(n, r.Layout.RowCount)).Select(i => r.Layout.Rows[i].LaneIndex).ToArray();

    [Fact]
    public async Task Trunk_lane_survives_the_metadata_spill()
    {
        await _host.Settings.UpdateAsync(s => s with { GraphMetadataFromDisk = true });
        var svc = new GraphLayoutService(_host.Settings, _host.Locator);
        await using var ctx = _host.Open(_repo.Path);
        ctx.MarkFirstGraphPaintDone(); ctx.MarkFirstFullLoadDone(); // no graph control / workspace here — stand in for the load signals

        // First compute: real CommitWhen resident → main (newest, checked out) takes lane 0.
        var first = await svc.ComputeLayoutAsync(ctx, Q);
        Assert.Equal(0, first.Layout.Rows[0].LaneIndex);
        Assert.Contains(first.Layout.Decorations, d => d.RowIndex == 0 && d.Label == "main");

        // SpillAndSwap has now stubbed the resident metadata. A re-layout (tab switch / rebind)
        // must produce the identical trunk — not a SHA-ordered one.
        var second = await svc.ComputeLayoutAsync(ctx, Q);
        Assert.Equal(0, second.Layout.Rows[0].LaneIndex);
        Assert.Contains(second.Layout.Decorations, d => d.RowIndex == 0 && d.Label == "main");

        Assert.Equal(Lanes(first, 40), Lanes(second, 40));

        // And the quick(200)+Extend path the graph control actually uses.
        var quick = await svc.ComputeLayoutAsync(ctx, Q, maxRows: 3);
        var extended = await svc.ExtendLayoutAsync(ctx, quick, additionalRows: 200);
        Assert.Equal(0, extended.Layout.Rows[0].LaneIndex);
        Assert.Equal(Lanes(first, 40), Lanes(extended, 40));
    }

    [Fact]
    public async Task Warm_reopen_from_the_disk_walk_cache_also_keeps_the_trunk()
    {
        await _host.Settings.UpdateAsync(s => s with { GraphMetadataFromDisk = true });
        await using var ctx = _host.Open(_repo.Path);
        ctx.MarkFirstGraphPaintDone(); ctx.MarkFirstFullLoadDone(); // no graph control / workspace here — stand in for the load signals

        // Session 1 walks, lays out, and persists the walk + spilled metadata to disk.
        var s1 = new GraphLayoutService(_host.Settings, _host.Locator);
        var warmUp = await s1.ComputeLayoutAsync(ctx, Q);
        var expected = Lanes(warmUp, 40);
        Assert.Equal(0, warmUp.Layout.Rows[0].LaneIndex);
        // give the background spill a moment to finish writing the whole history
        await Task.Delay(1500);

        // Session 2 (fresh service = cold launch): loads id/parents-only from the walk cache.
        var s2 = new GraphLayoutService(_host.Settings, _host.Locator);
        var reopened = await s2.ComputeLayoutAsync(ctx, Q);

        Assert.Equal(0, reopened.Layout.Rows[0].LaneIndex);
        Assert.Equal(expected, Lanes(reopened, 40));
    }
}
