namespace Historia.Services.Network;

/// <summary>One transport attempt in a routing plan.</summary>
public enum RouteTransport
{
    /// <summary>Run over SSH. The effective URL is the SSH form (converted if the stored URL is HTTPS).</summary>
    Ssh,

    /// <summary>Run over HTTPS. The effective URL is the <c>https://</c> form (converted if the stored URL is SSH).</summary>
    Https,

    /// <summary>Run against the stored URL unchanged — local path / <c>git://</c> / unknown scheme.</summary>
    AsIs,
}

/// <summary>
/// Pure, table-driven decision for how a fetch / push / pull is routed. The URL's own
/// scheme decides which transport is tried first; the other is the fallback, unless
/// <c>SSH Only</c> forces SSH with no fallback.
/// </summary>
public static class GitTransportRouting
{
    /// <summary>
    /// The ordered transport attempts for a remote of the given <paramref name="urlKind"/> with
    /// the repo's <paramref name="sshOnly"/> toggle. Never empty.
    /// </summary>
    public static IReadOnlyList<RouteTransport> Plan(RemoteUrlKind urlKind, bool sshOnly)
    {
        if (urlKind is RemoteUrlKind.LocalOrOther)
            return [RouteTransport.AsIs];

        if (sshOnly)
            return [RouteTransport.Ssh];

        return urlKind switch
        {
            RemoteUrlKind.Https => [RouteTransport.Https, RouteTransport.Ssh],
            RemoteUrlKind.ScpLike or RemoteUrlKind.SshUrl => [RouteTransport.Ssh, RouteTransport.Https],
            _ => [RouteTransport.AsIs],
        };
    }
}
