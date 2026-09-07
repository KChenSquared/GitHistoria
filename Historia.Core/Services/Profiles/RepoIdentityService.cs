namespace Historia.Services.Profiles;

/// <inheritdoc cref="IRepoIdentityService"/>
public sealed class RepoIdentityService(IGitCliService cli) : IRepoIdentityService
{
    public async Task<RepoIdentityKey> ResolveAsync(RepositoryContext context, CancellationToken ct = default)
    {
        if (context.IdentityKey is { } cached)
            return cached;

        // 1. Explicit override wins (§9.3.1).
        var explicitId = await ReadRepoIdAsync(context, ct).ConfigureAwait(false);
        if (explicitId is { } guid)
        {
            var key = RepoIdentityKey.FromExplicitId(guid);
            context.IdentityKey = key;
            return key;
        }

        // 2. Lexicographically smallest reachable root-commit SHA (§9.3.1, §17 Q10).
        var smallestRoot = await context.WithRepositoryAsync(FindSmallestRootSha, ct).ConfigureAwait(false);
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

    private static string? FindSmallestRootSha(Repository repo)
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
            SortBy = CommitSortStrategies.Topological,
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
