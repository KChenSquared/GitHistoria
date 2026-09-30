namespace Historia.Services.Network;

/// <summary>
/// Maps a remote's fetch URL to the web page a browser can open for it. No network probe is
/// made: an SSH or scp-like remote is assumed to serve its web page over https, so the result is
/// a best guess that is only ever opened by an explicit user click.
/// </summary>
public static class RemoteWebUrl
{
    /// <summary>
    /// Builds the web page URL for <paramref name="url"/>. Userinfo, an SSH port, a trailing
    /// slash and a trailing <c>.git</c> are dropped; a non-default http(s) port is kept. Returns
    /// <see langword="false"/> for a local path, a <c>file://</c> or <c>git://</c> URL, or
    /// anything that does not parse.
    /// </summary>
    public static bool TryGet(string url, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? webUrl)
    {
        webUrl = null;

        var candidate = RemoteUrlConverter.Classify(url) switch
        {
            RemoteUrlKind.Https => url,
            RemoteUrlKind.ScpLike or RemoteUrlKind.SshUrl => RemoteUrlConverter.ToHttps(url).ConvertedUrl,
            _ => null,
        };

        if (candidate is null
            || !Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https")
            || string.IsNullOrEmpty(uri.Host))
            return false;

        var path = uri.AbsolutePath.TrimEnd('/');
        if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            path = path[..^".git".Length].TrimEnd('/');

        var port = uri.IsDefaultPort ? string.Empty : $":{uri.Port}";
        webUrl = $"{uri.Scheme}://{uri.Host}{port}{path}";
        return true;
    }
}
