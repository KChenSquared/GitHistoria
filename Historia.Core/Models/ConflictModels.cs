namespace Historia.Models;

/// <summary>
/// A single unmerged path (§8.4.2). The three versions come from index stages 1/2/3 — never
/// from parsing conflict markers (see §8.4.2 sourcing note). A null <c>*Content</c> for a
/// stage is meaningful: it is what distinguishes <see cref="ConflictStatus.BothAdded"/>
/// (no base), <see cref="ConflictStatus.DeletedByUs"/> (no ours), and
/// <see cref="ConflictStatus.DeletedByThem"/> (no theirs).
/// </summary>
public sealed record ConflictFile
{
    public required string FilePath { get; init; }
    public required ConflictStatus Status { get; init; }
    public required bool IsBinary { get; init; }

    public required string? BaseContent { get; init; }    // Stage 1
    public required string? OursContent { get; init; }     // Stage 2
    public required string? TheirsContent { get; init; }   // Stage 3

    public string MergedContent { get; set; } = "";        // Editable result
}

public enum ConflictStatus
{
    BothModified,       // stages 1,2,3
    BothAdded,          // stages 2,3 (no ancestor)
    DeletedByUs,        // stages 1,3
    DeletedByThem,      // stages 1,2
    AddedByUs,          // rename/add asymmetry
    AddedByThem,
    BothDeletedRenamed, // rename/rename and rename/delete
}

public enum ConflictSide { Ours, Theirs }
