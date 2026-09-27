namespace Historia.Models;

/// <summary>Result of converting a remote URL for the current network mode.</summary>
public sealed record RemoteUrlConversion(
    string OriginalUrl,
    string? ConvertedUrl,
    bool WasConverted,
    string? SkipReason)
{
    /// <summary>True when the URL carried inline credentials that were stripped.</summary>
    public bool HadEmbeddedCredentials { get; init; }

    public static RemoteUrlConversion Skipped(string url, string reason) => new(url, null, false, reason);
}

/// <summary>The parsed shape of a Git remote URL.</summary>
public enum RemoteUrlKind
{
    ScpLike,        // git@github.com:owner/repo.git — colon separates host from path, not a port
    SshUrl,         // ssh://git@host:2222/owner/repo.git — colon is a port
    Https,          // https://host/owner/repo.git — may carry a port and userinfo
    LocalOrOther,   // file://…, C:\path, git://… — never converted
}
