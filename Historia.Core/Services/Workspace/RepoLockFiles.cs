namespace Historia.Services.Workspace;

/// <summary>A lock file git left in the repository, old enough to be offered for removal.</summary>
public sealed record StaleLockFile(string FullPath, string RelativePath, DateTime LastWriteUtc);

/// <summary>What <see cref="RepoLockFiles.RemoveStaleLocksAsync"/> did. <see cref="Busy"/> means
/// nothing was touched because another network operation (<see cref="BusyWith"/>, when still
/// known) holds the repository.</summary>
public sealed record LockRemovalResult(int Removed, int Skipped, bool Busy, NetworkOperationInfo? BusyWith);

/// <summary>
/// Finds and removes stale lock files — the <c>*.lock</c> files git creates while it updates a
/// ref, the index or the config, and leaves behind when its process dies mid-update. Until they
/// are gone every fetch, pull or commit that needs the same file fails with "another git process
/// seems to be running", and git has no command that clears them. Only these qualify: any
/// <c>*.lock</c> under <c>refs/</c>, plus <c>packed-refs.lock</c>, <c>config.lock</c> and
/// <c>shallow.lock</c> in the common directory and <c>index.lock</c> and <c>HEAD.lock</c> in the
/// git directory; and only once <see cref="MinAge"/> old, since a younger one may still belong to
/// a git command that is running right now. Removal is destructive: callers confirm first.
/// </summary>
public static class RepoLockFiles
{
    /// <summary>A lock younger than this is assumed to belong to a running git command.</summary>
    public static readonly TimeSpan MinAge = TimeSpan.FromMinutes(5);

    private static readonly string[] CommonDirLocks = ["packed-refs.lock", "config.lock", "shallow.lock"];
    private static readonly string[] GitDirLocks = ["index.lock", "HEAD.lock"];

    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>The qualifying lock files at least <see cref="MinAge"/> old at
    /// <paramref name="nowUtc"/>, sorted by relative path. Read-only; never throws for an
    /// unreadable directory or a file that vanishes mid-scan.</summary>
    public static IReadOnlyList<StaleLockFile> Find(RepositoryContext ctx, DateTime nowUtc)
    {
        var found = new List<StaleLockFile>();
        foreach (var (full, rel) in Candidates(ctx))
        {
            if (TryLastWriteUtc(full) is { } written && nowUtc - written >= MinAge)
                found.Add(new StaleLockFile(full, rel, written));
        }
        found.Sort((a, b) => string.CompareOrdinal(a.RelativePath, b.RelativePath));
        return found;
    }

    /// <summary>
    /// Deletes <paramref name="files"/> (from <see cref="Find"/>) while holding the repository's
    /// network gate and write lock, so none of Historia's own git operations can be using them.
    /// Each file is checked again just before it goes: it must still be a qualifying lock of this
    /// repository, unchanged since it was found, and still <see cref="MinAge"/> old — otherwise it
    /// is skipped. Returns at once, touching nothing, when another network operation holds the
    /// repository.
    /// </summary>
    public static async Task<LockRemovalResult> RemoveStaleLocksAsync(
        RepositoryContext ctx, IReadOnlyList<StaleLockFile> files, CancellationToken ct = default)
    {
        if (!ctx.TryBeginNetworkOperation(new NetworkOperationInfo(NetworkOperationOwner.Maintenance, "Lock cleanup"), out var lease))
            return new LockRemovalResult(0, files.Count, Busy: true, ctx.CurrentNetworkOperation);

        using (lease)
        using (ctx.BeginSelfOperation())
        {
            return await ctx.WithWriteLockAsync(_ =>
            {
                var qualifying = Candidates(ctx).Select(c => Path.GetFullPath(c.Full)).ToHashSet(PathComparer);
                var now = DateTime.UtcNow;
                int removed = 0, skipped = 0;
                foreach (var file in files)
                {
                    var full = Path.GetFullPath(file.FullPath);
                    if (!qualifying.Contains(full)
                        || TryLastWriteUtc(full) is not { } written
                        || written != file.LastWriteUtc
                        || now - written < MinAge)
                    {
                        skipped++;
                        continue;
                    }

                    try
                    {
                        File.Delete(full);
                        removed++;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        skipped++;
                    }
                }
                return Task.FromResult(new LockRemovalResult(removed, skipped, Busy: false, null));
            }, ct, label: "RepoLockFiles.Remove").ConfigureAwait(false);
        }
    }

    /// <summary>Every qualifying lock file that exists now, with its path relative to the
    /// directory that holds it (forward slashes).</summary>
    private static List<(string Full, string Rel)> Candidates(RepositoryContext ctx)
    {
        var common = RefStateToken.ResolveCommonDir(ctx.GitDirectory);
        var result = new List<(string, string)>();

        foreach (var name in CommonDirLocks)
            AddIfExists(result, common, name);
        foreach (var name in GitDirLocks)
            AddIfExists(result, ctx.GitDirectory, name);

        var refs = Path.Combine(common, "refs");
        if (Directory.Exists(refs))
        {
            try
            {
                var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
                foreach (var file in Directory.EnumerateFiles(refs, "*.lock", options))
                    result.Add((file, Path.GetRelativePath(common, file).Replace('\\', '/')));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Keep what was read before the directory went away.
            }
        }
        return result;
    }

    private static void AddIfExists(List<(string, string)> into, string dir, string name)
    {
        var path = Path.Combine(dir, name);
        if (File.Exists(path))
            into.Add((path, name));
    }

    private static DateTime? TryLastWriteUtc(string path)
    {
        try
        {
            return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
