namespace Historia.Models;

/// <summary>
/// A parsed file diff. <see cref="DiffHunk"/>/<see cref="DiffLine"/> are the single
/// shared hunk model used by display and partial staging — no parallel
/// model is defined elsewhere.
/// </summary>
public sealed record FileDiff
{
    public required string Path { get; init; }
    public string? OldPath { get; init; }
    public required DiffKind Kind { get; init; }
    public required bool IsBinary { get; init; }

    /// <summary>True when the file exceeded the inline-diff size budget.</summary>
    public required bool IsTruncated { get; init; }
    public required IReadOnlyList<DiffHunk> Hunks { get; init; }
    public required int AddedLines { get; init; }
    public required int DeletedLines { get; init; }

    /// <summary>
    /// The raw unified-diff text Git produced for this file. Patches for partial
    /// staging are generated from this — never by re-reading and re-splitting the file, which
    /// would break under <c>core.autocrlf</c> and drop the no-newline marker.
    /// </summary>
    public string? RawPatch { get; init; }

    public static FileDiff EmptyFor(string path, DiffKind kind) => new()
    {
        Path = path,
        Kind = kind,
        IsBinary = false,
        IsTruncated = false,
        Hunks = [],
        AddedLines = 0,
        DeletedLines = 0,
    };
}

/// <summary>A contiguous block of changes.</summary>
public sealed record DiffHunk
{
    public required int OldStart { get; init; }
    public required int OldLineCount { get; init; }
    public required int NewStart { get; init; }
    public required int NewLineCount { get; init; }

    /// <summary>The <c>@@ … @@</c> line.</summary>
    public required string Header { get; init; }
    public required IReadOnlyList<DiffLine> Lines { get; init; }
}

/// <summary>A single line within a hunk.</summary>
public sealed record DiffLine
{
    public required DiffLineType Type { get; init; }
    public required string Content { get; init; }
    public required int? OldLineNumber { get; init; }
    public required int? NewLineNumber { get; init; }
}

public enum DiffLineType
{
    Context,
    Addition,
    Deletion,
}

/// <summary>Stable coordinate for a diff line: (hunk index, line index within hunk).</summary>
public readonly record struct DiffLineRef(int HunkIndex, int LineIndexInHunk);

public enum PatchScope { WholeFile, SingleHunk, SelectedLines }
public enum PatchTarget { StagingArea, WorkingTree }
public sealed record PatchCheckResult(bool CanApply, string? FailureReason);

/// <summary>What to diff. Exactly one source shape is set.</summary>
public sealed record DiffRequest
{
    public required DiffSource Source { get; init; }

    /// <summary>Repo-relative path to diff. Null diffs all changed paths for the source.</summary>
    public string? Path { get; init; }

    /// <summary>Commit id for <see cref="DiffSource.Commit"/>; "left" side for <see cref="DiffSource.Comparison"/>.</summary>
    public ObjectId? CommitId { get; init; }

    /// <summary>"right" side for <see cref="DiffSource.Comparison"/>.</summary>
    public ObjectId? OtherCommitId { get; init; }

    /// <summary>For a merge-commit diff: which parent to diff against (1-based). Default 1.</summary>
    public int ParentIndex { get; init; } = 1;

    public int ContextLines { get; init; } = 3;
    public WhitespaceMode Whitespace { get; init; } = WhitespaceMode.Show;
}

public enum DiffSource
{
    /// <summary>Index ↔ working tree.</summary>
    UnstagedChanges,

    /// <summary>HEAD ↔ index.</summary>
    StagedChanges,

    /// <summary>Commit ↔ its first parent (or empty tree for a root commit).</summary>
    Commit,

    /// <summary>Arbitrary ref ↔ ref.</summary>
    Comparison,
}
