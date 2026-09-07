namespace Historia.Services.UI;

/// <summary>Network actions the v8 M5 shortcuts bar can request against the focused repository.</summary>
public enum RepoNetworkAction
{
    Fetch,
    Pull,
    Push,
}

/// <summary>
/// v8 M5: a thin app-wide bus so the shortcuts bar (owned by the window shell) can drive the
/// Fetch / Pull / Push flow that still lives in <c>RepositoryView</c> (which owns the inline
/// progress box and push-rejection remedies). Singleton.
/// </summary>
public interface IRepoActionBus
{
    event EventHandler<RepoNetworkAction>? NetworkActionRequested;

    void RequestNetworkAction(RepoNetworkAction action);
}

/// <inheritdoc cref="IRepoActionBus"/>
public sealed class RepoActionBus : IRepoActionBus
{
    public event EventHandler<RepoNetworkAction>? NetworkActionRequested;

    public void RequestNetworkAction(RepoNetworkAction action) => NetworkActionRequested?.Invoke(this, action);
}
