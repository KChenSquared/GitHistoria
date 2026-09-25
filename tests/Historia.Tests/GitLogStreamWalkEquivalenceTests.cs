namespace Historia.Tests;

/// <summary>
/// The graph walk (<see cref="GitLogStream"/>, via <see cref="GraphLayoutService"/>) yields exactly
/// the commits reachable from every ref — checked against an independent LibGit2Sharp revwalk
/// computed inside the test — resumes across extends without gaps or dupes, and emits rows in
/// strictly descending commit-date order when timestamps are distinct. Skipped when git.exe isn't
/// discoverable by <see cref="IGitExecutableLocator"/>.
/// </summary>
[Trait("tier", "git")]
public sealed class GitLogStreamWalkEquivalenceTests : IAsyncDisposable
{
    private readonly MiniHost _host = new();
    private readonly TestRepositoryBuilder _repo;

    public GitLogStreamWalkEquivalenceTests()
    {
        _repo = TestRepositoryBuilder.FromTemplate("git-log-walk-equiv", b =>
        {
            b.WriteFile("root.txt", "unrelated\n").Stage().Commit("unrelated root");

            // A second, wholly unrelated root — only reachable via its own orphan branch, like a
            // grafted-in history. Exercises the tip peeling / multi-root walk on both engines.
            b.Git("checkout", "--orphan", "unrelated");
            b.Git("rm", "-rf", ".");
            b.WriteFile("other.txt", "x\n").Stage().Commit("second root");
            b.Git("checkout", "main");

            for (var i = 0; i < 5; i++)
                b.WriteFile("f.txt", $"{i}\n").Stage().Commit($"main {i}");

            b.Git("checkout", "-b", "feature");
            b.WriteFile("feature.txt", "x\n").Stage().Commit("feature work");
            b.Git("checkout", "main");
            b.WriteFile("f2.txt", "y\n").Stage().Commit("main after fork");
            b.Git("merge", "--no-ff", "-m", "merge feature", "feature");
            b.Git("tag", "v1.0");
        });
    }

    public async ValueTask DisposeAsync()
    {
        _repo.Dispose();
        await _host.DisposeAsync();
    }

    private static GraphQuery Q => new() { RefScope = GraphRefScope.AllRefs };

    private static List<ObjectId> RowIds(GraphLayoutResult r) =>
        Enumerable.Range(0, r.Layout.RowCount).Select(i => r.Layout.Commits[i].Id).ToList();

    /// <summary>Ground-truth check independent of the walk: for every row, every real parent of
    /// that row's commit (when also present in the walked window) must sort at a LATER row —
    /// children-before-parents.</summary>
    private static async Task AssertChildrenBeforeParents(RepositoryContext ctx, GraphLayoutResult result)
    {
        var rowOf = new Dictionary<ObjectId, int>();
        var ids = RowIds(result);
        for (var i = 0; i < ids.Count; i++)
            rowOf[ids[i]] = i;

        await ctx.WithRepositoryAsync(repo =>
        {
            for (var i = 0; i < ids.Count; i++)
            {
                var commit = repo.Lookup<Commit>(ids[i]);
                if (commit is null)
                    continue;
                foreach (var parent in commit.Parents)
                {
                    if (rowOf.TryGetValue(parent.Id, out var parentRow))
                        Assert.True(parentRow > i, $"parent {parent.Id} of {ids[i]} must sort after its child (row {parentRow} vs {i})");
                }
            }
            return true;
        });
    }

    /// <summary>Test oracle: every commit reachable from HEAD and every ref, by LibGit2Sharp's
    /// own revwalk (which peels annotated tags itself). Independent of the graph service's tip
    /// resolution.</summary>
    private static Task<HashSet<ObjectId>> ReachableFromAllRefs(RepositoryContext ctx) =>
        ctx.WithRepositoryAsync(repo =>
        {
            var tips = new List<object> { repo.Head };
            tips.AddRange(repo.Refs);
            return repo.Commits.QueryBy(new CommitFilter { IncludeReachableFrom = tips })
                .Select(c => c.Id)
                .ToHashSet();
        });

    [Fact]
    public async Task Walk_yields_exactly_the_commits_reachable_from_every_ref()
    {
        var locator = _host.Provider.GetRequiredService<IGitExecutableLocator>();
        if (!locator.Current.Found)
            return; // git.exe not discoverable (see class doc)

        await using var ctx = _host.Open(_repo.Path);
        ctx.MarkFirstGraphPaintDone(); ctx.MarkFirstFullLoadDone();
        var result = await new GraphLayoutService(_host.Settings, locator).ComputeLayoutAsync(ctx, Q, maxRows: 1000);

        var ids = new HashSet<ObjectId>(RowIds(result));

        Assert.NotEmpty(ids);
        Assert.Equal(await ReachableFromAllRefs(ctx), ids);
        // Sanity: both roots and the merge are actually in the walked set.
        Assert.Equal(10, ids.Count);

        await AssertChildrenBeforeParents(ctx, result);
    }

