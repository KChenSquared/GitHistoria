namespace Historia.Models;

/// <summary>
/// One stash entry. <see cref="CommitId"/> is the stable identity — every operation
/// is keyed on it, never on <see cref="Index"/>, which is a stack position that shifts on
/// every drop/pop.
/// </summary>
public sealed record StashEntry
{
    /// <summary>DISPLAY ONLY. Never key an operation on this.</summary>
    public required int Index { get; init; }

    /// <summary>The stash commit's own id — the stable identity.</summary>
    public required ObjectId CommitId { get; init; }

    public required string Message { get; init; }
    public required string AuthorName { get; init; }
    public required string AuthorEmail { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required string BranchName { get; init; }
    public int FileCount { get; init; }
    public bool IncludesUntracked { get; init; }
}
