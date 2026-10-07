namespace Historia.Services.ExternalControl;

/// <summary>Resolves an externally supplied revision to a commit id. The only external-control code
/// that starts a git process, and it only reads.</summary>
public static class RevisionResolver
{
    /// <summary>The commit <paramref name="rev"/> names, or null when it is invalid, unknown, or
    /// names a non-commit object.</summary>
    public static async Task<GitObjectId?> ResolveCommitAsync(
        IGitCliService cli, string workingDir, string rev, CancellationToken ct = default)
    {
        if (!ControlInputValidation.TryValidateRevision(rev, out _))
            return null;

        var result = await cli.RunAsync(new GitCliRequest
        {
            WorkingDirectory = workingDir,
            Arguments = ["rev-parse", "--verify", "-q", "--end-of-options", rev + "^{commit}"],
        }, ct).ConfigureAwait(false);

        if (!result.IsSuccess)
            return null;

        return GitObjectId.TryParse(result.StandardOutput.Trim(), out var id) ? id : null;
    }
}
