namespace Historia.Tests;

/// <summary>
/// The first prime of an all-refs walk paints from a walk of only the recent tips, and keeps
/// those rows only when the commit-graph's corrected dates prove they are the full walk's first
/// rows. Everything painted must be identical to the same service walking every tip — rows, lanes,
/// edges and badges — whether the proof holds, fails, or the prefix is skipped; the full walk must
/// take over from the proven rows without a seam, and a proven row the full walk disagrees with
/// must be thrown away, not kept.
/// </summary>
[Trait("tier", "git")]
public sealed class GraphPrefixWalkTests(ITestOutputHelper output) : IAsyncDisposable
{
    private readonly MiniHost _host = new();
    private readonly List<TestRepositoryBuilder> _repos = [];

    public async ValueTask DisposeAsync()
    {
        GraphLayoutService.PrefixWalkDisabledForTesting = false;
        GraphLayoutService.TestPrefixWalkObserved = null;
        GraphLayoutService.TestRewriteProvenPrefix = null;
        await _host.DisposeAsync();
        foreach (var r in _repos)
            r.Dispose();
    }

    private static readonly GraphQuery AllRefs = new() { RefScope = GraphRefScope.AllRefs };

    private const int Window = 500; // ComputeLayoutAsync pulls this plus 2,000 rows of overscan

