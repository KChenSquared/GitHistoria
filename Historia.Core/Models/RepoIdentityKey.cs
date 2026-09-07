namespace Historia.Models;

/// <summary>
/// Stable identity key for a repository (§9.3.1). Derived from the repository's <b>root
/// commit</b> only, so it survives folder renames, drive-letter changes, moves, and symlinks.
/// The path is deliberately never an input to the hash.
/// </summary>
public readonly record struct RepoIdentityKey(string Value)
{
    private const string Prefix = "Historia-repo-v1:";

    /// <summary>
    /// Computes the key from a root-commit SHA. When a repository has several root commits,
    /// pass the lexicographically smallest so the result is deterministic and identical across
    /// clones (§9.3.1, §17 Q10).
    /// </summary>
    public static RepoIdentityKey FromRootCommit(string rootCommitSha)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(Prefix + rootCommitSha));
        return new RepoIdentityKey(Convert.ToHexStringLower(hash));
    }

    /// <summary>
    /// Fallback for repositories with no reachable root (shallow clones, grafted history,
    /// empty repos) and for clones the user has given a separate identity. The GUID is
    /// persisted in the repository's own config as <c>Historia.repoId</c> and takes precedence
    /// over the derived key (§9.3.1).
    /// </summary>
    public static RepoIdentityKey FromExplicitId(Guid repoId) => new(repoId.ToString("N"));

    public override string ToString() => Value;
}
