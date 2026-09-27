namespace Historia.Services.PowerTools;

public sealed record StashSaveOptions
{
    public string? Message { get; init; }
    public bool IncludeUntracked { get; init; }
    public bool KeepIndex { get; init; }
    public IReadOnlyList<string>? Paths { get; init; }
}

/// <summary>
/// Visual stash manager. Every method is keyed by <see cref="StashEntry.CommitId"/> and
/// re-resolves the current <c>stash@{n}</c> immediately before acting — index-keyed overloads
/// deliberately do not exist. Singleton.
/// </summary>
public interface IStashService
{
    Task<IReadOnlyList<StashEntry>> ListAsync(RepositoryContext context, CancellationToken ct = default);

    /// <summary>Returns false when there was nothing to stash.</summary>
    Task<bool> SaveAsync(RepositoryContext context, StashSaveOptions options, CancellationToken ct = default);

    /// <summary>Stashes <b>only the staged content</b> (<c>git stash push --staged</c>, Git ≥ 2.35),
    /// leaving unstaged working-tree changes in place. Returns false when nothing was staged.</summary>
    Task<bool> StashStagedAsync(RepositoryContext context, string? message, CancellationToken ct = default);

    /// <summary>Applies then drops. On conflict the entry is <b>kept</b> (Git's behaviour) and the resolver opens.</summary>
    Task ApplyAndDropAsync(RepositoryContext context, ObjectId commitId, CancellationToken ct = default);

    Task ApplyAsync(RepositoryContext context, ObjectId commitId, bool restoreIndex, CancellationToken ct = default);
    Task DropAsync(RepositoryContext context, ObjectId commitId, CancellationToken ct = default);
    Task ClearAsync(RepositoryContext context, CancellationToken ct = default);
    Task BranchFromAsync(RepositoryContext context, ObjectId commitId, string branchName, CancellationToken ct = default);
}
