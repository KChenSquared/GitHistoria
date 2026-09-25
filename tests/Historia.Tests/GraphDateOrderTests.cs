using System.IO.Compression;
using System.Text.Json.Nodes;

namespace Historia.Tests;
[Trait("tier", "git")]

public sealed class GraphDateOrderTests : IAsyncDisposable
{
    private readonly MiniHost _host = new();

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
    }

    private static GraphQuery Q => new() { RefScope = GraphRefScope.AllRefs };

    private static List<string> Messages(GraphLayoutResult r) =>
        Enumerable.Range(0, r.Layout.RowCount).Select(i => r.Layout.Commits[i].MessageShort).ToList();

    [Fact]
    public async Task Cli_walk_puts_a_newer_branch_commit_above_older_trunk_commits()
    {
        var locator = _host.Provider.GetRequiredService<IGitExecutableLocator>();
        if (!locator.Current.Found)
            return;

        using var repo = TestRepositoryBuilder.FromTemplate("graph-date-order-newer-branch", b =>
        {
            var t0 = DateTimeOffset.Parse("2026-09-15T10:00:00Z");
            var xSha = b.WriteFile("x.txt", "x").Stage().CommitAt("X", t0);
            b.WriteFile("y.txt", "y").Stage().CommitAt("Y", t0.AddMinutes(10));
            b.WriteFile("z.txt", "z").Stage().CommitAt("Z", t0.AddMinutes(20));

            b.Checkout("branch-o", create: true);
            b.Git("reset", "--hard", xSha);
            b.WriteFile("o.txt", "o").Stage().CommitAt("O", t0.AddMinutes(15));
        });

        await using var ctx = _host.Open(repo.Path);
        ctx.MarkFirstGraphPaintDone(); ctx.MarkFirstFullLoadDone();
        var svc = new GraphLayoutService(_host.Settings, locator);
        var result = await svc.ComputeLayoutAsync(ctx, Q);

        var messages = Messages(result);
        Assert.Equal(["Z", "O", "Y", "X"], messages);
    }

    [Fact]
    public async Task Cli_walk_interleaves_two_branches_by_commit_date()
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
        var svc = new GraphLayoutService(_host.Settings, locator);
        var result = await svc.ComputeLayoutAsync(ctx, Q);

        var messages = Messages(result);
        Assert.Equal(["M3", "F2", "M2", "F1", "M1", "A"], messages);
    }

    [Fact]
    public async Task Rows_follow_the_commit_date_not_the_author_date()
    {
        var locator = _host.Provider.GetRequiredService<IGitExecutableLocator>();
        if (!locator.Current.Found)
            return;

        using var repo = TestRepositoryBuilder.FromTemplate("graph-author-vs-commit-date", b =>
        {
            var t0 = DateTimeOffset.Parse("2026-09-15T12:00:00Z");
            b.WriteFile("1.txt", "1").Stage().CommitAt("C1", authorWhen: t0.AddMinutes(6), commitWhen: t0.AddMinutes(1));
            b.WriteFile("2.txt", "2").Stage().CommitAt("C2", authorWhen: t0.AddMinutes(5), commitWhen: t0.AddMinutes(2));
            b.WriteFile("3.txt", "3").Stage().CommitAt("C3", authorWhen: t0.AddMinutes(4), commitWhen: t0.AddMinutes(3));
        });

        await using var ctx = _host.Open(repo.Path);
        ctx.MarkFirstGraphPaintDone(); ctx.MarkFirstFullLoadDone();
        var svc = new GraphLayoutService(_host.Settings, locator);
        var result = await svc.ComputeLayoutAsync(ctx, Q);

        var messages = Messages(result);
        Assert.Equal(["C3", "C2", "C1"], messages);
    }

    [Fact]
    public async Task A_v4_walk_cache_file_is_a_miss_and_the_graph_re_walks()
    {
        var locator = _host.Provider.GetRequiredService<IGitExecutableLocator>();
        if (!locator.Current.Found)
            return;

        await _host.Settings.UpdateAsync(s => s with { GraphMetadataFromDisk = false });

        using var repo = TestRepositoryBuilder.FromTemplate("graph-c1-c2", b =>
        {
            var t0 = DateTimeOffset.Parse("2026-09-15T12:00:00Z");
            b.WriteFile("1.txt", "1").Stage().CommitAt("C1", t0.AddMinutes(1));
            b.WriteFile("2.txt", "2").Stage().CommitAt("C2", t0.AddMinutes(2));
        });

        await using var ctx1 = _host.Open(repo.Path);
        ctx1.MarkFirstGraphPaintDone(); ctx1.MarkFirstFullLoadDone();
        var svc1 = new GraphLayoutService(_host.Settings, locator);
        var res1 = await svc1.ComputeLayoutAsync(ctx1, Q);
        Assert.Equal(["C2", "C1"], Messages(res1));

        var gzPath = GraphCachePaths.File(repo.Path, ".json.gz");
        for (var i = 0; i < 50 && !File.Exists(gzPath); i++)
            await Task.Delay(50);
        Assert.True(File.Exists(gzPath), $"Expected walk cache file at {gzPath}");

        // Rewrite its Version header field to 4
        JsonObject node;
        using (var fs = File.OpenRead(gzPath))
        using (var gz = new GZipStream(fs, CompressionMode.Decompress))
        {
            node = (JsonObject)JsonNode.Parse(gz)!;
        }

        node["version"] = 4;

        using (var fs = File.Create(gzPath))
        using (var gz = new GZipStream(fs, CompressionLevel.Fastest))
        {
            using var writer = new System.Text.Json.Utf8JsonWriter(gz);
            node.WriteTo(writer);
        }

        // Direct TryLoad with v5 code must reject the v4 file as a miss
        var cache = new GraphWalkCache();
        var directHit = cache.TryLoad(repo.Path, Q, wantCount: 2, fingerprint: res1.Fingerprint, WalkEngine.Cli);
        Assert.Null(directHit);

        // Compute again through a fresh service instance: must encounter a cache miss, re-walk, and return date-ordered rows
        await using var ctx2 = _host.Open(repo.Path);
        ctx2.MarkFirstGraphPaintDone(); ctx2.MarkFirstFullLoadDone();
        var svc2 = new GraphLayoutService(_host.Settings, locator);
        var res2 = await svc2.ComputeLayoutAsync(ctx2, Q);

        Assert.Equal(["C2", "C1"], Messages(res2));
    }
}
