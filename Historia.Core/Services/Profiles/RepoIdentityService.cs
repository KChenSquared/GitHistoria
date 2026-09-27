namespace Historia.Services.Profiles;

/// <inheritdoc cref="IRepoIdentityService"/>
public sealed class RepoIdentityService(IGitCliService cli) : IRepoIdentityService
{
    public async Task<RepoIdentityKey> ResolveAsync(RepositoryContext context, CancellationToken ct = default)
    {
        if (context.IdentityKey is { } cached)
            return cached;

        using var _ = Historia.Diagnostics.PerfLog.Scope($"RepoIdentityService.ResolveAsync({context.DisplayName})");

        // 1. Explicit override wins.
        var explicitId = await ReadRepoIdAsync(context, ct).ConfigureAwait(false);
        if (explicitId is { } guid)
        {
            var key = RepoIdentityKey.FromExplicitId(guid);
            context.IdentityKey = key;
            return key;
        }

        // 2. Lexicographically smallest reachable root-commit SHA.
        var smallestRoot = await FindSmallestRootShaAsync(context, ct).ConfigureAwait(false);
        if (smallestRoot is not null)
        {
            var key = RepoIdentityKey.FromRootCommit(smallestRoot);
            context.IdentityKey = key;
            return key;
        }

        // 3. No reachable root (shallow / grafted / empty): generate and persist a GUID.
        var newId = Guid.NewGuid();
        await WriteRepoIdAsync(context, newId, ct).ConfigureAwait(false);
        var explicitKey = RepoIdentityKey.FromExplicitId(newId);
        context.IdentityKey = explicitKey;
        return explicitKey;
    }

    /// <summary>
    /// Resolves the lexicographically smallest reachable root-commit SHA.
    /// Primary path shells out to <c>git rev-list --max-parents=0</c> — a separate process that
    /// never takes the repo's foreground gate, so it can never queue behind (or block) the
    /// graph's own cold-open walk. Only when git is unavailable, or the invocation fails, does
    /// this fall back to a libgit2 walk — streamed with no topological sort, so it never pays the
    /// same tip-priority prime the graph's own walk does.
    /// </summary>
    private async Task<string?> FindSmallestRootShaAsync(RepositoryContext context, CancellationToken ct)
    {
        if (cli.IsGitAvailable)
        {
            using var _ = Historia.Diagnostics.PerfLog.Scope("RepoIdentityService.FindSmallestRootSha (cli)");
            var result = await cli.RunAsync(new GitCliRequest
            {
                Arguments = ["rev-list", "--max-parents=0", "--branches", "--remotes", "--tags"],
                WorkingDirectory = context.RepositoryPath,
                Timeout = TimeSpan.FromSeconds(120),
                // The libgit2 fallback below (and every other caller of it, e.g.
                // GitLogStream.cs) ignores `git replace` refs entirely, so the CLI path must too —
                // otherwise a repo using replace grafts computes a different smallest root through
                // this path than through the libgit2 fallback, and the identity key (and its
                // repo-profiles.json binding) silently disagrees depending on which path ran.
                ExtraEnvironment = new Dictionary<string, string> { ["GIT_NO_REPLACE_OBJECTS"] = "1" },
                // The libgit2 fallback below (and every other caller of it, e.g.
                // GitLogStream.cs) ignores `git replace` refs entirely, so the CLI path must too —
                // otherwise a repo using replace grafts computes a different smallest root through
                // this path than through the libgit2 fallback, and the identity key (and its
                // repo-profiles.json binding) silently disagrees depending on which path ran.
            }, ct).ConfigureAwait(false);

            if (result.ExitCode == 0)
            {
                string? smallest = null;
                foreach (var line in result.StandardOutput.Split('\n'))
                {
                    var sha = line.Trim();
                    if (sha.Length == 0)
                        continue;
                    if (smallest is null || string.CompareOrdinal(sha, smallest) < 0)
                        smallest = sha;
                }
                return smallest;
            }

            Historia.Diagnostics.PerfLog.Mark(
                $"RepoIdentityService: rev-list failed (exit={result.ExitCode}), falling back to libgit2 walk");
        }

        // Fallback: git missing, or the CLI invocation failed. Stream the walk (no topological
        // sort — that prime is exactly the cost this path exists to avoid) off the repo gate so a
        // slow fallback still never blocks the foreground.
        return await context.WithRepositoryBackgroundAsync(FindSmallestRootShaFallback, ct).ConfigureAwait(false);
    }

    private static string? FindSmallestRootShaFallback(Repository repo)
    {
        if (repo.Info.IsHeadUnborn && !repo.Refs.Any())
            return null;

        var tips = repo.Refs
            .Where(r => r.IsLocalBranch || r.IsRemoteTrackingBranch || r.IsTag)
            .ToArray();
        if (tips.Length == 0)
            return null;

        var filter = new CommitFilter
        {
            SortBy = CommitSortStrategies.None,
            IncludeReachableFrom = tips,
        };

        string? smallest = null;
        foreach (var commit in repo.Commits.QueryBy(filter))
        {
            if (commit.Parents.Any())
                continue;
            var sha = commit.Sha;
            if (smallest is null || string.CompareOrdinal(sha, smallest) < 0)
                smallest = sha;
        }

        return smallest;
    }

    private async Task<Guid?> ReadRepoIdAsync(RepositoryContext context, CancellationToken ct)
    {
        if (!cli.IsGitAvailable)
            return null;

        var result = await cli.RunAsync(new GitCliRequest
        {
            Arguments = ["config", "--local", "--get", "Historia.repoId"],
            WorkingDirectory = context.RepositoryPath,
            Timeout = TimeSpan.FromSeconds(10),
        }, ct).ConfigureAwait(false);

        if (result.ExitCode != 0)
            return null;

        return Guid.TryParse(result.StandardOutput.Trim(), out var g) ? g : null;
    }

    private async Task WriteRepoIdAsync(RepositoryContext context, Guid id, CancellationToken ct)
    {
        if (!cli.IsGitAvailable)
            return;

        await cli.RunAsync(new GitCliRequest
        {
            Arguments = ["config", "--local", "Historia.repoId", id.ToString("N")],
            WorkingDirectory = context.RepositoryPath,
            Timeout = TimeSpan.FromSeconds(10),
        }, ct).ConfigureAwait(false);
    }
}
