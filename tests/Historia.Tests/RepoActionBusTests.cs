namespace Historia.Tests;

/// <summary><c>IRepoActionBus</c> — the un-targeted overload keeps working for the
/// shortcuts bar's three existing call sites, and a targeted request round-trips its
/// remote/branch.</summary>
[Trait("tier", "unit")]
public sealed class RepoActionBusTests
{
    [Fact]
    public void Untargeted_overload_raises_a_request_with_null_remote_and_branch()
    {
        var bus = new RepoActionBus();
        RepoNetworkRequest? received = null;
        bus.NetworkActionRequested += (_, r) => received = r;

        bus.RequestNetworkAction(RepoNetworkAction.Pull);

        Assert.NotNull(received);
        Assert.Equal(RepoNetworkAction.Pull, received!.Value.Action);
        Assert.Null(received.Value.Remote);
        Assert.Null(received.Value.Branch);
    }

    [Fact]
    public void Targeted_overload_round_trips_remote_and_branch()
    {
        var bus = new RepoActionBus();
        RepoNetworkRequest? received = null;
        bus.NetworkActionRequested += (_, r) => received = r;

        bus.RequestNetworkAction(new RepoNetworkRequest(RepoNetworkAction.Push, "upstream", "feature-a"));

        Assert.NotNull(received);
        Assert.Equal(RepoNetworkAction.Push, received!.Value.Action);
        Assert.Equal("upstream", received.Value.Remote);
        Assert.Equal("feature-a", received.Value.Branch);
        Assert.Null(received.Value.RemoteBranch);
    }

    [Fact]
    public void Targeted_overload_round_trips_remote_branch()
    {
        var bus = new RepoActionBus();
        RepoNetworkRequest? received = null;
        bus.NetworkActionRequested += (_, r) => received = r;

        bus.RequestNetworkAction(new RepoNetworkRequest(RepoNetworkAction.Push, "upstream", "feature-a", "feat-x"));

        Assert.NotNull(received);
        Assert.Equal(RepoNetworkAction.Push, received!.Value.Action);
        Assert.Equal("upstream", received.Value.Remote);
        Assert.Equal("feature-a", received.Value.Branch);
        Assert.Equal("feat-x", received.Value.RemoteBranch);
    }
}
