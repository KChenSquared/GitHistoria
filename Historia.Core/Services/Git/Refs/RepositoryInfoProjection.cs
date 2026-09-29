namespace Historia.Services.Git;

/// <summary>
/// Projects <see cref="RepositoryInfo"/> from a <see cref="RefSnapshot"/> and repository facts.
/// </summary>
public static class RepositoryInfoProjection
{
    public static RepositoryInfo Project(
        RefSnapshot snapshot,
        string repositoryPath,
        string gitDirectory,
        string displayName,
        bool isBare,
        string? hooksDir)
    {
        string? currentBranchName;
        if (snapshot.IsHeadUnborn)
        {
            currentBranchName = null;
        }
        else if (snapshot.IsHeadDetached)
        {
            currentBranchName = "(no branch)";
        }
        else if (snapshot.HeadSymref is not null)
        {
            currentBranchName = snapshot.HeadSymref.StartsWith("refs/heads/", StringComparison.Ordinal)
                ? snapshot.HeadSymref["refs/heads/".Length..]
                : snapshot.HeadSymref;
        }
        else
        {
            currentBranchName = null;
        }

        return new RepositoryInfo
        {
            WorkingDirectory = isBare ? gitDirectory : repositoryPath,
            GitDirectory = gitDirectory,
            DisplayName = displayName,
            IsBare = isBare,
            IsHeadDetached = snapshot.IsHeadDetached,
            IsHeadUnborn = snapshot.IsHeadUnborn,
            CurrentBranchName = currentBranchName,
            HasCommitHooks = CheckHasCommitHooks(hooksDir),
        };
    }

    public static RepositoryInfo Project(RefSnapshot snapshot, RepositoryContext context, string? hooksDir)
        => Project(snapshot, context.RepositoryPath, context.GitDirectory, context.DisplayName, context.IsBare, hooksDir);

    public static bool CheckHasCommitHooks(string? hooksDir)
    {
        if (string.IsNullOrWhiteSpace(hooksDir) || !Directory.Exists(hooksDir))
            return false;

        foreach (var name in (ReadOnlySpan<string>)["pre-commit", "commit-msg"])
        {
            if (File.Exists(Path.Combine(hooksDir, name)))
                return true;
        }

        return false;
    }
}
