namespace Historia.Tests;

/// <summary>The per-repository network gate: one fetch, pull, push, prune or lock cleanup at a
/// time, with the holder visible to anyone who is turned away.</summary>
[Trait("tier", "git")]
public sealed class RepositoryNetworkGateTests : IAsyncDisposable
{
    private readonly MiniHost _host = new();
    private TestRepositoryBuilder? _repo;

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
        _repo?.Dispose();
    }

    private RepositoryContext Open()
    {
        _repo = TestRepositoryBuilder.FromTemplate("network-gate-seed", b =>
        {
            b.WriteFile("a.txt", "x\n").Stage();
            b.Commit("c1");
        });
        return _host.Open(_repo.Path);
    }

    private static readonly NetworkOperationInfo Fetch = new(NetworkOperationOwner.User, "Fetch");
    private static readonly NetworkOperationInfo Auto = new(NetworkOperationOwner.AutoFetch, "Auto-fetch");

    [Fact]
    public void A_held_gate_turns_others_away_and_names_its_holder()
    {
        var ctx = Open();
        Assert.Null(ctx.CurrentNetworkOperation);

        Assert.True(ctx.TryBeginNetworkOperation(Fetch, out var lease));
        Assert.Same(Fetch, ctx.CurrentNetworkOperation);

        Assert.False(ctx.TryBeginNetworkOperation(Auto, out var refused));
        Assert.Null(refused);
        Assert.Same(Fetch, ctx.CurrentNetworkOperation);

        lease.Dispose();
        Assert.Null(ctx.CurrentNetworkOperation);
        Assert.True(ctx.TryBeginNetworkOperation(Auto, out var again));
        again.Dispose();
    }

    [Fact]
    public void Disposing_a_lease_twice_releases_once()
    {
        var ctx = Open();
        Assert.True(ctx.TryBeginNetworkOperation(Fetch, out var first));
        first.Dispose();
        Assert.True(ctx.TryBeginNetworkOperation(Auto, out var second));

        first.Dispose();

        Assert.Same(Auto, ctx.CurrentNetworkOperation);
        Assert.False(ctx.TryBeginNetworkOperation(Fetch, out _));
        second.Dispose();
    }

    [Fact(Timeout = 30000)]
    public async Task A_waiter_enters_when_the_holder_releases()
    {
        var ctx = Open();
        Assert.True(ctx.TryBeginNetworkOperation(Auto, out var held));

        var waiting = ctx.BeginNetworkOperationAsync(Fetch, CancellationToken.None);
        await Task.Delay(50);
        Assert.False(waiting.IsCompleted);

        held.Dispose();
        using var lease = await waiting;
        Assert.Same(Fetch, ctx.CurrentNetworkOperation);
    }

    [Fact(Timeout = 30000)]
    public async Task Cancelling_a_wait_leaves_the_holder_in_place()
    {
        var ctx = Open();
        Assert.True(ctx.TryBeginNetworkOperation(Auto, out var held));

        using var cts = new CancellationTokenSource();
        var waiting = ctx.BeginNetworkOperationAsync(Fetch, cts.Token);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.Same(Auto, ctx.CurrentNetworkOperation);

        held.Dispose();
        Assert.True(ctx.TryBeginNetworkOperation(Fetch, out var next));
        next.Dispose();
    }
}
