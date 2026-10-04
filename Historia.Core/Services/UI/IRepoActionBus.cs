namespace Historia.Services.UI;

/// <summary>Network actions requested against the focused repository, from the shortcuts bar
/// or from menus that target one remote.</summary>
public enum RepoNetworkAction
{
    Fetch,
    Pull,
    Push,
    Prune,
}

/// <summary>A network action request, optionally targeted at a specific remote/branch pair
/// (e.g. a branch menu's "Push to origin"). <paramref name="Branch"/> is the local branch;
/// <paramref name="RemoteBranch"/> is its name on <paramref name="Remote"/> (null means the
/// same name as <paramref name="Branch"/>). <see langword="null"/> targets fall back to the
/// runner's usual resolution — <c>origin</c> if present, else the first remote, and the current
/// branch. <paramref name="Repo"/> pins the request to one repository (e.g. the staging panel's
/// own, whichever tab is focused by the time it runs); null means the focused repository.</summary>
public readonly record struct RepoNetworkRequest(
    RepoNetworkAction Action,
    string? Remote,
    string? Branch,
    string? RemoteBranch = null,
    RepositoryContext? Repo = null);

/// <summary>
/// A thin app-wide bus so any surface — the shortcuts bar, branch menus, the staging panel —
/// can hand a Fetch / Pull / Push to the one network-action runner, which owns the progress
/// box, cancellation, the busy guard and the recovery flows. Singleton.
/// </summary>
public interface IRepoActionBus
{
    event EventHandler<RepoNetworkRequest>? NetworkActionRequested;

    void RequestNetworkAction(RepoNetworkAction action);
    void RequestNetworkAction(RepoNetworkRequest request);
}

/// <inheritdoc cref="IRepoActionBus"/>
public sealed class RepoActionBus : IRepoActionBus
{
    public event EventHandler<RepoNetworkRequest>? NetworkActionRequested;

    public void RequestNetworkAction(RepoNetworkAction action) => RequestNetworkAction(new RepoNetworkRequest(action, null, null));

    public void RequestNetworkAction(RepoNetworkRequest request) => NetworkActionRequested?.Invoke(this, request);
}
