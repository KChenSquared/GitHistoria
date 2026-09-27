namespace Historia.Services.PowerTools;

/// <inheritdoc cref="IStashService"/>
public sealed class StashService(IGitCliService cli) : IStashService
{
    public Task<IReadOnlyList<StashEntry>> ListAsync(RepositoryContext context, CancellationToken ct = default)
        => context.WithRepositoryAsync(Project, ct);

    public async Task<bool> SaveAsync(RepositoryContext context, StashSaveOptions options, CancellationToken ct = default)
    {
        using var _ = context.BeginSelfOperation();

        var args = new List<string> { "stash", "push" };
        if (options.IncludeUntracked) args.Add("-u");
        if (options.KeepIndex) args.Add("--keep-index");
        if (!string.IsNullOrWhiteSpace(options.Message))
        {
            args.Add("-m");
            args.Add(options.Message);
        }

        // `--` and pathspecs come AFTER the message; Git 2.35+.
        if (options.Paths is { Count: > 0 })
        {
            args.Add("--");
            args.AddRange(options.Paths);
        }

        var result = await cli.RunAsync(new GitCliRequest { Arguments = args, WorkingDirectory = context.RepositoryPath }, ct)
            .ConfigureAwait(false);

        if (result.StandardOutput.Contains("No local changes to save", StringComparison.OrdinalIgnoreCase)
            || result.StandardError.Contains("No local changes to save", StringComparison.OrdinalIgnoreCase))
            return false;
        if (!result.IsSuccess)
            throw HistoriaGitException.FromCli(result, "Stash");

        await context.State.RefreshAllAsync(context.Lifetime).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> StashStagedAsync(RepositoryContext context, string? message, CancellationToken ct = default)
    {
        using var _ = context.BeginSelfOperation();

        var args = new List<string> { "stash", "push", "--staged" };
        if (!string.IsNullOrWhiteSpace(message))
        {
            args.Add("-m");
            args.Add(message);
        }

        var result = await cli.RunAsync(new GitCliRequest { Arguments = args, WorkingDirectory = context.RepositoryPath }, ct)
            .ConfigureAwait(false);

        // Nothing staged → git prints one of these and exits non-zero; treat as a no-op.
        var combined = result.StandardOutput + result.StandardError;
        if (combined.Contains("No staged changes", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("No local changes to save", StringComparison.OrdinalIgnoreCase))
            return false;
        if (!result.IsSuccess)
            throw HistoriaGitException.FromCli(result, "Stash staged");

        await context.State.RefreshAllAsync(context.Lifetime).ConfigureAwait(false);
        return true;
    }

    public Task ApplyAndDropAsync(RepositoryContext context, GitObjectId commitId, CancellationToken ct = default)
        => MutateAsync(context, commitId, "pop", extra: null, ct);

    public Task ApplyAsync(RepositoryContext context, GitObjectId commitId, bool restoreIndex, CancellationToken ct = default)
        => MutateAsync(context, commitId, "apply", extra: restoreIndex ? "--index" : null, ct);

    public Task DropAsync(RepositoryContext context, GitObjectId commitId, CancellationToken ct = default)
        => MutateAsync(context, commitId, "drop", extra: null, ct);

    public Task BranchFromAsync(RepositoryContext context, GitObjectId commitId, string branchName, CancellationToken ct = default)
        => MutateAsync(context, commitId, "branch", extra: branchName, ct, branchNameFirst: true);

    public async Task ClearAsync(RepositoryContext context, CancellationToken ct = default)
    {
        using var _ = context.BeginSelfOperation();
        var result = await cli.RunAsync(new GitCliRequest
        {
            Arguments = ["stash", "clear"],
            WorkingDirectory = context.RepositoryPath,
        }, ct).ConfigureAwait(false);
        if (!result.IsSuccess)
            throw HistoriaGitException.FromCli(result, "Stash clear");
        await context.State.RefreshAllAsync(context.Lifetime).ConfigureAwait(false);
    }

    private async Task MutateAsync(RepositoryContext context, GitObjectId commitId, string verb, string? extra,
        CancellationToken ct, bool branchNameFirst = false)
    {
        using var _ = context.BeginSelfOperation();

        // protocol: re-list, find the entry by commit id, act on its CURRENT index.
        var current = await context.WithRepositoryAsync(Project, ct).ConfigureAwait(false);
        var entry = current.FirstOrDefault(e => e.CommitId == commitId)
                    ?? throw new HistoriaGitException(GitErrorKind.RefNotFound,
                        "The stash list changed — refresh the stash panel and try again.");

        var stashRef = $"stash@{{{entry.Index}}}";
        var args = new List<string> { "stash", verb };
        if (branchNameFirst && extra is not null)
        {
            args.Add(extra);       // branch name
            args.Add(stashRef);
        }
        else
        {
            if (extra is not null) args.Add(extra);
            args.Add(stashRef);
        }

        var result = await cli.RunAsync(new GitCliRequest { Arguments = args, WorkingDirectory = context.RepositoryPath }, ct)
            .ConfigureAwait(false);

        // A conflicting pop/apply leaves the working tree with markers and the entry intact.
        if (!result.IsSuccess && result.ErrorKind != GitErrorKind.MergeConflict)
            throw HistoriaGitException.FromCli(result, $"Stash {verb}");

        await context.State.RefreshAllAsync(context.Lifetime).ConfigureAwait(false);
    }

    private static IReadOnlyList<StashEntry> Project(Repository repo)
    {
        var result = new List<StashEntry>();
        var i = 0;
        foreach (var stash in repo.Stashes)
        {
            var commit = stash.WorkTree;
            var includesUntracked = commit.Parents.Count() == 3; // HEAD, index, [untracked tree]
            result.Add(new StashEntry
            {
                Index = i++,
                CommitId = commit.Id.ToGitObjectId(),
                Message = stash.Message,
                AuthorName = commit.Author.Name,
                AuthorEmail = commit.Author.Email,
                CreatedAt = commit.Author.When,
                BranchName = ExtractBranch(stash.Message),
                IncludesUntracked = includesUntracked,
                FileCount = CountFiles(repo, commit),
            });
        }

        return result;
    }

    private static string ExtractBranch(string message)
    {
        // "WIP on main: 1234abcd Subject" / "On main: message"
        var onIdx = message.IndexOf(" on ", StringComparison.OrdinalIgnoreCase);
        if (onIdx < 0)
            onIdx = message.StartsWith("On ", StringComparison.OrdinalIgnoreCase) ? 0 : -1;
        var start = onIdx < 0 ? -1 : message.IndexOf("on ", onIdx, StringComparison.OrdinalIgnoreCase) + 3;
        if (start < 3)
            return "";
        var colon = message.IndexOf(':', start);
        return colon > start ? message[start..colon].Trim() : "";
    }

    private static int CountFiles(Repository repo, Commit stashCommit)
    {
        var parent = stashCommit.Parents.FirstOrDefault();
        if (parent is null)
            return 0;
        using var changes = repo.Diff.Compare<TreeChanges>(parent.Tree, stashCommit.Tree);
        return changes.Count();
    }
}
