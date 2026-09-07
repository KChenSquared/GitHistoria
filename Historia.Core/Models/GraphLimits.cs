namespace Historia.Models;

/// <summary>
/// Architectural safety ceilings on resident graph layout (§7.3, §7.6).
/// Prevents runaway memory allocation when background walks traverse massive histories.
/// </summary>
public static class GraphLimits
{
    /// <summary>Maximum resident rows when per-commit metadata is paged from disk.</summary>
    public const int MaxResidentRowsDisk = 1_000_000;

    /// <summary>Maximum resident rows when full CommitMetadata is resident in memory.</summary>
    public const int MaxResidentRowsMemory = 250_000;

    /// <summary>Returns the ceiling on resident layout rows given the metadata storage mode.</summary>
    public static int GetMaxResidentRows(bool metaFromDisk) =>
        metaFromDisk ? MaxResidentRowsDisk : MaxResidentRowsMemory;
}
