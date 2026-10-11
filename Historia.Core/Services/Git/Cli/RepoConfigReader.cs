namespace Historia.Services.Git;

/// <summary>
/// Reads remote configurations and URL rewrite rules via Git CLI and caches them
/// against the common-dir <c>config</c> file's timestamp and length.
/// </summary>
public sealed class RepoConfigReader(IGitCliService cli, string gitDirectory, string workingDirectory)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _cwd = string.IsNullOrWhiteSpace(workingDirectory) ? gitDirectory : workingDirectory;

    private sealed record CacheEntry((long Mtime, long Length) Stamp, IReadOnlyList<RemoteInfo> Remotes);
    private CacheEntry? _cache;
    private int _readCount;

    /// <summary>Number of times git config was invoked — test seam for cache hit/miss assertions.</summary>
    internal int ReadCountForTesting => Volatile.Read(ref _readCount);

    public async Task<IReadOnlyList<RemoteInfo>> GetRemotesAsync(CancellationToken ct = default)
    {
        var stamp = GetConfigStamp();

        var cached = Volatile.Read(ref _cache);
        if (cached is not null && cached.Stamp == stamp)
            return cached.Remotes;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // The config may have changed while this call waited; a stamp taken before the gate
            // would miss the entry another reader just cached and cache a stale stamp in its place.
            stamp = GetConfigStamp();
            cached = Volatile.Read(ref _cache);
            if (cached is not null && cached.Stamp == stamp)
                return cached.Remotes;

            // Trap: include.path / global-scope remotes are only re-read when the repo config stamp changes.
            Interlocked.Increment(ref _readCount);

            var result = await cli.RunAsync(new GitCliRequest
            {
                Arguments = ["config", "-z", "--get-regexp", @"^(remote\..*\.(pushurl|url)|url\..*\.(pushinsteadof|insteadof))$"],
                WorkingDirectory = _cwd,
                RawStandardOutput = true,
            }, ct).ConfigureAwait(false);

            if (result.ExitCode == 1)
            {
                var empty = (IReadOnlyList<RemoteInfo>)[];
                Volatile.Write(ref _cache, new CacheEntry(stamp, empty));
                return empty;
            }

            if (!result.IsSuccess)
                throw HistoriaGitException.FromCli(result, "Read remotes config");

            var remotes = ParseRemotes(result.StandardOutput);
            Volatile.Write(ref _cache, new CacheEntry(stamp, remotes));
            return remotes;
        }
        finally
        {
            _gate.Release();
        }
    }

    private (long Mtime, long Length) GetConfigStamp()
    {
        var commonDir = ResolveCommonDir(gitDirectory);
        var configPath = Path.Combine(commonDir, "config");
        if (File.Exists(configPath))
        {
            var fi = new FileInfo(configPath);
            return (fi.LastWriteTimeUtc.Ticks, fi.Length);
        }
        return (0L, 0L);
    }

    internal static string ResolveCommonDir(string gitDir)
    {
        var commonDirFile = Path.Combine(gitDir, "commondir");
        if (File.Exists(commonDirFile))
        {
            try
            {
                var rel = File.ReadAllText(commonDirFile).Trim();
                if (!string.IsNullOrEmpty(rel))
                    return Path.IsPathRooted(rel) ? rel : Path.GetFullPath(Path.Combine(gitDir, rel));
            }
            catch
            {
                // Fall back to gitDir if unreadable.
            }
        }
        return gitDir;
    }

    internal static IReadOnlyList<RemoteInfo> ParseRemotes(string stdout)
    {
        var remotesOrder = new List<string>();
        var remoteUrls = new Dictionary<string, string>(StringComparer.Ordinal);
        var remotePushUrls = new Dictionary<string, string>(StringComparer.Ordinal);
        var insteadOfRules = new List<(string Replacement, string Prefix)>();
        var pushInsteadOfRules = new List<(string Replacement, string Prefix)>();

        var entries = stdout.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        foreach (var entry in entries)
        {
            var nl = entry.IndexOf('\n');
            if (nl < 0)
                continue;

            var key = entry[..nl].Trim();
            var val = entry[(nl + 1)..];

            if (key.StartsWith("remote.", StringComparison.OrdinalIgnoreCase))
            {
                if (key.EndsWith(".url", StringComparison.OrdinalIgnoreCase))
                {
                    var name = key.Substring(7, key.Length - 7 - 4);
                    if (!remoteUrls.ContainsKey(name))
                        remoteUrls[name] = val;
                    if (!remotesOrder.Contains(name, StringComparer.Ordinal))
                        remotesOrder.Add(name);
                }
                else if (key.EndsWith(".pushurl", StringComparison.OrdinalIgnoreCase))
                {
                    var name = key.Substring(7, key.Length - 7 - 8);
                    if (!remotePushUrls.ContainsKey(name))
                        remotePushUrls[name] = val;
                    if (!remotesOrder.Contains(name, StringComparer.Ordinal))
                        remotesOrder.Add(name);
                }
            }
            else if (key.StartsWith("url.", StringComparison.OrdinalIgnoreCase))
            {
                if (key.EndsWith(".insteadof", StringComparison.OrdinalIgnoreCase))
                {
                    var replacement = key.Substring(4, key.Length - 4 - 10);
                    insteadOfRules.Add((replacement, val));
                }
                else if (key.EndsWith(".pushinsteadof", StringComparison.OrdinalIgnoreCase))
                {
                    var replacement = key.Substring(4, key.Length - 4 - 14);
                    pushInsteadOfRules.Add((replacement, val));
                }
            }
        }

        var list = new List<RemoteInfo>(remotesOrder.Count);
        foreach (var name in remotesOrder)
        {
            remoteUrls.TryGetValue(name, out var rawUrl);
            remotePushUrls.TryGetValue(name, out var rawPushUrl);

            var effectiveUrl = rawUrl is not null ? RewriteUrl(rawUrl, insteadOfRules) : "";
            string effectivePushUrl;
            if (rawPushUrl is not null)
            {
                effectivePushUrl = RewriteUrl(rawPushUrl, insteadOfRules);
            }
            else if (rawUrl is not null)
            {
                var pushRewritten = RewriteUrl(rawUrl, pushInsteadOfRules);
                if (!string.Equals(pushRewritten, rawUrl, StringComparison.Ordinal))
                    effectivePushUrl = pushRewritten;
                else
                    effectivePushUrl = effectiveUrl;
            }
            else
            {
                effectivePushUrl = effectiveUrl;
            }

            list.Add(new RemoteInfo
            {
                Name = name,
                Url = effectiveUrl,
                PushUrl = effectivePushUrl,
            });
        }

        return list;
    }

    private static string RewriteUrl(string url, List<(string Replacement, string Prefix)> rules)
    {
        (string Replacement, string Prefix)? best = null;
        foreach (var rule in rules)
        {
            if (url.StartsWith(rule.Prefix, StringComparison.Ordinal))
            {
                if (best is null || rule.Prefix.Length > best.Value.Prefix.Length)
                    best = rule;
            }
        }

        if (best is { } b)
            return b.Replacement + url[b.Prefix.Length..];

        return url;
    }
}
