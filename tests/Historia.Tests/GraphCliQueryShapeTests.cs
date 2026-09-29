namespace Historia.Tests;

/// <summary>
/// Every graph query shape — current branch, selected refs, stashes, path filter — walks through
/// <c>git log</c>, an extend keeps the whole query, and a walk that can't run gives an empty
/// graph that persists nothing and retries after <see cref="GraphLayoutService.Forget"/>.
/// Skipped when git.exe isn't discoverable by <see cref="IGitExecutableLocator"/>.
/// </summary>
[Trait("tier", "git")]
public sealed class GraphCliQueryShapeTests : IAsyncDisposable
{
    private readonly MiniHost _host = new();
    private readonly List<TestRepositoryBuilder> _repos = [];

    public async ValueTask DisposeAsync()
    {
        foreach (var r in _repos)
            r.Dispose();
        await _host.DisposeAsync();
    }

    private bool GitMissing => !_host.Locator.Current.Found;

    private TestRepositoryBuilder NewRepo()
    {
        var repo = TestRepositoryBuilder.Create();
        _repos.Add(repo);
        return repo;
    }

    private RepositoryContext Open(TestRepositoryBuilder repo)
    {
        var ctx = _host.Open(repo.Path);
        ctx.MarkFirstGraphPaintDone(); ctx.MarkFirstFullLoadDone();
        return ctx;
    }

    private static List<GitObjectId> Ids(GraphLayoutResult r) =>
        Enumerable.Range(0, r.Layout.RowCount).Select(i => r.Layout.Commits[i].Id).ToList();

    private static GitObjectId Id(string sha) => GitObjectId.Parse(sha.Trim());

    /// <summary>main: c1 ← c2 (HEAD); feature: c2 ← f1; other: c1 ← o1.</summary>
    private (TestRepositoryBuilder Repo, string C1, string C2, string F1, string O1) ThreeBranches()
    {
        var repo = NewRepo();
        var c1 = repo.WriteFile("a.txt", "1\n").Stage().Commit("c1");
        var c2 = repo.WriteFile("a.txt", "2\n").Stage().Commit("c2");
        repo.Checkout("feature", create: true);
        var f1 = repo.WriteFile("f.txt", "1\n").Stage().Commit("f1");
        repo.Git("checkout", "-b", "other", c1);
        var o1 = repo.WriteFile("o.txt", "1\n").Stage().Commit("o1");
        repo.Checkout("main");
        return (repo, c1, c2, f1, o1);
    }

    [Fact]
    public async Task Current_branch_scope_walks_only_head_history()
    {
        if (GitMissing)
            return;
        var (repo, c1, c2, _, _) = ThreeBranches();

        await using var ctx = Open(repo);
        var svc = new GraphLayoutService(_host.Settings, _host.Locator);
        var result = await svc.ComputeLayoutAsync(ctx, new GraphQuery { RefScope = GraphRefScope.CurrentBranch });

        Assert.Equal([Id(c2), Id(c1)], Ids(result));
        Assert.True(result.Layout.IsComplete);
    }

    [Fact]
    public async Task Selected_refs_scope_walks_only_those_refs()
    {
        if (GitMissing)
            return;
        var (repo, c1, c2, f1, o1) = ThreeBranches();

        await using var ctx = Open(repo);
        var svc = new GraphLayoutService(_host.Settings, _host.Locator);
        var result = await svc.ComputeLayoutAsync(ctx, new GraphQuery
        {
            RefScope = GraphRefScope.Selected,
            SelectedRefs = ["refs/heads/feature"],
        });

        Assert.Equal(new HashSet<GitObjectId> { Id(f1), Id(c2), Id(c1) }, Ids(result).ToHashSet());
        Assert.DoesNotContain(Id(o1), Ids(result));
    }

    [Fact]
    public async Task Stashes_on_adds_the_stash_commits_and_their_badge()
    {
        if (GitMissing)
            return;
        var repo = NewRepo();
        repo.WriteFile("a.txt", "1\n").Stage().Commit("c1");
        repo.WriteFile("a.txt", "dirty\n");
        repo.Git("stash", "push", "-m", "wip");
        var stash = Id(repo.Git("rev-parse", "stash@{0}"));

        await using var ctx = Open(repo);
        var svc = new GraphLayoutService(_host.Settings, _host.Locator);

        var with = await svc.ComputeLayoutAsync(ctx, new GraphQuery { IncludeStashes = true });
        var row = Ids(with).IndexOf(stash);
        Assert.True(row >= 0, "the stash work-tree commit should be a row");
        Assert.Contains(with.Layout.Decorations,
            d => d.RowIndex == row && d.Type == DecorationType.Stash && d.Label == "stash@{0}");

        var without = await svc.ComputeLayoutAsync(ctx, new GraphQuery { IncludeStashes = false });
        Assert.DoesNotContain(stash, Ids(without));
    }