    private static readonly DateTimeOffset Newest = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>An old trunk (one commit a day from 2015) with a lightweight tag on each of its
    /// first <paramref name="tags"/> commits; a <c>recent</c> branch of
    /// <paramref name="recentCommits"/> commits a minute apart ending at <see cref="Newest"/>,
    /// forked from the trunk's tip; a short <c>r2</c> branch off the middle of it. With
    /// <paramref name="skewInWindow"/>, an old tag's history hides a commit dated inside the
    /// recent run behind a child dated 2016 — only its corrected date shows it is recent. Then a
    /// commit-graph and packed refs.</summary>
    private static void Build(TestRepositoryBuilder b, int oldCommits, int tags, int recentCommits, bool skewInWindow, bool skewOld)
    {
        var sb = new StringBuilder();
        var mark = 0;
        int Commit(string branch, DateTimeOffset when, int? parent, bool fresh = false)
        {
            mark++;
            if (fresh)
                sb.Append("reset refs/heads/").Append(branch).Append('\n');
            sb.Append("commit refs/heads/").Append(branch).Append('\n');
            sb.Append("mark :").Append(mark).Append('\n');
            var t = when.ToUnixTimeSeconds();
            sb.Append($"author T <t@example.com> {t} +0000\ncommitter T <t@example.com> {t} +0000\n");
            var msg = $"{branch} {mark}\n";
            sb.Append("data ").Append(Encoding.UTF8.GetByteCount(msg)).Append('\n').Append(msg);
            if (parent is { } p)
                sb.Append("from :").Append(p).Append('\n');
            var content = $"{mark}\n";
            sb.Append("M 100644 inline f.txt\ndata ").Append(content.Length).Append('\n').Append(content).Append('\n');
            return mark;
        }
        void Tag(string name, int at) => sb.Append("reset refs/tags/").Append(name).Append("\nfrom :").Append(at).Append("\n\n");

        var start = new DateTimeOffset(2015, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var old = new int[oldCommits];
        for (var i = 0; i < oldCommits; i++)
            old[i] = Commit("main", start.AddDays(i), i == 0 ? null : old[i - 1], fresh: i == 0);
        for (var i = 0; i < tags; i++)
            Tag($"old-{i:D4}", old[i]);

        var recent = new int[recentCommits];
        for (var i = 0; i < recentCommits; i++)
            recent[i] = Commit("recent", Newest.AddMinutes(i - recentCommits + 1), i == 0 ? old[^1] : recent[i - 1], fresh: i == 0);
        var r2 = Commit("r2", Newest.AddMinutes(-recentCommits / 2 + 0.5), recent[recentCommits / 2], fresh: true);
        Commit("r2", Newest.AddMinutes(-recentCommits / 2 + 0.7), r2);

        if (skewInWindow)
        {
            // Dated inside the recent run, reachable only through a child dated 2016.
            var hidden = Commit("skew-in", Newest.AddMinutes(-recentCommits / 3 + 0.25), old[300], fresh: true);
            Tag("skew-in-window", Commit("skew-in", new DateTimeOffset(2016, 1, 1, 0, 0, 0, TimeSpan.Zero), hidden));
        }
        if (skewOld)
        {
            // An old branch whose parent is dated after its child.
            var parent = Commit("skew-old", start.AddDays(400), old[100], fresh: true);
            Commit("skew-old", start.AddDays(200), parent);
        }

        b.FastImport(sb.ToString());
        if (skewInWindow)
            b.Git("update-ref", "-d", "refs/heads/skew-in");
        b.Git("checkout", "-q", "-f", "recent");
        b.Git("commit-graph", "write", "--reachable");
        b.Git("pack-refs", "--all");
    }

    private TestRepositoryBuilder Repo(string key, int oldCommits = 1300, int tags = 1200, int recentCommits = 2700,
        bool skewInWindow = false, bool skewOld = false)
    {
        var repo = TestRepositoryBuilder.FromTemplate("prefix-walk-" + key,
            b => Build(b, oldCommits, tags, recentCommits, skewInWindow, skewOld));
        _repos.Add(repo);
        return repo;
    }

    private RepositoryContext Open(TestRepositoryBuilder repo)
    {
        var ctx = _host.Open(repo.Path);
        ctx.MarkFirstGraphPaintDone();
        ctx.MarkFirstFullLoadDone();
        return ctx;
    }

    private GraphLayoutService Service() => new(_host.Settings, _host.Locator);

    /// <summary>Everything a first window paints: each row's commit, lane, colour, flags and parent
    /// count; every edge; every badge.</summary>
    private static string Painted(GraphLayoutResult r)
    {
        var sb = new StringBuilder();
        var layout = r.Layout;
        for (var i = 0; i < layout.RowCount; i++)
        {
            var row = layout.Rows[i];
            sb.Append(layout.Commits[i].Id).Append(' ').Append(row.LaneIndex).Append(' ').Append(row.ColorIndex)
                .Append(' ').Append(row.Flags).Append(' ').Append(row.ParentCount).Append('\n');
        }
        foreach (var e in layout.Edges)
            sb.Append(e).Append('\n');
        foreach (var d in layout.Decorations)
            sb.Append(d.RowIndex).Append(' ').Append(d.Type).Append(' ').Append(d.Label).Append(' ').Append(d.ColorIndex)
                .Append(' ').Append(d.IsLocal).Append(' ').Append(d.IsHead).Append(' ').Append(string.Join(',', d.RemotesOrNull ?? []))
                .Append('\n');
        return sb.ToString();
    }

    private static List<GitObjectId> Ids(GraphLayoutResult r) =>
        [.. Enumerable.Range(0, r.Layout.RowCount).Select(i => r.Layout.Commits[i].Id)];

    /// <summary>The same first window from a service that walks every tip, on a separate copy of
    /// the repository so neither run can read the other's disk cache.</summary>
    private async Task<GraphLayoutResult> FullWalk(GraphQuery query, int rows, Func<TestRepositoryBuilder> copy)
    {
        GraphLayoutService.PrefixWalkDisabledForTesting = true;
        try
        {
            await using var ctx = Open(copy());
            return await Service().ComputeLayoutAsync(ctx, query, rows);
        }
        finally { GraphLayoutService.PrefixWalkDisabledForTesting = false; }
    }

    private async Task<(GraphLayoutResult Result, List<GraphLayoutService.PrefixWalkOutcome> Outcomes)> WithPrefix(
        TestRepositoryBuilder repo, GraphQuery query, int rows = Window)
    {
        var outcomes = new List<GraphLayoutService.PrefixWalkOutcome>();
        GraphLayoutService.TestPrefixWalkObserved = o => { lock (outcomes) outcomes.Add(o); };
        await using var ctx = Open(repo);
        var result = await Service().ComputeLayoutAsync(ctx, query, rows);
        return (result, outcomes);
    }

    [Fact]
    public async Task Recent_tips_paint_the_same_first_window_as_every_tip()
    {
        var (result, outcomes) = await WithPrefix(Repo("main"), AllRefs);
        var expected = await FullWalk(AllRefs, Window, () => Repo("main"));

        var outcome = Assert.Single(outcomes);
        Assert.True(outcome.Proven, outcome.FallbackReason);
        Assert.True(outcome.RecentTips < outcome.TotalTips / 10, $"{outcome.RecentTips} of {outcome.TotalTips}");
        Assert.True(outcome.TotalTips >= 1_200);
        Assert.Equal(Window, result.Layout.RowCount);
        Assert.Equal(Painted(expected), Painted(result));
    }

    [Fact]
    public async Task Failed_proof_falls_back_and_paints_the_same()
    {
        // Ten recent commits: the window runs into the trunk, older than the trunk tip's date.
        var (result, outcomes) = await WithPrefix(Repo("short-recent", recentCommits: 10), AllRefs);
        var expected = await FullWalk(AllRefs, Window, () => Repo("short-recent", recentCommits: 10));

        var outcome = Assert.Single(outcomes);
        Assert.False(outcome.Proven);
        Assert.False(string.IsNullOrEmpty(outcome.FallbackReason));
        Assert.Equal(Painted(expected), Painted(result));
    }

    [Fact]
    public async Task Commit_dated_inside_the_window_behind_an_old_child_is_ordered_by_its_corrected_date()
    {
        var (result, outcomes) = await WithPrefix(Repo("skew-in", skewInWindow: true), AllRefs);
        var expected = await FullWalk(AllRefs, Window, () => Repo("skew-in", skewInWindow: true));

        Assert.True(Assert.Single(outcomes).Proven);
        Assert.Equal(Painted(expected), Painted(result));
    }

    [Fact]
    public async Task Clock_skew_on_an_old_branch_paints_the_same()
    {
        var (result, outcomes) = await WithPrefix(Repo("skew-old", skewOld: true), AllRefs);
        var expected = await FullWalk(AllRefs, Window, () => Repo("skew-old", skewOld: true));

        Assert.True(Assert.Single(outcomes).Proven);
        Assert.Equal(Painted(expected), Painted(result));
    }

    [Fact]
    public async Task No_commit_graph_skips_the_prefix()
    {
        TestRepositoryBuilder Copy()
        {
            var repo = Repo("main");
            File.Delete(Path.Combine(repo.Path, ".git", "objects", "info", "commit-graph"));
            return repo;
        }
        var (result, outcomes) = await WithPrefix(Copy(), AllRefs);
        var expected = await FullWalk(AllRefs, Window, Copy);

        Assert.False(Assert.Single(outcomes).Proven);
        Assert.Equal(Painted(expected), Painted(result));
    }

    [Fact]
    public async Task Generation_one_graph_skips_the_prefix()
    {
        TestRepositoryBuilder Copy()
        {
            var repo = Repo("main");
            repo.Git("-c", "commitGraph.generationVersion=1", "commit-graph", "write", "--reachable");
            return repo;
        }
        var (result, outcomes) = await WithPrefix(Copy(), AllRefs);
        var expected = await FullWalk(AllRefs, Window, Copy);

        Assert.False(Assert.Single(outcomes).Proven);
        Assert.Equal(Painted(expected), Painted(result));
    }

    [Fact]
    public async Task Path_filter_skips_the_prefix()
    {
        var query = AllRefs with { PathFilter = "f.txt" };
        var (result, outcomes) = await WithPrefix(Repo("main"), query);
        var expected = await FullWalk(query, Window, () => Repo("main"));

        Assert.Empty(outcomes);
        Assert.Equal(Painted(expected), Painted(result));
    }

    [Fact]
    public async Task Current_branch_scope_skips_the_prefix()
    {
        var query = AllRefs with { RefScope = GraphRefScope.CurrentBranch };
        var (result, outcomes) = await WithPrefix(Repo("main"), query);
        var expected = await FullWalk(query, Window, () => Repo("main"));

        Assert.Empty(outcomes);
        Assert.Equal(Painted(expected), Painted(result));
    }

    [Fact]
    public async Task Few_tips_with_an_old_tag_use_the_prefix()
    {
        var (result, outcomes) = await WithPrefix(Repo("few-tags", tags: 5), AllRefs);
        var expected = await FullWalk(AllRefs, Window, () => Repo("few-tags", tags: 5));

        var outcome = Assert.Single(outcomes);
        Assert.True(outcome.Proven, outcome.FallbackReason);
        Assert.True(outcome.TotalTips < 10, $"{outcome.TotalTips} tips");
        Assert.Equal(Painted(expected), Painted(result));
    }

    [Fact]
    public async Task Every_tip_recent_skips_the_prefix()
    {
        TestRepositoryBuilder Copy()
        {
            var repo = TestRepositoryBuilder.FromTemplate("prefix-walk-all-recent", b =>
            {
                for (var i = 0; i < 20; i++)
                {
                    b.WriteFile("f.txt", $"{i}\n").Stage();
                    b.CommitAt($"c{i}", Newest.AddDays(-20 + i));
                    if (i % 4 == 0)
                        b.Git("tag", $"t{i}");
                }
                b.Git("commit-graph", "write", "--reachable");
            });
            _repos.Add(repo);
            return repo;
        }
        var (result, outcomes) = await WithPrefix(Copy(), AllRefs);
        var expected = await FullWalk(AllRefs, Window, Copy);

        var outcome = Assert.Single(outcomes);
        Assert.False(outcome.Proven);
        Assert.Equal("every tip is recent", outcome.FallbackReason);
        Assert.Equal(Painted(expected), Painted(result));
    }

    [Fact]
    public async Task Single_tip_skips_the_prefix()
    {
        TestRepositoryBuilder Copy()
        {
            var repo = TestRepositoryBuilder.FromTemplate("prefix-walk-single-tip", b =>
            {
                b.LinearHistory(30);
                b.Git("commit-graph", "write", "--reachable");
            });
            _repos.Add(repo);
            return repo;
        }
        var (result, outcomes) = await WithPrefix(Copy(), AllRefs);
        var expected = await FullWalk(AllRefs, Window, Copy);

        Assert.Empty(outcomes);
        Assert.Equal(Painted(expected), Painted(result));
    }

    [Fact]
    public async Task Full_walk_takes_over_from_the_proven_rows()
    {
        var repo = Repo("main");
        var outcomes = new List<GraphLayoutService.PrefixWalkOutcome>();
        GraphLayoutService.TestPrefixWalkObserved = o => { lock (outcomes) outcomes.Add(o); };
        await using var ctx = Open(repo);
        var service = Service();
        var first = await service.ComputeLayoutAsync(ctx, AllRefs, Window);
        Assert.True(Assert.Single(outcomes).Proven);

        var extended = await service.ExtendLayoutAsync(ctx, first, 2 * Window);
        extended = await service.ExtendLayoutAsync(ctx, extended, 3 * Window); // past the proven rows
        var expected = await FullWalk(AllRefs, 6 * Window, () => Repo("main"));

        Assert.Equal(6 * Window, extended.Layout.RowCount);
        Assert.Equal(Ids(expected), Ids(extended));
        Assert.Single(outcomes);
        Assert.True(GitLogStream.LiveCountForTesting(ctx.RepositoryPath) <= 1);
        service.Forget(ctx);
    }

    [Fact]
    public async Task Proven_row_the_full_walk_disagrees_with_is_thrown_away()
    {
        var repo = Repo("main");
        // Swap two proven rows: real commits, wrong order.
        GraphLayoutService.TestRewriteProvenPrefix = rows =>
        {
            var copy = rows.ToList();
            (copy[5], copy[6]) = (copy[6], copy[5]);
            return copy;
        };
        await using var ctx = Open(repo);
        var service = Service();
        var first = await service.ComputeLayoutAsync(ctx, AllRefs, Window);
        GraphLayoutService.TestRewriteProvenPrefix = null;

        var extended = await service.ExtendLayoutAsync(ctx, first, 2 * Window);
        var expected = await FullWalk(AllRefs, 3 * Window, () => Repo("main"));

        Assert.NotSame(first.State, extended.State);
        Assert.Equal(Ids(expected), Ids(extended));

        // Further extends continue the full walk.
        var further = await service.ExtendLayoutAsync(ctx, extended, Window);
        var expectedFurther = await FullWalk(AllRefs, 4 * Window, () => Repo("main"));
        Assert.Equal(Ids(expectedFurther), Ids(further));
        service.Forget(ctx);
    }

    /// <summary>The first window of each <c>HISTORIA_PERF_REPOS</c> repository with and without
    /// the prefix (warm, best of 3), and the prefix's tip count. Read-only apart from Historia's
    /// own cache folder under the test state directory.</summary>
    [ColdLoadPerfFact]
    public async Task First_window_of_a_real_repository()
    {
        var repos = (Environment.GetEnvironmentVariable("HISTORIA_PERF_REPOS") ?? "")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(Directory.Exists);
        foreach (var path in repos)
        {
            foreach (var disabled in new[] { false, true })
            {
                var best = double.MaxValue;
                GraphLayoutService.PrefixWalkOutcome? outcome = null;
                for (var round = 0; round < 3; round++)
                {
                    GraphLayoutService.PrefixWalkDisabledForTesting = disabled;
                    GraphLayoutService.TestPrefixWalkObserved = o => outcome = o;
                    await using var ctx = _host.Open(path);
                    ctx.MarkFirstGraphPaintDone();
                    ctx.MarkFirstFullLoadDone();
                    var service = Service();
                    await ctx.RefSnapshots.GetAsync(); // the ref scan is measured on its own
                    var sw = Stopwatch.StartNew();
                    await service.ComputeLayoutAsync(ctx, AllRefs, 200);
                    best = Math.Min(best, sw.Elapsed.TotalMilliseconds);
                    service.Forget(ctx);
                }
                GraphLayoutService.PrefixWalkDisabledForTesting = false;
                output.WriteLine($"{path}: prefix {(disabled ? "off" : "on")}: best {best:F0} ms, {outcome}");
            }
        }
    }
}
