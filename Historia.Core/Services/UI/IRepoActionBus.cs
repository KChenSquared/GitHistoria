namespace Historia.Services.UI;

/// <summary>Network actions the v8 M5 shortcuts bar can request against the focused repository.</summary>
public enum RepoNetworkAction
{
    Fetch,
    Pull,
    Push,
}

/// <summary>A network action request, optionally targeted at a specific remote/branch pair
/// (e.g. a branch menu's "Push to origin"). <paramref name="Branch"/> is the local branch;
/// <paramref name="RemoteBranch"/> is its name on <paramref name="Remote"/> (null means the
/// same name as <paramref name="Branch"/>). <see langword="null"/> targets fall back to the
/// runner's usual resolution — <c>origin</c> if present, else the first remote, and the current
/// branch.</summary>
public readonly record struct RepoNetworkRequest(
    RepoNetworkAction Action,
    string? Remote,
    string? Branch,
    string? RemoteBranch = null);

/// <summary>
/// v8 M5: a thin app-wide bus so the shortcuts bar (owned by the window shell) can drive the
/// Fetch / Pull / Push flow that still lives in <c>RepositoryView</c> (which owns the inline
/// progress box and push-rejection remedies). Singleton.
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
