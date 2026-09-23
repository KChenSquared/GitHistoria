namespace Historia.Models;

/// <summary>
/// A single unmerged path. Index stages give conflict status and file-level actions;
/// the working file's markers give conflict regions for resolution.
/// </summary>
public sealed record ConflictFile
{
    public required string FilePath { get; init; }
    public required ConflictStatus Status { get; init; }
    public required bool IsBinary { get; init; }
    public bool IsSubmodule { get; init; }
}

public sealed record ConflictWorkingCopy
{
    public required string Path { get; init; }
    public string? Text { get; init; }
    public TextFileFormat? Format { get; init; }
    public bool IsBinary { get; init; }
    public bool ExceedsBudget { get; init; }
    public bool UnsupportedEncoding { get; init; }
    public int MarkerSize { get; init; } = 7;
    public required string DiskHash { get; init; }
}

public sealed record ResolutionSaveResult
{
    public required bool Saved { get; init; }
    public required string DiskHash { get; init; }
}

public sealed record ConflictSideLabels
{
    public required string OursTitle { get; init; }
    public required string OursDetail { get; init; }
    public required string TheirsTitle { get; init; }
    public required string TheirsDetail { get; init; }
    public required OperationState Operation { get; init; }
    public required string StopId { get; init; }
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

