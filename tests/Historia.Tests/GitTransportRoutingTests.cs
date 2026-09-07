using Historia.Models;
using Historia.Services.Network;
using Xunit;

namespace Historia.Tests;

/// <summary>§13.1: scheme-first transport routing with a fallback to the other transport,
/// unless SSH Only forces SSH with none.</summary>
public class GitTransportRoutingTests
{
    [Fact]
    public void Https_remote_tries_https_then_ssh_when_ssh_only_off()
        => Assert.Equal(
            new[] { RouteTransport.Https, RouteTransport.Ssh },
            GitTransportRouting.Plan(RemoteUrlKind.Https, sshOnly: false));

    [Theory]
    [InlineData(RemoteUrlKind.ScpLike)]
    [InlineData(RemoteUrlKind.SshUrl)]
    public void Ssh_remote_tries_ssh_then_https_when_ssh_only_off(RemoteUrlKind kind)
        => Assert.Equal(
            new[] { RouteTransport.Ssh, RouteTransport.Https },
            GitTransportRouting.Plan(kind, sshOnly: false));

    [Theory]
    [InlineData(RemoteUrlKind.Https)]
    [InlineData(RemoteUrlKind.ScpLike)]
    [InlineData(RemoteUrlKind.SshUrl)]
    public void Ssh_only_forces_ssh_with_no_fallback(RemoteUrlKind kind)
        => Assert.Equal(
            new[] { RouteTransport.Ssh },
            GitTransportRouting.Plan(kind, sshOnly: true));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Local_or_other_is_run_as_is_with_no_conversion_or_fallback(bool sshOnly)
        => Assert.Equal(
            new[] { RouteTransport.AsIs },
            GitTransportRouting.Plan(RemoteUrlKind.LocalOrOther, sshOnly));
}
