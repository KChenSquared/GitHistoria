namespace Historia.Services.Git;

public sealed partial class GitGateway
{
    public Task<RepositoryInfo> GetRepositoryInfoAsync(CancellationToken ct = default)
        => context.WithRepositoryAsync(lib.ProjectInfo, ct);

    public Task<IReadOnlyList<BranchInfo>> GetBranchesAsync(CancellationToken ct = default)
        => context.WithRepositoryAsync(lib.ProjectBranches, ct);

    /// <summary>From the repo's cached ref snapshot — one <c>git for-each-ref</c>, shared with
    /// the commit graph, and never the repo gate (LibGit2Sharp's tag enumeration held it for
    /// 9–13 s at chromium's 39k tags, queueing status behind it). LibGit2Sharp only when git.exe
    /// is missing.</summary>
    public async Task<IReadOnlyList<TagInfo>> GetTagsAsync(CancellationToken ct = default)
    {
        if (!cli.IsGitAvailable)
            return await context.WithRepositoryAsync(lib.ProjectTags, ct).ConfigureAwait(false);

        var snapshot = await context.RefSnapshots.GetAsync(ct).ConfigureAwait(false);
        return TagProjection.FromSnapshot(snapshot);
    }

    public Task<IReadOnlyList<RemoteInfo>> GetRemotesAsync(CancellationToken ct = default)
        => context.WithRepositoryAsync(lib.ProjectRemotes, ct);


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
}
