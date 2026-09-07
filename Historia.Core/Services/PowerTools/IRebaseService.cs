namespace Historia.Services.PowerTools;

/// <summary>
/// Interactive rebase, executed by Git core (§8.1.4). Historia writes the todo plan and drives
/// the sequencer through re-invoked-self helpers pointed at by <c>GIT_SEQUENCE_EDITOR</c> and
/// <c>GIT_EDITOR</c>. Continue / skip / abort route through <c>IConflictService</c>. Singleton.
/// </summary>
public interface IRebaseService
{
    /// <summary>Builds an all-<c>Pick</c> session for <c>upstream..HEAD</c>, oldest first.</summary>
    Task<RebaseSession> BuildSessionAsync(RepositoryContext context, string upstreamRef, CancellationToken ct = default);

    /// <summary>Client-side validation of the plan before anything is written (§8.1.2).</summary>
    IReadOnlyList<RebaseValidationIssue> Validate(RebaseSession session);

    /// <summary>
    /// Starts the rebase. When every entry is <c>Pick</c> in unchanged order this takes the
    /// plain non-interactive fast path; otherwise it writes the todo plan and drives the
    /// sequencer. Offers autostash when the working tree is dirty.
    /// </summary>
    Task StartAsync(RebaseSession session, RepositoryContext context, bool autostash, CancellationToken ct = default);
}
