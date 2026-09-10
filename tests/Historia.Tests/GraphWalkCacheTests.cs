namespace Historia.Tests;

/// <summary>§V10.1: the trunk-tip order sidecar the optimistic snapshot replays.</summary>
public sealed class GraphWalkCacheTests : IDisposable
{
    private readonly StateDir _state = new();
    private readonly GraphWalkCache _cache = new();
    private const long Fp = 0x0123_4567_89ab_cdef;
    private const string Repo = @"C:\repos\demo";

    public void Dispose() => _state.Dispose();

    private static GraphQuery AllRefs(bool remotes = true) =>
        new() { RefScope = GraphRefScope.AllRefs, IncludeRemoteBranches = remotes };

    private static string Sha(int n) => n.ToString("x40");

    [Fact]
    public void Tip_order_round_trips()
    {
        var order = new[] { Sha(3), Sha(1), Sha(2) };
        _cache.SaveTipOrder(Repo, AllRefs(), Fp, includeRemotes: true, order);

        var back = _cache.TryLoadTipOrder(Repo, includeRemotes: true);

        Assert.NotNull(back);
        Assert.Equal(order.Select(s => new ObjectId(s)), back);
    }

    [Fact]
    public void Tip_order_is_not_returned_when_the_remote_branch_flag_differs()
    {
        _cache.SaveTipOrder(Repo, AllRefs(remotes: true), Fp, includeRemotes: true, [Sha(1), Sha(2)]);

        Assert.Null(_cache.TryLoadTipOrder(Repo, includeRemotes: false));
        Assert.NotNull(_cache.TryLoadTipOrder(Repo, includeRemotes: true));
    }

    [Fact]
    public void Missing_sidecar_reads_as_null()
        => Assert.Null(_cache.TryLoadTipOrder(@"C:\repos\never-written", includeRemotes: true));

    [Fact]
    public void Empty_order_is_not_persisted()
    {
        _cache.SaveTipOrder(Repo, AllRefs(), Fp, includeRemotes: true, []);
        Assert.Null(_cache.TryLoadTipOrder(Repo, includeRemotes: true));
    }

    [Fact]
    public void A_path_filtered_query_never_writes_a_sidecar()
    {
        var filtered = new GraphQuery
        {
            RefScope = GraphRefScope.AllRefs,
            IncludeRemoteBranches = true,
            PathFilter = "src/",
        };
        _cache.SaveTipOrder(Repo, filtered, Fp, includeRemotes: true, [Sha(1), Sha(2)]);
        Assert.Null(_cache.TryLoadTipOrder(Repo, includeRemotes: true));
    }

    [Fact]
    public void WipeAll_removes_the_sidecar()
    {
        _cache.SaveTipOrder(Repo, AllRefs(), Fp, includeRemotes: true, [Sha(1), Sha(2)]);
        Assert.NotNull(_cache.TryLoadTipOrder(Repo, includeRemotes: true));

        _cache.WipeAll();

        Assert.Null(_cache.TryLoadTipOrder(Repo, includeRemotes: true));
    }
}
