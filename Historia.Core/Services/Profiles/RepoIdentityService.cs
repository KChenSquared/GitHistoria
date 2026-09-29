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

        // 3. The probe succeeded and found no reachable root (empty repo, or every root grafted
        //    away): generate and persist a GUID. The key is cached only once the write landed.
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
    /// graph's own cold-open walk. Returns null only when the walk succeeded and found no root;
    /// a cancelled, timed-out or failed walk throws.
    /// </summary>
    private async Task<string?> FindSmallestRootShaAsync(RepositoryContext context, CancellationToken ct)
    {
        using var _ = Historia.Diagnostics.PerfLog.Scope("RepoIdentityService.FindSmallestRootSha (cli)");
        var result = await cli.RunAsync(new GitCliRequest
        {
            Arguments = ["rev-list", "--max-parents=0", "--branches", "--remotes", "--tags"],
            WorkingDirectory = context.RepositoryPath,
            Timeout = TimeSpan.FromSeconds(120),
            // Ignore `git replace` refs entirely so replace grafts don't mask root commits.
            ExtraEnvironment = new Dictionary<string, string> { ["GIT_NO_REPLACE_OBJECTS"] = "1" },
        }, ct).ConfigureAwait(false);

        ThrowIfFailed(result, "Find root commits", ct);

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

    private async Task<Guid?> ReadRepoIdAsync(RepositoryContext context, CancellationToken ct)
    {
        var result = await cli.RunAsync(new GitCliRequest
        {
            Arguments = ["config", "--local", "--get", "Historia.repoId"],
            WorkingDirectory = context.RepositoryPath,
            Timeout = TimeSpan.FromSeconds(10),
        }, ct).ConfigureAwait(false);

        // `git config --get` exits 1 when the key is not set; any other failure is not "no id".
        if (result.ExitCode == 1)
            return null;
        ThrowIfFailed(result, "Read repository id", ct);

        return Guid.TryParse(result.StandardOutput.Trim(), out var g) ? g : null;
    }

    private async Task WriteRepoIdAsync(RepositoryContext context, Guid id, CancellationToken ct)
    {
        var result = await cli.RunAsync(new GitCliRequest
        {
            Arguments = ["config", "--local", "Historia.repoId", id.ToString("N")],
            WorkingDirectory = context.RepositoryPath,
            Timeout = TimeSpan.FromSeconds(10),
        }, ct).ConfigureAwait(false);

        ThrowIfFailed(result, "Write repository id", ct);
    }

    /// <summary>The CLI reports cancellation and timeouts as results rather than exceptions; an
    /// identity must never be derived from one, so a non-zero result always surfaces as an
    /// exception.</summary>
    private static void ThrowIfFailed(GitCliResult result, string operation, CancellationToken ct)
    {
        if (result.ExitCode == 0)
            return;
        if (result.ErrorKind == GitErrorKind.Cancelled)
            throw new OperationCanceledException(ct);
        throw HistoriaGitException.FromCli(result, operation);
    }
}
