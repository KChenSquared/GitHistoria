namespace Historia.Models;


/// <summary>Full detail for a single selected commit.</summary>
public sealed record CommitDetail
{
    public required GitObjectId Id { get; init; }
    /// <summary>The full message exactly as committed (subject, blank line, body), trailing newline trimmed.</summary>
    public string Message { get; init; } = "";
    public required string MessageShort { get; init; }
    /// <summary>The message body only — the subject line (<see cref="MessageShort"/>) is already stripped.</summary>
    public required string MessageBody { get; init; }
    public required string AuthorName { get; init; }
    public required string AuthorEmail { get; init; }
    public required DateTimeOffset AuthorWhen { get; init; }
    public required string CommitterName { get; init; }
    public required string CommitterEmail { get; init; }
    public required DateTimeOffset CommitWhen { get; init; }
    public required IReadOnlyList<GitObjectId> ParentIds { get; init; }
    /// <summary>Decorations for the commit; not populated on the single-commit detail path for performance.</summary>
    public IReadOnlyList<CommitDecoration> Decorations { get; init; } = [];
}

/// <summary>A ref pointer (branch/tag/HEAD/stash) attached to a commit row.</summary>
public readonly record struct CommitDecoration
{
    public required DecorationType Type { get; init; }

    /// <summary>e.g. "main", "v2.1.0", "origin/main".</summary>
    public required string Label { get; init; }
}
