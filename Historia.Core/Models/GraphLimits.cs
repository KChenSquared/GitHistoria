namespace Historia.Models;

/// <summary>
/// Architectural safety ceilings on resident graph layout.
/// Prevents runaway memory allocation when background walks traverse massive histories.
/// </summary>
public static class GraphLimits
{
    /// <summary>Maximum resident rows when per-commit metadata is paged from disk.</summary>
    public const int MaxResidentRowsDisk = 2_000_000;

    /// <summary>Maximum resident rows when full CommitMetadata is resident in memory.</summary>
    public const int MaxResidentRowsMemory = 500_000;

    /// <summary>Returns the ceiling on resident layout rows given the metadata storage mode,
    /// using <paramref name="residentRowCapDisk"/> (AppSettings.ResidentRowCap) as the
    /// disk-mode ceiling. Clamped through <see cref="AppSettings.ClampResidentRowCap"/> here —
    /// centrally, not at each call site — so a hand-edited or otherwise out-of-range settings
    /// value (e.g. <c>0</c>) can never stop the background spill or the graph from extending
    /// (#13, 2026-09-12 memory review).</summary>
    public static int GetMaxResidentRows(bool metaFromDisk, int residentRowCapDisk) =>
        metaFromDisk ? AppSettings.ClampResidentRowCap(residentRowCapDisk) : MaxResidentRowsMemory;
}
