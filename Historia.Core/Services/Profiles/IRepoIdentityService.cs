namespace Historia.Services.Profiles;

/// <summary>
/// Resolves a repository's <see cref="RepoIdentityKey"/>, in precedence order:
/// an explicit <c>Historia.repoId</c> in local config wins; otherwise the
/// lexicographically smallest reachable root-commit SHA; otherwise a generated GUID persisted
/// to local config. Walks history — computed once per open and cached on
/// <see cref="RepositoryContext"/>. A GUID is generated only when the root probe succeeded and
/// found no root. A cancelled, timed-out or failed git probe throws
/// (<see cref="OperationCanceledException"/> or <see cref="HistoriaGitException"/>) and caches
/// nothing, so a later call re-probes. Singleton.
/// </summary>
public interface IRepoIdentityService
{
    Task<RepoIdentityKey> ResolveAsync(RepositoryContext context, CancellationToken ct = default);
}
