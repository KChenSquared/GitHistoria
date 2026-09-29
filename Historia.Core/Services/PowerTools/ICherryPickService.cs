namespace Historia.Services.PowerTools;

/// <summary>
/// Cherry-pick and revert of one commit or an ordered set. Commits must be passed
/// <b>oldest-first</b> regardless of ctrl-click order. Merge commits require a mainline parent.
/// On conflict the sequencer stops and <c>IConflictService</c> drives continue/skip/abort.
/// Records the prior HEAD for the session-scoped undo. Singleton.
/// </summary>
public interface ICherryPickService
{
    Task CherryPickAsync(RepositoryContext context, IReadOnlyList<GitObjectId> oldestFirst,
        bool noCommit = false, int? mainline = null, CancellationToken ct = default);

    Task RevertAsync(RepositoryContext context, IReadOnlyList<GitObjectId> oldestFirst,
        int? mainline = null, CancellationToken ct = default);

    /// <summary>The HEAD recorded before the last history-rewriting op this session, for "Undo".</summary>
    string? LastOrigHead(RepositoryContext context);

    Task UndoToOrigHeadAsync(RepositoryContext context, CancellationToken ct = default);
}
