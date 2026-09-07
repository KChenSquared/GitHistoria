using Historia.Services.Workspace;

namespace Historia.Services.PowerTools;

/// <summary>Submodule add / init / update / sync / remove (§8.6.2). Singleton.</summary>
public interface ISubmoduleService
{
    Task<IReadOnlyList<SubmoduleInfo>> ListAsync(RepositoryContext context, CancellationToken ct = default);
    Task AddAsync(RepositoryContext context, string url, string path, CancellationToken ct = default);
    Task InitAndUpdateAsync(RepositoryContext context, string? path, bool recursive, CancellationToken ct = default);
    Task SyncAsync(RepositoryContext context, string? path, CancellationToken ct = default);
    Task RemoveAsync(RepositoryContext context, SubmoduleInfo submodule, CancellationToken ct = default);
}
