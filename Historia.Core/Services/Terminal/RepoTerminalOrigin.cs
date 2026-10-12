namespace Historia.Services.Terminal;

/// <summary>Builds the terminal origin for a repository tab.</summary>
public static class RepoTerminalOrigin
{
    public static TerminalOrigin For(RepositoryContext ctx)
    {
        var key = ctx.IdentityKey ?? RepoIdentityKey.FromExplicitId(
            Guid.Parse(System.Security.Cryptography.MD5.HashData(
                System.Text.Encoding.UTF8.GetBytes(ctx.GitDirectory.ToLowerInvariant()))
                .Aggregate(new System.Text.StringBuilder(32), (sb, b) => sb.Append(b.ToString("x2"))).ToString()));
        var startDir = Historia.Services.Terminal.TerminalStartDirectory.ForRepository(
            ctx.IsBare ? null : ctx.RepositoryPath, ctx.GitDirectory);
        // Identity is the workspace tab (ctx.Id), not the repo hash — two tabs on the same
        // repository (e.g. two clones of one upstream) each get their own shell.
        return TerminalOrigin.ForRepository(key, ctx.Id, startDir, ctx.DisplayName);
    }
}
