namespace Historia.Tests;

/// <summary>
/// the graph query controls. Path filter restricts the walk (a coherent, valid
/// topology); ref scope narrows the tips. Search is find-not-filter and lives in the control,
/// so it is not exercised here — this covers the layout-service side.
/// </summary>
[Trait("tier", "git")]
public class GraphQueryTests : IAsyncDisposable
{
    private readonly MiniHost _host = new();
    private readonly TestRepositoryBuilder _repo;

    public GraphQueryTests()
    {
        _repo = TestRepositoryBuilder.FromTemplate("graph-query-fixture", b =>
        {
            b.WriteFile("main.txt", "1\n").Stage();
            b.Commit("c1 on main");
            b.WriteFile("main.txt", "2\n").Stage();
            b.Commit("c2 on main");

            b.Checkout("feature", create: true);
            b.WriteFile("feature.txt", "f\n").Stage();
            b.Commit("c3 on feature");

            b.Checkout("main");
        });
    }

    [Fact]
    public async Task CurrentBranchScopeExcludesOtherBranchCommits()
    {
        await using var ctx = _host.Open(_repo.Path);
        var svc = new GraphLayoutService(_host.Settings, _host.Locator);

        var all = await svc.ComputeLayoutAsync(ctx, new GraphQuery { RefScope = GraphRefScope.AllRefs });
        svc.Forget(ctx);
        var current = await svc.ComputeLayoutAsync(ctx, new GraphQuery { RefScope = GraphRefScope.CurrentBranch });

        Assert.Equal(3, all.Layout.RowCount);      // c1, c2, c3
        Assert.Equal(2, current.Layout.RowCount);  // c1, c2 only
    }

    [Fact]
    public async Task PathFilterRestrictsTheWalkAndKeepsACoherentTopology()
    {
        await using var ctx = _host.Open(_repo.Path);
        var svc = new GraphLayoutService(_host.Settings, _host.Locator);

        var filtered = await svc.ComputeLayoutAsync(ctx, new GraphQuery
        {
            RefScope = GraphRefScope.AllRefs,
            PathFilter = "feature.txt",
        });

        // Only the one commit that touched feature.txt.
        Assert.Equal(1, filtered.Layout.RowCount);
        Assert.Contains("c3 on feature", filtered.Layout.Commits[0].MessageShort);
        // A coherent graph: every edge starts on a real row; a parent outside the filtered
        // walk becomes an OffWindow stub terminating at the window boundary, never a dangling
        // line to a nonexistent interior row.
        foreach (var e in filtered.Layout.Edges)
        {
            Assert.InRange(e.FromRow, 0, filtered.Layout.RowCount - 1);
            if (e.Type == EdgeType.OffWindow)
                Assert.InRange(e.ToRow, 0, filtered.Layout.RowCount);
            else
                Assert.InRange(e.ToRow, 0, filtered.Layout.RowCount - 1);
        }
    }

    [Fact]
    public async Task Quick_first_batch_then_extend_matches_a_single_full_layout()
    {
        // The control opens a repo with a tiny phase-1 batch, then ExtendLayoutAsync's to the
        // configured "Initial rows loaded". The result must equal one full layout.
        using var repo = RepoTemplates.Linear(30);
        await using var ctx = _host.Open(repo.Path);
        var svc = new GraphLayoutService(_host.Settings, _host.Locator);
        var query = new GraphQuery { RefScope = GraphRefScope.AllRefs };

        var full = await svc.ComputeLayoutAsync(ctx, query, maxRows: 20);
        // Forget disposes a disk-backed commit store, so read the ids while it is still open. A
        // disk-backed store can hold more rows than the layout has laid out; compare only those.
        var fullIds = full.Layout.Commits.Take(full.Layout.RowCount).Select(c => c.Id).ToList();
        svc.Forget(ctx);

        var fast = await svc.ComputeLayoutAsync(ctx, query, maxRows: 5);
        var extended = await svc.ExtendLayoutAsync(ctx, fast, additionalRows: 15);

        Assert.Equal(5, fast.Layout.RowCount);
        Assert.Equal(full.Layout.RowCount, extended.Layout.RowCount);
        Assert.Equal(full.Layout.Rows, extended.Layout.Rows);
        Assert.Equal(full.Layout.Edges, extended.Layout.Edges);
        Assert.Equal(fullIds, extended.Layout.Commits.Take(extended.Layout.RowCount).Select(c => c.Id));
    }

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
        _repo.Dispose();
    }
}
