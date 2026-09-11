namespace Historia.Services.Graph;

/// <summary>
/// §4 of the linux cold-prime plan: once a large repository's full history has been walked and
/// spilled to disk (§7.6), write a commit-graph file for it if it doesn't already have one. A
/// commit-graph is what lets <c>git log --topo-order</c> (<see cref="GitLogStream"/>, the CLI
/// walk engine) expand lazily by generation number instead of touching the whole reachable
/// history up front — a repo that gets one here starts fast on every later open, not just the one
/// that triggered the write.
/// <para>
/// Runs off the repo gate (a separate <c>git.exe</c> process) and one repo at a time app-wide, so
/// a multi-tab session restore never fans out several concurrent <c>git commit-graph write</c>
/// invocations. Each repository is attempted at most once per process lifetime.
/// </para>
/// </summary>
internal sealed class CommitGraphMaintenance(IGitCliService cli, ISettingsService settings)
{
    private const int MinCommitsToWrite = 100_000;

    // App-wide serialization: only one `git commit-graph write` runs at a time.
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private readonly HashSet<string> _attempted = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _sync = new();

    /// <summary>
    /// Fire-and-forget: schedules a background <c>git commit-graph write --reachable</c> if this
    /// repo qualifies (≥ <see cref="MinCommitsToWrite"/> commits walked, the setting is on, git is
    /// available, and no commit-graph exists yet). Safe to call repeatedly — e.g. every time the
    /// §7.6 background spill reaches completion — only the first qualifying call per repo per
    /// process does any work.
    /// </summary>
    public void MaybeSchedule(RepositoryContext context, int commitsWalked)
    {
        if (!settings.Current.WriteCommitGraphForLargeRepos || !cli.IsGitAvailable)
            return;
        if (commitsWalked < MinCommitsToWrite)
            return;

        lock (_sync)
        {
            if (!_attempted.Add(context.GitDirectory))
                return;
        }

        _ = Task.Run(() => RunAsync(context));
    }

    private async Task RunAsync(RepositoryContext context)
    {
        try
        {
            if (HasCommitGraph(context.GitDirectory))
                return;

            await Gate.WaitAsync(context.Lifetime).ConfigureAwait(false);
            try
            {
                // Another repo sharing this gitdir (a second tab, a worktree) may have written
                // one while this call waited for the gate.
                if (HasCommitGraph(context.GitDirectory))
                    return;

                using var _ = Diagnostics.PerfLog.Scope($"CommitGraphMaintenance.write({context.DisplayName})");
                var result = await cli.RunAsync(new GitCliRequest
                {
                    Arguments = ["commit-graph", "write", "--reachable"],
                    WorkingDirectory = context.RepositoryPath,
                    Timeout = Timeout.InfiniteTimeSpan,
                }, context.Lifetime).ConfigureAwait(false);

                if (!result.IsSuccess)
                    Diagnostics.PerfLog.Mark($"CommitGraphMaintenance: write failed (exit={result.ExitCode}) for {context.DisplayName}");
            }
            finally
            {
                Gate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            // Tab closed mid-write — best effort, try again next launch.
        }
        catch
        {
            // Commit-graph maintenance is an optimisation, never a correctness dependency.
        }
    }

    private static bool HasCommitGraph(string gitDirectory)
    {
        try
        {
            return File.Exists(Path.Combine(gitDirectory, "objects", "info", "commit-graph"))
                || Directory.Exists(Path.Combine(gitDirectory, "objects", "info", "commit-graphs"));
        }
        catch
        {
            return false;
        }
    }
}
