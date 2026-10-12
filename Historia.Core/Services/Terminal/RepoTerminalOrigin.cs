namespace Historia.Services.Terminal;

/// <summary>Builds the terminal origin for a repository tab.</summary>
public static class RepoTerminalOrigin
{
    public static TerminalOrigin For(RepositoryContext ctx)
    {
        var key = ctx.IdentityKey ?? RepoIdentityKey.FromExplicitId(PathDerivedId(ctx.GitDirectory));
        var startDir = Historia.Services.Terminal.TerminalStartDirectory.ForRepository(
            ctx.IsBare ? null : ctx.RepositoryPath, ctx.GitDirectory);
        // Identity is the workspace tab (ctx.Id), not the repo hash — two tabs on the same
        // repository (e.g. two clones of one upstream) each get their own shell.
        return TerminalOrigin.ForRepository(key, ctx.Id, startDir, ctx.DisplayName);
    }

    // A stable id for a repository with no stored identity: the git directory path, case-folded,
    // hashed down to a Guid. It only tells repositories apart; it protects nothing.
    private static Guid PathDerivedId(string gitDirectory)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(gitDirectory.ToLowerInvariant()));
        return new Guid(hash.AsSpan(0, 16));
    }
}
