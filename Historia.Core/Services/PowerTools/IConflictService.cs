namespace Historia.Services.PowerTools;

/// <summary>
/// Detect / parse / resolve / continue / abort for any conflicted operation (§8.4.4). Every
/// multi-step operation (merge, rebase, cherry-pick, revert) funnels here.
/// Registered as a singleton.
/// </summary>
public interface IConflictService
{
    /// <summary>Lists every unmerged path, deriving <see cref="ConflictStatus"/> from which
    /// index stages are present. Content is NOT loaded here — only metadata.</summary>
    Task<IReadOnlyList<ConflictFile>> GetConflictsAsync(RepositoryContext context, CancellationToken ct = default);

    /// <summary>Loads the working-tree file copy, checking budgets, detecting encoding/BOM,
    /// and inspecting conflict-marker-size attribute.</summary>
    Task<ConflictWorkingCopy> LoadWorkingCopyAsync(RepositoryContext context, string filePath, CancellationToken ct = default);

    /// <summary>Computes the on-disk SHA-256 hash for a working-tree path without loading or decoding content.
    /// Returns <see cref="string.Empty"/> if the file is missing or is a directory (matching the
    /// <see cref="ConflictWorkingCopy.DiskHash"/> convention). Equals the hash returned by
    /// <see cref="LoadWorkingCopyAsync"/> for the same bytes.</summary>
    Task<string> GetDiskHashAsync(RepositoryContext context, string filePath, CancellationToken ct = default);

    /// <summary>Writes resolved content and optionally stages it, verifying the current on-disk hash
    /// against <paramref name="expectedDiskHash"/> before writing (compare-before-write).
    /// Outcomes:
    /// <list type="bullet">
    /// <item><description>Refused (<see cref="ResolutionSaveResult.Saved"/> is false): current on-disk hash differed from <paramref name="expectedDiskHash"/>.</description></item>
    /// <item><description>Saved and staged (<see cref="ResolutionSaveResult.Saved"/> is true, <see cref="ResolutionSaveResult.StageError"/> is null): bytes written to disk and staged in index.</description></item>
    /// <item><description>Saved but not staged (<see cref="ResolutionSaveResult.Saved"/> is true, <see cref="ResolutionSaveResult.StageError"/> is set): bytes written to disk, but staging failed.</description></item>
    /// </list>
    /// If the file was missing when loaded, <paramref name="expectedDiskHash"/> should be <see cref="string.Empty"/>.</summary>
    Task<ResolutionSaveResult> SaveResolutionAsync(RepositoryContext context, string filePath, string text, TextFileFormat format, string expectedDiskHash, bool stage, CancellationToken ct = default);

    /// <summary>Resolves a path by taking one side wholesale, including submodule and delete cases.</summary>
    Task ResolveWithSideAsync(RepositoryContext context, string filePath, ConflictSide side, CancellationToken ct = default);

    /// <summary>Re-creates conflict markers with a base section (<c>--conflict=zdiff3</c>).</summary>
    Task RecreateConflictAsync(RepositoryContext context, string filePath, CancellationToken ct = default);

    /// <summary>Gets informative labels for Ours and Theirs sides based on the current sequencer operation and branch state.</summary>
    Task<ConflictSideLabels> GetSideLabelsAsync(RepositoryContext context, CancellationToken ct = default);

    /// <summary>Launches the configured external merge tool for one path and waits for exit (§8.4.5).</summary>
    Task<bool> LaunchExternalMergeToolAsync(RepositoryContext context, string filePath, CancellationToken ct = default);

    /// <summary>Continues the in-progress operation, dispatching on <see cref="OperationState"/> (§6.7).</summary>
    Task ContinueOperationAsync(RepositoryContext context, CancellationToken ct = default);

    /// <summary>Skips the current commit — valid for rebase, cherry-pick, revert; not merge.</summary>
    Task SkipCurrentAsync(RepositoryContext context, CancellationToken ct = default);

    /// <summary>Aborts the in-progress operation, restoring the prior state.</summary>
    Task AbortOperationAsync(RepositoryContext context, CancellationToken ct = default);
}

