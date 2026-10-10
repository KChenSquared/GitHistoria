namespace Historia.Services.Git;

/// <summary>
/// Detects index paths that differ only in letter case and share a single on-disk file on
/// case-insensitive filesystems, caching collision groups and worktree hashes against index and
/// config timestamps.
/// </summary>
internal sealed class IndexCaseCollisions(IGitCliService cli, string gitDirectory, string workingDirectory)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _cwd = string.IsNullOrWhiteSpace(workingDirectory) ? gitDirectory : workingDirectory;

    private sealed record IgnoreCaseCache((long Mtime, long Length) Stamp, bool Value);
    private IgnoreCaseCache? _ignoreCaseCache;

    private sealed record GroupsCache(
        (long Mtime, long Length) Stamp,
        IReadOnlyDictionary<string, IReadOnlyList<(string Path, string BlobId)>> Groups);
    private GroupsCache? _groupsCache;

    private readonly Dictionary<string, (long Length, long Mtime, string Hash)> _worktreeHashCache = new(StringComparer.Ordinal);

    public async Task<bool> IgnoreCaseAsync(CancellationToken ct = default)
    {
        var stamp = GetConfigStamp();

        var cached = Volatile.Read(ref _ignoreCaseCache);
        if (cached is not null && cached.Stamp == stamp)
            return cached.Value;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            cached = Volatile.Read(ref _ignoreCaseCache);
            if (cached is not null && cached.Stamp == stamp)
                return cached.Value;

            var result = await cli.RunAsync(new GitCliRequest
            {
                Arguments = ["config", "--type=bool", "--get", "core.ignorecase"],
                WorkingDirectory = _cwd,
            }, ct).ConfigureAwait(false);

            bool ignoreCase;
            if (result.IsSuccess)
            {
                ignoreCase = string.Equals(result.StandardOutput.Trim(), "true", StringComparison.OrdinalIgnoreCase);
            }
            else if (result.ExitCode == 1 && result.ErrorKind is not (GitErrorKind.Cancelled or GitErrorKind.TimedOut))
            {
                ignoreCase = false;
            }
            else
            {
                throw HistoriaGitException.FromCli(result, "Read core.ignorecase config");
            }

            Volatile.Write(ref _ignoreCaseCache, new IgnoreCaseCache(stamp, ignoreCase));
            return ignoreCase;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<(string Path, string BlobId)>>> GroupsAsync(CancellationToken ct = default)
    {
        var stamp = GetIndexStamp();
        if (stamp == (0L, 0L))
            return ImmutableDictionary<string, IReadOnlyList<(string Path, string BlobId)>>.Empty;

        var cached = Volatile.Read(ref _groupsCache);
        if (cached is not null && cached.Stamp == stamp)
            return cached.Groups;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            cached = Volatile.Read(ref _groupsCache);
            if (cached is not null && cached.Stamp == stamp)
                return cached.Groups;

            var sw = Stopwatch.StartNew();
            var result = await cli.RunAsync(new GitCliRequest
            {
                Arguments = ["ls-files", "-s", "-z"],
                WorkingDirectory = _cwd,
                RawStandardOutput = true,
                Timeout = Timeout.InfiniteTimeSpan,
            }, ct).ConfigureAwait(false);

            if (!result.IsSuccess)
                throw HistoriaGitException.FromCli(result, "List index entries");

            var groups = ParseGroups(result.StandardOutput, out var totalEntries, out var groupCount);
            sw.Stop();
            PerfLog.Mark($"IndexCaseCollisions.GroupsAsync: {sw.ElapsedMilliseconds} ms, {totalEntries} entries, {groupCount} groups");

            Volatile.Write(ref _groupsCache, new GroupsCache(stamp, groups));
            return groups;
        }
        finally
        {
            _gate.Release();
        }
    }

    internal static IReadOnlyDictionary<string, IReadOnlyList<(string Path, string BlobId)>> ParseGroups(string lsFilesStageOutput)
        => ParseGroups(lsFilesStageOutput, out _, out _);

    internal static IReadOnlyDictionary<string, IReadOnlyList<(string Path, string BlobId)>> ParseGroups(
        string lsFilesStageOutput,
        out int totalEntries,
        out int groupCount)
    {
        totalEntries = 0;
        groupCount = 0;

        if (string.IsNullOrEmpty(lsFilesStageOutput))
            return ImmutableDictionary<string, IReadOnlyList<(string Path, string BlobId)>>.Empty;

        var entries = lsFilesStageOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        totalEntries = entries.Length;

        var caseGroups = new Dictionary<string, List<(string Path, string BlobId)>>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries)
        {
            var tabIndex = entry.IndexOf('\t');
            if (tabIndex < 0)
                continue;

            var meta = entry[..tabIndex];
            var path = entry[(tabIndex + 1)..];

            var space1 = meta.IndexOf(' ');
            if (space1 < 0)
                continue;
            var space2 = meta.IndexOf(' ', space1 + 1);
            if (space2 < 0)
                continue;

            var oid = meta.Substring(space1 + 1, space2 - space1 - 1);
            var stage = meta[(space2 + 1)..];

            if (stage != "0")
                continue;

            if (!caseGroups.TryGetValue(path, out var list))
            {
                list = [];
                caseGroups[path] = list;
            }
            list.Add((path, oid));
        }

        var result = new Dictionary<string, IReadOnlyList<(string Path, string BlobId)>>(StringComparer.Ordinal);

        foreach (var group in caseGroups.Values)
        {
            if (group.Count < 2)
                continue;

            groupCount++;

            for (var i = 0; i < group.Count; i++)
            {
                var member = group[i];
                var peers = new List<(string Path, string BlobId)>(group.Count - 1);
                for (var j = 0; j < group.Count; j++)
                {
                    if (i != j)
                    {
                        peers.Add(group[j]);
                    }
                }
                result[member.Path] = peers;
            }
        }

        return result;
    }

    public async Task<IReadOnlyDictionary<string, string>> HashWorktreeAsync(IReadOnlyList<string> paths, CancellationToken ct = default)
    {
        if (paths.Count == 0)
            return ImmutableDictionary<string, string>.Empty;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var candidateSet = new HashSet<string>(paths, StringComparer.Ordinal);
            var toRemove = new List<string>();
            foreach (var cachedPath in _worktreeHashCache.Keys)
            {
                if (!candidateSet.Contains(cachedPath))
                    toRemove.Add(cachedPath);
            }
            foreach (var p in toRemove)
                _worktreeHashCache.Remove(p);

            var toHash = new List<(string Path, long Length, long Mtime)>();
            foreach (var path in paths)
            {
                var fullPath = Path.Combine(_cwd, path.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(fullPath))
                    continue;

                var fi = new FileInfo(fullPath);
                var length = fi.Length;
                var mtime = fi.LastWriteTimeUtc.Ticks;

                if (_worktreeHashCache.TryGetValue(path, out var cached)
                    && cached.Length == length
                    && cached.Mtime == mtime)
                {
                    continue;
                }

                toHash.Add((path, length, mtime));
            }

            if (toHash.Count > 0)
            {
                var stdin = string.Join("\n", toHash.Select(x => x.Path)) + "\n";
                var result = await cli.RunAsync(new GitCliRequest
                {
                    Arguments = ["hash-object", "--stdin-paths"],
                    WorkingDirectory = _cwd,
                    StandardInput = stdin,
                    Timeout = Timeout.InfiniteTimeSpan,
                }, ct).ConfigureAwait(false);

                if (result.IsSuccess)
                {
                    var lines = result.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
                    if (lines.Length == toHash.Count)
                    {
                        for (var i = 0; i < toHash.Count; i++)
                        {
                            var item = toHash[i];
                            _worktreeHashCache[item.Path] = (item.Length, item.Mtime, lines[i].Trim());
                        }
                    }
                    else
                    {
                        PerfLog.Mark($"IndexCaseCollisions.HashWorktreeAsync count mismatch: got {lines.Length}, expected {toHash.Count}");
                    }
                }
                else
                {
                    ct.ThrowIfCancellationRequested();
                    PerfLog.Mark($"IndexCaseCollisions.HashWorktreeAsync failed: exit={result.ExitCode}, kind={result.ErrorKind}");
                }
            }

            var dict = new Dictionary<string, string>(_worktreeHashCache.Count, StringComparer.Ordinal);
            foreach (var (p, entry) in _worktreeHashCache)
            {
                dict[p] = entry.Hash;
            }
            return dict;
        }
        finally
        {
            _gate.Release();
        }
    }

    internal static bool SharesOneDiskEntry(string workDir, string a, string b)
    {
        try
        {
            var fullA = Path.Combine(workDir, a.Replace('/', Path.DirectorySeparatorChar));
            var fullB = Path.Combine(workDir, b.Replace('/', Path.DirectorySeparatorChar));

            if (!File.Exists(fullA) || !File.Exists(fullB))
                return false;

            var segsA = a.Split('/');
            var segsB = b.Split('/');

            if (segsA.Length != segsB.Length)
                return false;

            int diffIndex = -1;
            for (var i = 0; i < segsA.Length; i++)
            {
                if (!string.Equals(segsA[i], segsB[i], StringComparison.Ordinal))
                {
                    if (!string.Equals(segsA[i], segsB[i], StringComparison.OrdinalIgnoreCase))
                        return false;

                    diffIndex = i;
                    break;
                }
            }

            if (diffIndex < 0)
                return false;

            var parentDir = diffIndex == 0
                ? workDir
                : Path.Combine(workDir, string.Join(Path.DirectorySeparatorChar, segsA[..diffIndex]));

            if (!Directory.Exists(parentDir))
                return false;

            var targetSeg = segsA[diffIndex];
            var count = 0;
            foreach (var entry in Directory.EnumerateFileSystemEntries(parentDir))
            {
                var name = Path.GetFileName(entry);
                if (string.Equals(name, targetSeg, StringComparison.OrdinalIgnoreCase))
                {
                    count++;
                }
            }

            return count == 1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private (long Mtime, long Length) GetConfigStamp()
    {
        var commonDir = RepoConfigReader.ResolveCommonDir(gitDirectory);
        var configPath = Path.Combine(commonDir, "config");
        if (File.Exists(configPath))
        {
            var fi = new FileInfo(configPath);
            return (fi.LastWriteTimeUtc.Ticks, fi.Length);
        }
        return (0L, 0L);
    }

    private (long Mtime, long Length) GetIndexStamp()
    {
        var indexPath = Path.Combine(gitDirectory, "index");
        if (File.Exists(indexPath))
        {
            var fi = new FileInfo(indexPath);
            return (fi.LastWriteTimeUtc.Ticks, fi.Length);
        }
        return (0L, 0L);
    }
}
