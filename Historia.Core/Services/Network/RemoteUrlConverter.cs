namespace Historia.Services.Network;

/// <summary>
/// Parse-based (never regex-substitution) bidirectional remote URL conversion (§6.14.1).
/// Round-tripping is lossless for clean URLs; non-default SSH ports keep the <c>ssh://</c>
/// form; inline HTTPS credentials are stripped and never written back to config.
/// </summary>
public static partial class RemoteUrlConverter
{
    private sealed record Parsed(
        RemoteUrlKind Kind,
        string? User,
        string Host,
        int? Port,
        string Path,
        bool HadCredentials);

    public static RemoteUrlKind Classify(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return RemoteUrlKind.LocalOrOther;
        if (url.StartsWith("file://", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("git://", StringComparison.OrdinalIgnoreCase) ||
            WindowsPathRegex().IsMatch(url) ||
            (!url.Contains("://") && !ScpLikeRegex().IsMatch(url)))
            return RemoteUrlKind.LocalOrOther;
        if (url.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase))
            return RemoteUrlKind.SshUrl;
        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return RemoteUrlKind.Https;
        if (ScpLikeRegex().IsMatch(url))
            return RemoteUrlKind.ScpLike;
        return RemoteUrlKind.LocalOrOther;
    }

    /// <summary>Converts to an SSH form (SCP-like for the default port, <c>ssh://</c> otherwise).</summary>
    public static RemoteUrlConversion ToSsh(string url)
    {
        var kind = Classify(url);
        if (kind is RemoteUrlKind.LocalOrOther)
            return RemoteUrlConversion.Skipped(url, "local or unsupported scheme");
        if (kind is RemoteUrlKind.ScpLike or RemoteUrlKind.SshUrl)
            return new RemoteUrlConversion(url, url, false, "already SSH");

        var p = Parse(url, kind);
        var user = string.IsNullOrEmpty(p.User) ? "git" : p.User;
        string converted;
        if (p.Port is null or 22)
            converted = $"{user}@{p.Host}:{p.Path}";
        else
            converted = $"ssh://{user}@{p.Host}:{p.Port}/{p.Path}";

        return new RemoteUrlConversion(url, converted, true, null) { HadEmbeddedCredentials = p.HadCredentials };
    }

    /// <summary>Converts to an HTTPS URL. Non-default SSH ports are dropped (no HTTPS equivalent).</summary>
    public static RemoteUrlConversion ToHttps(string url)
    {
        var kind = Classify(url);
        if (kind is RemoteUrlKind.LocalOrOther)
            return RemoteUrlConversion.Skipped(url, "local or unsupported scheme");
        if (kind is RemoteUrlKind.Https)
            return new RemoteUrlConversion(url, url, false, "already HTTPS");

        var p = Parse(url, kind);
        var converted = $"https://{p.Host}/{p.Path}";
        return new RemoteUrlConversion(url, converted, true, null);
    }

    private static Parsed Parse(string url, RemoteUrlKind kind) => kind switch
    {
        RemoteUrlKind.ScpLike => ParseScp(url),
        RemoteUrlKind.SshUrl => ParseAuthority(url["ssh://".Length..], RemoteUrlKind.SshUrl),
        RemoteUrlKind.Https => ParseAuthority(url[(url.IndexOf("://", StringComparison.Ordinal) + 3)..], RemoteUrlKind.Https),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static Parsed ParseScp(string url)
    {
        var m = ScpLikeRegex().Match(url);
        return new Parsed(RemoteUrlKind.ScpLike, m.Groups["user"].Value, m.Groups["host"].Value, null,
            m.Groups["path"].Value.TrimStart('/'), HadCredentials: false);
    }

    private static Parsed ParseAuthority(string rest, RemoteUrlKind kind)
    {
        string? user = null;
        var hadCreds = false;
        var atIdx = rest.IndexOf('@');
        var slashIdx = rest.IndexOf('/');
        if (atIdx > 0 && (slashIdx < 0 || atIdx < slashIdx))
        {
            var userinfo = rest[..atIdx];
            rest = rest[(atIdx + 1)..];
            var colon = userinfo.IndexOf(':');
            if (colon >= 0)
            {
                user = userinfo[..colon];
                hadCreds = true; // password present — strip it, never write it back (§6.14.1)
            }
            else
            {
                user = userinfo;
            }
        }

        slashIdx = rest.IndexOf('/');
        var authority = slashIdx < 0 ? rest : rest[..slashIdx];
        var path = slashIdx < 0 ? string.Empty : rest[(slashIdx + 1)..];

        int? port = null;
        string host;
        if (authority.StartsWith('['))
        {
            // Bracketed IPv6 literal — the host keeps its brackets, the port (if any) is
            // after the closing bracket. A LastIndexOf(':') here would split the address.
            var close = authority.IndexOf(']');
            if (close < 0)
            {
                host = authority;
            }
            else
            {
                host = authority[..(close + 1)];
                var afterBracket = authority[(close + 1)..];
                if (afterBracket.StartsWith(':') && int.TryParse(afterBracket[1..], out var bracketPort))
                    port = bracketPort;
            }
        }
        else
        {
            host = authority;
            var portColon = authority.LastIndexOf(':');
            if (portColon >= 0 && int.TryParse(authority[(portColon + 1)..], out var parsedPort))
            {
                host = authority[..portColon];
                port = parsedPort;
            }
        }

        return new Parsed(kind, user, host, port, path.TrimStart('/'), hadCreds);
    }

    [GeneratedRegex(@"^(?<user>[^@/\s]+)@(?<host>[^:/\s]+):(?<path>(?!/).+)$", RegexOptions.CultureInvariant)]
    private static partial Regex ScpLikeRegex();

    [GeneratedRegex(@"^[A-Za-z]:[\\/]", RegexOptions.CultureInvariant)]
    private static partial Regex WindowsPathRegex();
}