    [Fact]
    public async Task Path_filter_layout_has_no_edge_to_a_missing_row()
    {
        if (GitMissing)
            return;
        var repo = NewRepo();
        repo.WriteFile("dir/base.txt", "1\n").Stage().Commit("base");
        repo.WriteFile("elsewhere.txt", "0\n").Stage().Commit("base elsewhere");
        repo.Checkout("side", create: true);
        repo.WriteFile("dir/side.txt", "1\n").Stage().Commit("side touches dir");
        repo.WriteFile("other.txt", "1\n").Stage().Commit("side elsewhere");
        repo.Checkout("main");
        repo.WriteFile("dir/trunk.txt", "1\n").Stage().Commit("trunk touches dir");
        repo.WriteFile("elsewhere.txt", "1\n").Stage().Commit("trunk elsewhere");
        repo.Git("merge", "--no-ff", "-m", "merge side", "side");

        await using var ctx = Open(repo);
        var svc = new GraphLayoutService(_host.Settings, _host.Locator);
        var result = await svc.ComputeLayoutAsync(ctx, new GraphQuery { PathFilter = "dir" });
        var layout = result.Layout;

        Assert.True(layout.IsComplete);
        Assert.Equal(4, layout.RowCount); // merge, trunk, side, base — the "elsewhere" commits drop out
        // Parents are rewritten to shown commits, so every parent link is a real edge between two
        // real rows — none runs off the window.
        Assert.DoesNotContain(layout.Edges, e => e.Type == EdgeType.OffWindow);
        foreach (var e in layout.Edges)
        {
            Assert.InRange(e.FromRow, 0, layout.RowCount - 1);
            Assert.InRange(e.ToRow, 0, layout.RowCount - 1);
        }
        var parentLinks = Enumerable.Range(0, layout.RowCount).Sum(i => layout.Commits[i].ParentCount);
        Assert.Equal(parentLinks, layout.Edges.Length);
        Assert.Equal(2, layout.Commits[0].ParentCount); // the merge keeps both sides
    }

    /// <summary>More rows than the first compute's walk (maxRows + the 2,000-row overscan), so
    /// the extend genuinely resumes the walk rather than reading rows already pulled. Commits
    /// alternate between <c>p.txt</c> and <c>q.txt</c>.</summary>
    private TestRepositoryBuilder AlternatingHistory(int commits)
    {
        var repo = NewRepo();
        var sb = new StringBuilder();
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        for (var i = 0; i < commits; i++)
        {
            var msg = $"commit {i}\n";
            var file = i % 2 == 0 ? "p.txt" : "q.txt";
            var content = $"{i}\n";
            sb.Append("commit refs/heads/main\n");
            sb.Append($"mark :{i + 1}\n");
            sb.Append($"author Test User <test@example.com> {t0 + i * 60} +0000\n");
            sb.Append($"committer Test User <test@example.com> {t0 + i * 60} +0000\n");
            sb.Append($"data {Encoding.UTF8.GetByteCount(msg)}\n{msg}");
            sb.Append($"M 100644 inline {file}\n");
            sb.Append($"data {Encoding.UTF8.GetByteCount(content)}\n{content}\n");
        }
        repo.FastImport(sb.ToString());
        repo.Git("reset", "--hard");
        return repo;
    }

    private async Task AssertExtendMatchesOneFullLayout(TestRepositoryBuilder repo, GraphQuery query)
    {
        await using var ctx = Open(repo);
        var svc = new GraphLayoutService(_host.Settings, _host.Locator);
        var current = await svc.ComputeLayoutAsync(ctx, query, maxRows: 2);
        Assert.Equal(2, current.Layout.RowCount);
        for (var i = 0; i < 10; i++)
        {
            var next = await svc.ExtendLayoutAsync(ctx, current, additionalRows: 2_500);
            if (next.Layout.RowCount == current.Layout.RowCount)
                break;
            current = next;
        }

        await using var fullCtx = Open(repo);
        var full = await new GraphLayoutService(_host.Settings, _host.Locator)
            .ComputeLayoutAsync(fullCtx, query, maxRows: 10_000);

        Assert.True(full.Layout.IsComplete);
        Assert.Equal(Ids(full), Ids(current));
    }

    [Fact]
    public async Task Extend_keeps_the_path_filter()
    {
        if (GitMissing)
            return;
        var repo = AlternatingHistory(4_400);
        await AssertExtendMatchesOneFullLayout(repo, new GraphQuery { PathFilter = "p.txt" });

        await using var ctx = Open(repo);
        var rows = await new GraphLayoutService(_host.Settings, _host.Locator)
            .ComputeLayoutAsync(ctx, new GraphQuery { PathFilter = "p.txt" }, maxRows: 10_000);
        Assert.Equal(2_200, rows.Layout.RowCount); // only the even commits touch p.txt
        for (var i = 0; i < rows.Layout.RowCount; i++)
            Assert.Equal(0, int.Parse(rows.Layout.Commits[i].MessageShort["commit ".Length..]) % 2);
    }

