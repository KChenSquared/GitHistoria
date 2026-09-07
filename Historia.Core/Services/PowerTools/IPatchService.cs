namespace Historia.Services.PowerTools;

/// <summary>
/// Builds and applies partial-staging patches (§8.2.5). Patches are generated from the diff
/// Git produced (<see cref="FileDiff.RawPatch"/>), never by re-splitting the file. Every apply
/// is preceded by a check with the same flags. Singleton.
/// </summary>
public interface IPatchService
{
    /// <summary>
    /// Builds a unified-diff patch containing only the requested hunks, or only the requested
    /// lines within them. The caller supplies the <see cref="FileDiff"/> for the correct diff
    /// direction (index↔worktree to stage, HEAD↔index to unstage) — §8.2.3 requirement 1.
    /// </summary>
    string GeneratePatch(FileDiff source, PatchScope scope, IReadOnlySet<DiffLineRef>? selectedLines = null);

    /// <summary>Runs <c>git apply --check</c> with the same flags the real apply will use.</summary>
    Task<PatchCheckResult> CheckAsync(RepositoryContext context, string patch, PatchTarget target, bool reverse, CancellationToken ct = default);

    /// <summary>Applies a patch to the index (<c>--cached</c>) or the working tree. Always preceded by <see cref="CheckAsync"/>.</summary>
    Task ApplyPatchAsync(RepositoryContext context, string patch, PatchTarget target, bool reverse, CancellationToken ct = default);
}
