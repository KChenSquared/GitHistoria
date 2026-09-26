namespace Historia.Tests;

/// <summary>
/// A ref that points at a missing object makes <c>git for-each-ref</c> stop
/// (<c>fatal: missing object … for refs/…</c>). The ref snapshot then reads the other refs another
/// way and leaves the broken ones out — as LibGit2Sharp-based reads always did — so the graph and
/// the tag list keep working. The entries it builds must be exactly what the normal scan gives,
/// or the walk key and the tag list would drift on such a repo. A failure no missing object
/// explains still throws.
/// </summary>
[Trait("tier", "git")]
public sealed class RefSnapshotBrokenRefTests : IAsyncDisposable
{
    private const string MissingObject = "1111111111111111111111111111111111111111";

    private readonly MiniHost _host = new();
    private readonly TestRepositoryBuilder _repo;

    public RefSnapshotBrokenRefTests()
    {
        _repo = TestRepositoryBuilder.FromTemplate("ref-shapes", RefSnapshotProviderTests.BuildRefShapes);
    }

    public async ValueTask DisposeAsync()
    {
        _repo.Dispose();
        await _host.DisposeAsync();
    }

    /// <summary>A loose branch and a packed tag, both naming an object that doesn't exist. The
    /// packed one goes in with the header's <c>sorted</c> trait dropped, so git sorts the file
    /// itself instead of trusting its order.</summary>
    private void AddBrokenRefs()
    {
        File.WriteAllText(Path.Combine(_repo.Path, ".git", "refs", "heads", "broken"), MissingObject + "\n");

        var packed = Path.Combine(_repo.Path, ".git", "packed-refs");
        var lines = File.ReadAllLines(packed).ToList();
        lines[0] = lines[0].Replace(" sorted", "", StringComparison.Ordinal);
        lines.Add($"{MissingObject} refs/tags/broken-tag");
        File.WriteAllText(packed, string.Join('\n', lines) + "\n"); // LF: git rejects a CR in packed-refs
    }

    private static readonly string[] BrokenNames = ["refs/heads/broken", "refs/tags/broken-tag"];

    [Fact]
    public async Task Degraded_scan_matches_the_normal_scan_minus_the_broken_refs()
    {
        await using var ctx = _host.Open(_repo.Path);
        var normal = await ctx.RefSnapshots.GetAsync();

        AddBrokenRefs();
        var degraded = await ctx.RefSnapshots.GetAsync();

        Assert.NotSame(normal, degraded);
        Assert.DoesNotContain(degraded.Refs, r => BrokenNames.Contains(r.CanonicalName));
        Assert.Equal(normal.Refs, degraded.Refs);
        Assert.Equal(
            normal.Refs.Select(r => r.TaggerWhen?.Offset),
            degraded.Refs.Select(r => r.TaggerWhen?.Offset));
        Assert.Equal(normal.HeadSymref, degraded.HeadSymref);
        Assert.Equal(normal.HeadTarget, degraded.HeadTarget);
    }

    [Fact]
    public async Task Tags_list_without_the_broken_tag()
    {
        await using var ctx = _host.Open(_repo.Path);
        var healthy = await ctx.Gateway.GetTagsAsync();

        AddBrokenRefs();
        var actual = await ctx.Gateway.GetTagsAsync();

        Assert.DoesNotContain(actual, t => t.Name == "broken-tag");
        Assert.Equal(healthy, actual);
    }

    private static async Task<long> WalkKeyAsync(RepositoryContext ctx, GraphQuery query)
    {
        var method = typeof(GraphLayoutService).GetMethod("TryGetRefStateAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
        var task = (Task)method.Invoke(null, [ctx, query, false, CancellationToken.None])!;
        await task;
        var state = (System.Runtime.CompilerServices.ITuple)task.GetType().GetProperty("Result")!.GetValue(task)!;
        return (long)state[3]!;
    }

    private static List<ObjectId> Ids(GraphLayoutResult r) =>
        Enumerable.Range(0, r.Layout.RowCount).Select(i => r.Layout.Commits[i].Id).ToList();

    [Fact]
    public async Task Graph_walks_with_a_broken_ref()
    {
        if (!_host.Locator.Current.Found)
            return; // git.exe not discoverable — the CLI walk never engages
        var query = new GraphQuery { RefScope = GraphRefScope.AllRefs };

        List<ObjectId> before;
        long keyBefore;
        await using (var ctx = _host.Open(_repo.Path))
        {
            ctx.MarkFirstGraphPaintDone(); ctx.MarkFirstFullLoadDone();
            before = Ids(await new GraphLayoutService(_host.Settings, _host.Locator).ComputeLayoutAsync(ctx, query));
            keyBefore = await WalkKeyAsync(ctx, query);
        }

        AddBrokenRefs();
        await using var broken = _host.Open(_repo.Path);
        broken.MarkFirstGraphPaintDone(); broken.MarkFirstFullLoadDone();
        var after = await new GraphLayoutService(_host.Settings, _host.Locator).ComputeLayoutAsync(broken, query);

        Assert.NotEmpty(before);
        Assert.Equal(before, Ids(after));
        Assert.Equal(keyBefore, await WalkKeyAsync(broken, query));
    }

    [Fact]
    public async Task An_unexplained_failure_still_throws()
    {
        // Every ref lives in packed-refs now, and a line git can't parse stops even the
        // object-free listing — no missing object explains it.
        _repo.Git("pack-refs", "--all");
        File.AppendAllText(Path.Combine(_repo.Path, ".git", "packed-refs"), "garbage line\n");

        await using var ctx = _host.Open(_repo.Path);
        var ex = await Assert.ThrowsAsync<HistoriaGitException>(() => ctx.RefSnapshots.GetAsync());
        Assert.Contains("unexpected line", ex.Details);
    }
}