    [Fact]
    public async Task Extend_keeps_the_stashes()
    {
        if (GitMissing)
            return;
        var repo = NewRepo().LinearHistory(2_200);
        repo.WriteFile("f.txt", "dirty\n");
        repo.Git("stash", "push", "-m", "wip");
        var stash = Id(repo.Git("rev-parse", "stash@{0}"));

        var query = new GraphQuery { IncludeStashes = true };
        await AssertExtendMatchesOneFullLayout(repo, query);

        await using var ctx = Open(repo);
        var full = await new GraphLayoutService(_host.Settings, _host.Locator).ComputeLayoutAsync(ctx, query, maxRows: 10_000);
        Assert.Contains(stash, Ids(full));
    }

    [Fact]
    public async Task Changing_the_path_filter_resets_the_walk()
    {
        if (GitMissing)
            return;
        var repo = NewRepo();
        var a1 = repo.WriteFile("a.txt", "1\n").Stage().Commit("a1");
        var b1 = repo.WriteFile("b.txt", "1\n").Stage().Commit("b1");
        var a2 = repo.WriteFile("a.txt", "2\n").Stage().Commit("a2");

        await using var ctx = Open(repo);
        var svc = new GraphLayoutService(_host.Settings, _host.Locator);
        var forA = await svc.ComputeLayoutAsync(ctx, new GraphQuery { PathFilter = "a.txt" });
        var forB = await svc.ComputeLayoutAsync(ctx, new GraphQuery { PathFilter = "b.txt" });
        var unfiltered = await svc.ComputeLayoutAsync(ctx, new GraphQuery());

        Assert.Equal([Id(a2), Id(a1)], Ids(forA));
        Assert.Equal([Id(b1)], Ids(forB));
        Assert.Equal([Id(a2), Id(b1), Id(a1)], Ids(unfiltered));
    }

    /// <summary>A locator that reports git.exe missing until <see cref="Missing"/> is cleared,
    /// then defers to the real one.</summary>
    private sealed class SwitchableLocator(IGitExecutableLocator real) : IGitExecutableLocator
    {
        public bool Missing { get; set; } = true;
        public GitExecutableInfo Current => Missing ? GitExecutableInfo.NotFound : real.Current;
        public Task<GitExecutableInfo> ResolveAsync(string? userOverridePath, CancellationToken ct = default) =>
            Task.FromResult(Current);
        public Task<GitExecutableInfo> WhenResolvedAsync(CancellationToken ct = default) =>
            Task.FromResult(Current);
    }

    private static string[] CacheFilesFor(string repoPath)
    {
        var hdr = GraphCachePaths.File(repoPath, GraphMetaStore.HdrExt);
        var dir = Path.GetDirectoryName(hdr)!;
        return Directory.Exists(dir)
            ? Directory.GetFiles(dir, GraphCachePaths.Stem(repoPath) + "*")
            : [];
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Git_missing_gives_an_empty_graph_and_persists_nothing(bool metadataFromDisk)
    {
        if (GitMissing)
            return;
        var repo = NewRepo().LinearHistory(30);
        await _host.Settings.UpdateAsync(s => s with { GraphMetadataFromDisk = metadataFromDisk });
        var locator = new SwitchableLocator(_host.Locator);

        await using var ctx = Open(repo);
        var svc = new GraphLayoutService(_host.Settings, locator);
        var query = new GraphQuery();

        var result = await svc.ComputeLayoutAsync(ctx, query);
        Assert.Equal(0, result.Layout.RowCount);

        // No respawn: even with git back, the failed cache doesn't walk again until it is reset.
        locator.Missing = false;
        var extended = await svc.ExtendLayoutAsync(ctx, result, additionalRows: 100);
        Assert.Equal(0, extended.Layout.RowCount);

        await Task.Delay(300); // any background spill / .gz save would have been queued by now
        Assert.Empty(CacheFilesFor(repo.Path));
        svc.Forget(ctx);
    }

    [Fact]
    public async Task A_failed_walk_retries_after_Forget()
    {
        if (GitMissing)
            return;
        var repo = NewRepo().LinearHistory(30);
        var locator = new SwitchableLocator(_host.Locator);

        await using var ctx = Open(repo);
        var svc = new GraphLayoutService(_host.Settings, locator);
        var query = new GraphQuery();

        Assert.Equal(0, (await svc.ComputeLayoutAsync(ctx, query)).Layout.RowCount);

        locator.Missing = false;
        svc.Forget(ctx);
        var result = await svc.ComputeLayoutAsync(ctx, query);

        Assert.Equal(30, result.Layout.RowCount);
        Assert.True(result.Layout.IsComplete);
    }
}
