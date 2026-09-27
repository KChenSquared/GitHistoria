
namespace Historia.Services.Workspace;

/// <summary>Persistent recent-repositories list. Singleton.</summary>
public interface IRecentRepositoryService
{
    /// <summary>Most-recent first, with <see cref="RecentRepository.IsStale"/> resolved against the filesystem.</summary>
    IReadOnlyList<RecentRepository> Items { get; }

    Task AddAsync(string path, string displayName, CancellationToken ct = default);
    Task RemoveAsync(string path, CancellationToken ct = default);
    Task ClearAsync(CancellationToken ct = default);

    event EventHandler? Changed;
}
