using Historia.Services.Workspace;

namespace Historia.Services.PowerTools;

public sealed record WorktreeAddRequest
{
    public required string Path { get; init; }

    /// <summary>Existing branch to check out, or the start point when <see cref="NewBranch"/> is set.</summary>
    public string? Branch { get; init; }
    public string? NewBranch { get; init; }
    public bool Detach { get; init; }
    public string? Commit { get; init; }
}

/// <summary>Worktree add / remove / lock / unlock / move / prune / repair (§8.7.2). Singleton.</summary>
public interface IWorktreeService
{
    Task<IReadOnlyList<WorktreeInfo>> ListAsync(RepositoryContext context, CancellationToken ct = default);
    Task AddAsync(RepositoryContext context, WorktreeAddRequest request, CancellationToken ct = default);
    Task RemoveAsync(RepositoryContext context, string path, bool force, CancellationToken ct = default);
    Task LockAsync(RepositoryContext context, string path, string reason, CancellationToken ct = default);
    Task UnlockAsync(RepositoryContext context, string path, CancellationToken ct = default);
    Task MoveAsync(RepositoryContext context, string path, string newPath, CancellationToken ct = default);
    Task PruneAsync(RepositoryContext context, CancellationToken ct = default);
    Task RepairAsync(RepositoryContext context, CancellationToken ct = default);
}
