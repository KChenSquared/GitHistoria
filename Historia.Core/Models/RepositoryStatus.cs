namespace Historia.Models;

/// <summary>
/// Working-directory status snapshot. Paths are always repo-relative with forward
/// slashes, matching Git's own representation; conversion to Windows paths happens only at the
/// filesystem boundary.
/// </summary>
public sealed record RepositoryStatus
{
    public required IReadOnlyList<FileStatusEntry> Staged { get; init; }
    public required IReadOnlyList<FileStatusEntry> Unstaged { get; init; }
    public required IReadOnlyList<FileStatusEntry> Untracked { get; init; }
    public required IReadOnlyList<FileStatusEntry> Conflicted { get; init; }
    public required bool IsDirty { get; init; }
    public required int AheadBy { get; init; }
    public required int BehindBy { get; init; }
    public required DateTimeOffset ComputedAt { get; init; }

    public static RepositoryStatus Empty { get; } = new()
    {
        Staged = [],
        Unstaged = [],
        Untracked = [],
        Conflicted = [],
        IsDirty = false,
        AheadBy = 0,
        BehindBy = 0,
        ComputedAt = DateTimeOffset.MinValue,
    };
}

/// <summary>A single working-tree change entry.</summary>
public sealed record FileStatusEntry
{
    /// <summary>Repo-relative, forward slashes.</summary>
    public required string Path { get; init; }
    public required FileChangeKind Kind { get; init; }

    /// <summary>Set when <see cref="Kind"/> is <see cref="FileChangeKind.Renamed"/> or <see cref="FileChangeKind.Copied"/>.</summary>
    public string? OldPath { get; init; }
    public required bool IsBinary { get; init; }
    public required bool IsSubmodule { get; init; }
    public long SizeBytes { get; init; }
}
