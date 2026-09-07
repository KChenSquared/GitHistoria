namespace Historia.Models;

/// <summary>One row of a rebase todo list (§8.1.2). Target of a squash/fixup is expressed by
/// <b>ordering</b> — meld always goes into the preceding entry — never by a field.</summary>
public sealed record RebaseEntry
{
    public required ObjectId CommitId { get; init; }
    public required string MessageShort { get; init; }
    public required string AuthorName { get; init; }   // projected, never a LibGit2Sharp Signature
    public required string AuthorEmail { get; init; }
    public RebaseAction Action { get; init; } = RebaseAction.Pick;

    /// <summary>New message when <see cref="Action"/> is <see cref="RebaseAction.Reword"/>.</summary>
    public string? RewordMessage { get; init; }
}

public enum RebaseAction
{
    Pick,   // keep as-is
    Reword, // keep, replace the message
    Edit,   // stop after applying, for amending
    Squash, // meld into the PRECEDING entry, combining messages
    Fixup,  // meld into the PRECEDING entry, discarding this message
    Drop,   // remove the commit
}

public sealed record RebaseSession
{
    public required ObjectId OntoCommitId { get; init; }
    public required string OntoRefName { get; init; }
    public required string UpstreamRefName { get; init; }
    public required string SourceBranchName { get; init; }

    /// <summary>Oldest first — todo-file order.</summary>
    public required IReadOnlyList<RebaseEntry> Entries { get; init; }
    public RebaseState State { get; init; } = RebaseState.Editing;
}

public enum RebaseState { Editing, InProgress, Paused, Conflicted, Completed, Aborted }

/// <summary>A client-side validation problem found before the todo file is written (§8.1.2).</summary>
public sealed record RebaseValidationIssue(string Message, bool RequiresConfirmation);
