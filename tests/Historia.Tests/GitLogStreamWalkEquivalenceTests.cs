namespace Historia.Tests;

/// <summary>
/// §3 linux cold-prime plan, Verification item 1: the CLI streaming walk
/// (<see cref="GitLogStream"/>, via <see cref="GraphLayoutService"/>'s 2-arg DI ctor) and the
/// libgit2 fallback walk (the 1-arg ctor) must agree on the reachable commit set and on
/// topological order for the same ref set — branches, a merge, a tag, and an unrelated second
/// root, mirroring linux's own mix of tags + multiple lines of history. Skipped when git.exe
/// isn't discoverable by <see cref="IGitExecutableLocator"/> (the CLI engine never engages).
/// </summary>
public sealed class GitLogStreamWalkEquivalenceTests : IAsyncDisposable
{
    private readonly MiniHost _host = new();
    private readonly TestRepositoryBuilder _repo;

    public GitLogStreamWalkEquivalenceTests()
    {
        _repo = TestRepositoryBuilder.Create();

        _repo.WriteFile("root.txt", "unrelated\n").Stage().Commit("unrelated root");

        // A second, wholly unrelated root — only reachable via its own orphan branch, like a
        // grafted-in history. Exercises the tip peeling / multi-root walk on both engines.
        _repo.Git("checkout", "--orphan", "unrelated");
        _repo.Git("rm", "-rf", ".");
        _repo.WriteFile("other.txt", "x\n").Stage().Commit("second root");
        _repo.Git("checkout", "main");

        for (var i = 0; i < 5; i++)
            _repo.WriteFile("f.txt", $"{i}\n").Stage().Commit($"main {i}");

        _repo.Git("checkout", "-b", "feature");
        _repo.WriteFile("feature.txt", "x\n").Stage().Commit("feature work");
        _repo.Git("checkout", "main");
        _repo.WriteFile("f2.txt", "y\n").Stage().Commit("main after fork");
        _repo.Git("merge", "--no-ff", "-m", "merge feature", "feature");
        _repo.Git("tag", "v1.0");
    }

    public async ValueTask DisposeAsync()
    {
        _repo.Dispose();
        await _host.DisposeAsync();
    }

    private static GraphQuery Q => new() { RefScope = GraphRefScope.AllRefs };

    private static List<ObjectId> RowIds(GraphLayoutResult r) =>
        Enumerable.Range(0, r.Layout.RowCount).Select(i => r.Layout.Commits[i].Id).ToList();

    /// <summary>Ground-truth check independent of either engine: for every row, every real
    /// libgit2 parent of that row's commit (when also present in the walked window) must sort at
    /// a LATER row — children-before-parents, regardless of which engine produced the order.</summary>
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

    [Fact]
    public async Task Cli_and_libgit2_walks_agree_on_the_reachable_commit_set()
    {
        var locator = _host.Provider.GetRequiredService<IGitExecutableLocator>();
        if (!locator.Current.Found)
            return; // git.exe not discoverable — nothing to compare (see class doc)

        await using var ctxCli = _host.Open(_repo.Path);
        ctxCli.MarkFirstGraphPaintDone(); ctxCli.MarkFirstFullLoadDone();
        var cliSvc = new GraphLayoutService(_host.Settings, locator);
        var cliResult = await cliSvc.ComputeLayoutAsync(ctxCli, Q, maxRows: 1000);

        await using var ctxLibgit2 = _host.Open(_repo.Path);
        ctxLibgit2.MarkFirstGraphPaintDone(); ctxLibgit2.MarkFirstFullLoadDone();
        var libgit2Svc = new GraphLayoutService(_host.Settings);
        var libgit2Result = await libgit2Svc.ComputeLayoutAsync(ctxLibgit2, Q, maxRows: 1000);

        var cliIds = new HashSet<ObjectId>(RowIds(cliResult));
        var libIds = new HashSet<ObjectId>(RowIds(libgit2Result));

        Assert.NotEmpty(cliIds);
        Assert.Equal(libIds, cliIds);
        // Sanity: both roots and the merge are actually in the walked set.
        Assert.Equal(10, cliIds.Count);

        await AssertChildrenBeforeParents(ctxCli, cliResult);
        await AssertChildrenBeforeParents(ctxLibgit2, libgit2Result);
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

    /// <summary>§10.1.3 finding #18: an annotated tag's ref target is the TAG OBJECT, not the
    /// commit it points at. Before the fix, <c>GraphLayoutService.ResolveTipShas</c> called
    /// <c>repo.Lookup&lt;Commit&gt;(target)</c> straight on that target and silently dropped the
    /// tip when it returned null — exactly this fixture's shape (a release tagged on a branch that
    /// is then deleted, so the annotated tag is the ONLY remaining path to that history).
    /// libgit2's own <see cref="StartWalk"/> fallback never had this gap: it feeds
    /// <see cref="Reference"/> objects straight to <c>CommitFilter.IncludeReachableFrom</c>, which
    /// peels annotated tags internally — so before the fix the two engines walked different
    /// histories for this repo.</summary>
    [Fact]
    public async Task Cli_and_libgit2_walks_agree_when_a_commit_is_reachable_only_via_an_annotated_tag_on_a_deleted_branch()
    {
        var locator = _host.Provider.GetRequiredService<IGitExecutableLocator>();
        if (!locator.Current.Found)
            return; // git.exe not discoverable — nothing to compare (see class doc)

        _repo.Git("checkout", "-b", "doomed");
        var doomedSha = _repo.WriteFile("doomed.txt", "x\n").Stage().Commit("doomed work");
        _repo.Git("tag", "-a", "release-1.0", "-m", "release 1.0");
        _repo.Git("checkout", "main");
        _repo.Git("branch", "-D", "doomed");

        await using var ctxCli = _host.Open(_repo.Path);
        ctxCli.MarkFirstGraphPaintDone(); ctxCli.MarkFirstFullLoadDone();
        var cliResult = await new GraphLayoutService(_host.Settings, locator).ComputeLayoutAsync(ctxCli, Q, maxRows: 1000);

        await using var ctxLibgit2 = _host.Open(_repo.Path);
        ctxLibgit2.MarkFirstGraphPaintDone(); ctxLibgit2.MarkFirstFullLoadDone();
        var libgit2Result = await new GraphLayoutService(_host.Settings).ComputeLayoutAsync(ctxLibgit2, Q, maxRows: 1000);

        var cliIds = new HashSet<ObjectId>(RowIds(cliResult));
        var libIds = new HashSet<ObjectId>(RowIds(libgit2Result));

        Assert.Equal(libIds, cliIds);
        // The doomed-branch commit must actually be present — the annotated tag is its only
        // remaining path now that the branch itself is deleted.
        Assert.Contains(new ObjectId(doomedSha), cliIds);
    }
}
