namespace Historia.Services.PowerTools;

/// <summary>
/// Detect / parse / resolve / continue / abort for any conflicted operation (§8.4.4). Every
/// Phase 3 multi-step operation (merge, rebase, cherry-pick, revert) funnels here, which is
/// why it is built first (§8.9.1). Registered as a singleton.
/// </summary>
public interface IConflictService
{
    /// <summary>Lists every unmerged path, deriving <see cref="ConflictStatus"/> from which
    /// index stages are present. Content is NOT loaded here — only metadata.</summary>
    Task<IReadOnlyList<ConflictFile>> GetConflictsAsync(RepositoryContext context, CancellationToken ct = default);

    /// <summary>Loads base/ours/theirs from index stages 1/2/3 for one path. A null stage is meaningful.</summary>
    Task<ConflictFile> LoadConflictContentAsync(RepositoryContext context, string filePath, CancellationToken ct = default);

    /// <summary>Maps conflict-marker regions in working-tree content for Result-pane navigation only (§8.4.2).</summary>
    IReadOnlyList<ConflictRegion> ParseMarkerRegions(string workingTreeContent);

    /// <summary>Writes resolved content and stages it, collapsing stages 1/2/3 into stage 0.</summary>
    Task MarkResolvedAsync(RepositoryContext context, string filePath, string resolvedContent, CancellationToken ct = default);

    /// <summary>Resolves a path by taking one side wholesale, including the delete cases.</summary>
    Task ResolveWithSideAsync(RepositoryContext context, string filePath, ConflictSide side, CancellationToken ct = default);

    /// <summary>Launches the configured external merge tool for one path and waits for exit (§8.4.5).</summary>
    Task<bool> LaunchExternalMergeToolAsync(RepositoryContext context, string filePath, CancellationToken ct = default);

    /// <summary>Continues the in-progress operation, dispatching on <see cref="OperationState"/> (§6.7).</summary>
    Task ContinueOperationAsync(RepositoryContext context, CancellationToken ct = default);

    /// <summary>Skips the current commit — valid for rebase, cherry-pick, revert; not merge.</summary>
    Task SkipCurrentAsync(RepositoryContext context, CancellationToken ct = default);

    /// <summary>Aborts the in-progress operation, restoring the prior state.</summary>
    Task AbortOperationAsync(RepositoryContext context, CancellationToken ct = default);
}
