using LibGit2Sharp;

namespace Historia.Models;

/// <summary>
/// Lightweight commit row for the history list (§6.11) and, in Phase 2, the graph. Projected
/// from LibGit2Sharp inside the repository gate (§6.3.1) — no live Git object is retained.
/// </summary>
public sealed record CommitSummary
{
    public required ObjectId Id { get; init; }
    public required string MessageShort { get; init; }
    public required string AuthorName { get; init; }
    public required string AuthorEmail { get; init; }
    public required DateTimeOffset AuthorWhen { get; init; }
    public required int ParentCount { get; init; }
    public required IReadOnlyList<CommitDecoration> Decorations { get; init; }
}

/// <summary>Full detail for a single selected commit (§6.4.2).</summary>
public sealed record CommitDetail
{
    public required ObjectId Id { get; init; }
    public required string MessageShort { get; init; }
    /// <summary>The message body only — the subject line (<see cref="MessageShort"/>) is already stripped.</summary>
    public required string MessageBody { get; init; }
    public required string AuthorName { get; init; }
    public required string AuthorEmail { get; init; }
    public required DateTimeOffset AuthorWhen { get; init; }
    public required string CommitterName { get; init; }
    public required string CommitterEmail { get; init; }
    public required DateTimeOffset CommitWhen { get; init; }
    public required IReadOnlyList<ObjectId> ParentIds { get; init; }
    /// <summary>Decorations for the commit; not populated on the single-commit detail path for performance.</summary>
    public IReadOnlyList<CommitDecoration> Decorations { get; init; } = [];
}

/// <summary>A ref pointer (branch/tag/HEAD/stash) attached to a commit row (§6.11).</summary>
public readonly record struct CommitDecoration
{
    public required DecorationType Type { get; init; }

    /// <summary>e.g. "main", "v2.1.0", "origin/main".</summary>
    public required string Label { get; init; }
}

/// <summary>
/// Paged history query (§6.4.2). History is <b>always</b> paged — no call ever materializes
/// an unbounded commit list.
/// </summary>
public sealed record CommitQuery
{
    public int Skip { get; init; }
    public int Take { get; init; } = 500;
    public string? PathFilter { get; init; }
    public CommitRefScope RefScope { get; init; } = CommitRefScope.AllRefs;

    /// <summary>Free-text filter over message and author (server-side, via the query — never client-side filtering of a loaded page).</summary>
    public string? SearchText { get; init; }
}
