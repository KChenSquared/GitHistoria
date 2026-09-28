namespace Historia.Services.Git;

public sealed partial class GitGateway
{
    private sealed record HooksCacheEntry((long Mtime, long Length) Stamp, string HooksDir);
    private readonly SemaphoreSlim _hooksGate = new(1, 1);
    private HooksCacheEntry? _hooksCache;

    public async Task<RepositoryInfo> GetRepositoryInfoAsync(CancellationToken ct = default)
    {
        var snapshot = await context.RefSnapshots.GetAsync(ct).ConfigureAwait(false);
        var hooksDir = await GetHooksDirectoryAsync(ct).ConfigureAwait(false);
        return RepositoryInfoProjection.Project(snapshot, context, hooksDir);
    }

    public async Task<IReadOnlyList<BranchInfo>> GetBranchesAsync(CancellationToken ct = default)
    {
        var snapshot = await context.RefSnapshots.GetAsync(ct).ConfigureAwait(false);
        var remotes = await context.RemoteConfig.GetRemotesAsync(ct).ConfigureAwait(false);
        return BranchProjection.Project(snapshot, remotes);
    }

    /// <summary>From the repo's cached ref snapshot — one <c>git for-each-ref</c>, shared with
    /// the commit graph, and never the repo gate (LibGit2Sharp's tag enumeration held it for
    /// 9–13 s at chromium's 39k tags, queueing status behind it).</summary>
    public async Task<IReadOnlyList<TagInfo>> GetTagsAsync(CancellationToken ct = default)
    {
        var snapshot = await context.RefSnapshots.GetAsync(ct).ConfigureAwait(false);
        return TagProjection.FromSnapshot(snapshot);
    }

    public Task<IReadOnlyList<RemoteInfo>> GetRemotesAsync(CancellationToken ct = default)
        => context.RemoteConfig.GetRemotesAsync(ct);


    public async Task<AheadBehind?> GetDivergenceAsync(string local, string remote, CancellationToken ct = default)
    {
        var result = await cli.RunAsync(new GitCliRequest
        {
            Arguments = ["rev-list", "--left-right", "--count", $"{local}...{remote}"],
            WorkingDirectory = context.RepositoryPath,
        }, ct).ConfigureAwait(false);

        if (!result.IsSuccess)
            return null;

        var parts = result.StandardOutput.Trim().Split('\t', StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || !int.TryParse(parts[0], out var ahead) || !int.TryParse(parts[1], out var behind))
            return null;

        return new AheadBehind(ahead, behind);
    }

    private async Task<string?> GetHooksDirectoryAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var stamp = GetConfigStamp();
        var cached = Volatile.Read(ref _hooksCache);
        if (cached is not null && cached.Stamp == stamp)
            return cached.HooksDir;

        await _hooksGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            cached = Volatile.Read(ref _hooksCache);
            if (cached is not null && cached.Stamp == stamp)
                return cached.HooksDir;

            var res = await cli.RunAsync(new GitCliRequest
            {
                Arguments = ["rev-parse", "--git-path", "hooks"],
                WorkingDirectory = context.RepositoryPath,
            }, ct).ConfigureAwait(false);

            if (res.IsSuccess)
            {
                var outPath = res.StandardOutput.Trim();
                if (!string.IsNullOrEmpty(outPath))
                {
                    var resolved = Path.IsPathRooted(outPath)
                        ? Path.GetFullPath(outPath)
                        : Path.GetFullPath(Path.Combine(context.RepositoryPath, outPath));
                    Volatile.Write(ref _hooksCache, new HooksCacheEntry(stamp, resolved));
                    return resolved;
                }
            }

            var fallback = Path.Combine(context.GitDirectory, "hooks");
            Volatile.Write(ref _hooksCache, new HooksCacheEntry(stamp, fallback));
            return fallback;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Path.Combine(context.GitDirectory, "hooks");
        }
        finally
        {
            _hooksGate.Release();
        }
    }

    private (long Mtime, long Length) GetConfigStamp()
    {
        var commonDir = RefStateToken.ResolveCommonDir(context.GitDirectory);
        var configPath = Path.Combine(commonDir, "config");
        if (File.Exists(configPath))
        {
            var fi = new FileInfo(configPath);
            return (fi.LastWriteTimeUtc.Ticks, fi.Length);
        }
        return (0L, 0L);
    }
}
