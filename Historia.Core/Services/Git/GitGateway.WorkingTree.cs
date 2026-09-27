namespace Historia.Services.Git;

public sealed partial class GitGateway
{
    public async Task StageAsync(IReadOnlyList<string> paths, CancellationToken ct = default)
    {
        using var _ = context.BeginSelfOperation();
        await context.WithRepositoryAsync(r => lib.Stage(r, paths), ct).ConfigureAwait(false);
    }

    public async Task UnstageAsync(IReadOnlyList<string> paths, CancellationToken ct = default)
    {
        using var _ = context.BeginSelfOperation();

        var isUnborn = await context.WithRepositoryAsync(r => r.Info.IsHeadUnborn, ct).ConfigureAwait(false);
        if (isUnborn)
        {
            // No HEAD to reset against — fall back to `git rm --cached`.
            var args = new List<string> { "rm", "--cached", "--" };
            args.AddRange(paths);
            var result = await cli.RunAsync(new GitCliRequest
            {
                Arguments = args,
                WorkingDirectory = context.RepositoryPath,
            }, ct).ConfigureAwait(false);
            if (!result.IsSuccess)
                throw HistoriaGitException.FromCli(result, "Unstage");
            return;
        }

        await context.WithRepositoryAsync(r => lib.Unstage(r, paths), ct).ConfigureAwait(false);
    }

    public async Task DiscardAsync(IReadOnlyList<string> paths, CancellationToken ct = default)
    {
        using var _ = context.BeginSelfOperation();
        await context.WithRepositoryAsync(r => lib.DiscardWorkingTree(r, paths), ct).ConfigureAwait(false);
    }

    public async Task<GitObjectId> CommitAsync(CommitRequest request, CancellationToken ct = default)
    {
        using var _ = context.BeginSelfOperation();

        // Signed commits and repos with pre-commit / commit-msg hooks route through the CLI —
        // LibGit2Sharp invokes neither the GPG agent nor Git hooks.
        var hasHooks = await context.WithRepositoryAsync(
            r => LibGit2Service.RepoHasCommitHooks(r.Info.Path), ct).ConfigureAwait(false);

        if (request.Sign || hasHooks)
        {
            var args = new List<string> { "commit", "-m", request.Message };
            if (request.Sign) args.Add("-S");
            if (request.AmendPreviousCommit) args.Add("--amend");
            if (request.AllowEmptyCommit) args.Add("--allow-empty");
            args.Add("--author");
            args.Add($"{request.AuthorName} <{request.AuthorEmail}>");

            var env = new Dictionary<string, string>
            {
                ["GIT_COMMITTER_NAME"] = request.CommitterName ?? request.AuthorName,
                ["GIT_COMMITTER_EMAIL"] = request.CommitterEmail ?? request.AuthorEmail,
            };

            var result = await cli.RunAsync(new GitCliRequest
            {
                Arguments = args,
                WorkingDirectory = context.RepositoryPath,
                ExtraEnvironment = env,
            }, ct).ConfigureAwait(false);
            if (!result.IsSuccess)
                throw HistoriaGitException.FromCli(result, "Commit");

            return await context.WithRepositoryAsync(r => r.Head.Tip!.Id.ToGitObjectId(), ct).ConfigureAwait(false);
        }

        return await context.WithRepositoryAsync(r => lib.Commit(r, request), ct).ConfigureAwait(false);
    }
}
