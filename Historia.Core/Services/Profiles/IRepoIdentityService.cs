namespace Historia.Services.Profiles;

/// <summary>
/// Resolves a repository's <see cref="RepoIdentityKey"/>, applying the precedence rules of
/// an explicit <c>Historia.repoId</c> in local config wins; otherwise the
/// lexicographically smallest reachable root-commit SHA; otherwise a generated GUID persisted
/// to local config. Walks history — computed once per open and cached on
/// <see cref="RepositoryContext"/>. Singleton.
/// </summary>
public interface IRepoIdentityService
{
    Task<RepoIdentityKey> ResolveAsync(RepositoryContext context, CancellationToken ct = default);
}
