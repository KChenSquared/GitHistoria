namespace Historia.Services.Profiles;

/// <inheritdoc cref="IRepoIdentityService"/>
public sealed class RepoIdentityService(IGitCliService cli) : IRepoIdentityService
{
    // One resolve per context at a time. Five call sites can overlap on a freshly opened tab; on a
    // repo with no root commit each would generate and persist its own id, and the caller that
    // lost the race would act on a key that was never stored.
    private readonly object _inFlightLock = new();
    private readonly ConditionalWeakTable<RepositoryContext, Task<RepoIdentityKey>> _inFlight = new();

    public async Task<RepoIdentityKey> ResolveAsync(RepositoryContext context, CancellationToken ct = default)
    {
        if (context.IdentityKey is { } cached)
            return cached;

        Task<RepoIdentityKey> shared;
        lock (_inFlightLock)
        {
            if (!_inFlight.TryGetValue(context, out shared!))
            {
                // Runs on the tab's own lifetime, not on this caller's token: another caller may be
                // waiting on the same resolve, so one caller giving up must not cancel it.
                shared = Task.Run(() => ResolveCoreAsync(context, context.Lifetime));
                _inFlight.Add(context, shared);
                var started = shared;
                _ = started.ContinueWith(t =>
                {
                    _ = t.Exception; // observed here even when every caller has given up
                    ForgetIfFailed(context, t);
                }, TaskScheduler.Default);
            }
        }

        try
        {
            return await shared.WaitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            // Dropped before this caller rethrows, so the very next call already starts afresh.
            ForgetIfFailed(context, shared);
            throw;
        }
    }

    /// <summary>A failed or cancelled resolve is never remembered: the next call retries. A resolve
    /// still running, or one that succeeded, stays.</summary>
    private void ForgetIfFailed(RepositoryContext context, Task<RepoIdentityKey> task)
    {
        if (!task.IsCompleted || task.IsCompletedSuccessfully)
            return;
        lock (_inFlightLock)
        {
            if (_inFlight.TryGetValue(context, out var current) && ReferenceEquals(current, task))
                _inFlight.Remove(context);
        }
    }

    private async Task<RepoIdentityKey> ResolveCoreAsync(RepositoryContext context, CancellationToken ct)
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
        //    The write itself is not cancelled by a tab close: killing `git config` mid-write could
        //    leave the config half-written. A tab that closed before the write starts skips it.
        var newId = Guid.NewGuid();
        ct.ThrowIfCancellationRequested();
        await WriteRepoIdAsync(context, newId, CancellationToken.None).ConfigureAwait(false);
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