    [Fact]
    public async Task Cli_walk_resuming_across_several_extends_yields_no_dupes_or_gaps()
    {
        var locator = _host.Provider.GetRequiredService<IGitExecutableLocator>();
        if (!locator.Current.Found)
            return;

        await using var oracle = _host.Open(_repo.Path);
        oracle.MarkFirstGraphPaintDone(); oracle.MarkFirstFullLoadDone();
        var fullIds = new HashSet<ObjectId>(RowIds(
            await new GraphLayoutService(_host.Settings, locator).ComputeLayoutAsync(oracle, Q, maxRows: 1000)));

        await using var ctx = _host.Open(_repo.Path);
        ctx.MarkFirstGraphPaintDone(); ctx.MarkFirstFullLoadDone();
        var svc = new GraphLayoutService(_host.Settings, locator);

        var current = await svc.ComputeLayoutAsync(ctx, Q, maxRows: 2);
        var seen = RowIds(current);

        for (var i = 0; i < fullIds.Count + 5 && !current.Layout.IsComplete; i++)
            current = await svc.ExtendLayoutAsync(ctx, current, additionalRows: 2);

        seen = RowIds(current);

        Assert.Equal(fullIds.Count, seen.Count);           // no gaps — every commit eventually surfaced
        Assert.Equal(seen.Count, seen.Distinct().Count());  // no dupes across the resumed pulls
        Assert.Equal(fullIds, new HashSet<ObjectId>(seen)); // same set as one big walk
    }

    /// <summary>An annotated tag's ref target is the TAG OBJECT, not the commit it points at, so
    /// <c>GraphLayoutService.ResolveTipShas</c> must peel it — a plain
    /// <c>repo.Lookup&lt;Commit&gt;(target)</c> returns null and would silently drop the tip. This
    /// fixture is a release tagged on a branch that is then deleted, so the annotated tag is the
    /// ONLY remaining path to that history.</summary>
    [Fact]
    public async Task Walk_includes_a_commit_reachable_only_via_an_annotated_tag_on_a_deleted_branch()
    {
        var locator = _host.Provider.GetRequiredService<IGitExecutableLocator>();
        if (!locator.Current.Found)
            return; // git.exe not discoverable (see class doc)

        _repo.Git("checkout", "-b", "doomed");
        var doomedSha = _repo.WriteFile("doomed.txt", "x\n").Stage().Commit("doomed work");
        _repo.Git("tag", "-a", "release-1.0", "-m", "release 1.0");
        _repo.Git("checkout", "main");
        _repo.Git("branch", "-D", "doomed");

        await using var ctx = _host.Open(_repo.Path);
        ctx.MarkFirstGraphPaintDone(); ctx.MarkFirstFullLoadDone();
        var result = await new GraphLayoutService(_host.Settings, locator).ComputeLayoutAsync(ctx, Q, maxRows: 1000);

        var ids = new HashSet<ObjectId>(RowIds(result));

        Assert.Equal(await ReachableFromAllRefs(ctx), ids);
        // The doomed-branch commit must actually be present — the annotated tag is its only
        // remaining path now that the branch itself is deleted.
        Assert.Contains(new ObjectId(doomedSha), ids);
    }

    [Fact]
    public async Task Walk_rows_descend_by_commit_date_when_timestamps_are_distinct()
    {
        var locator = _host.Provider.GetRequiredService<IGitExecutableLocator>();
        if (!locator.Current.Found)
            return;

        using var repo = TestRepositoryBuilder.FromTemplate("graph-interleaved-branches", b =>
        {
            var t0 = DateTimeOffset.Parse("2026-09-15T12:00:00Z");
            b.WriteFile("a.txt", "a").Stage().CommitAt("A", t0.AddMinutes(1));
            b.WriteFile("m1.txt", "m1").Stage().CommitAt("M1", t0.AddMinutes(2));

            b.Checkout("feature", create: true);
            b.WriteFile("f1.txt", "f1").Stage().CommitAt("F1", t0.AddMinutes(3));
            b.WriteFile("f2.txt", "f2").Stage().CommitAt("F2", t0.AddMinutes(5));

            b.Checkout("main");
            b.WriteFile("m2.txt", "m2").Stage().CommitAt("M2", t0.AddMinutes(4));
            b.WriteFile("m3.txt", "m3").Stage().CommitAt("M3", t0.AddMinutes(6));
        });

        await using var ctx = _host.Open(repo.Path);
        ctx.MarkFirstGraphPaintDone(); ctx.MarkFirstFullLoadDone();
        var result = await new GraphLayoutService(_host.Settings, locator).ComputeLayoutAsync(ctx, Q, maxRows: 1000);

        // --date-order with distinct timestamps and no clock skew: strictly newest-first.
        var dates = Enumerable.Range(0, result.Layout.RowCount)
            .Select(i => result.Layout.Commits[i].CommitWhen)
            .ToList();
        Assert.Equal(6, dates.Count);
        for (var i = 1; i < dates.Count; i++)
            Assert.True(dates[i - 1] > dates[i], $"row {i - 1} ({dates[i - 1]:O}) must be newer than row {i} ({dates[i]:O})");
    }
}
