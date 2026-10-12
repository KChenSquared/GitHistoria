namespace Historia.Views;

/// <summary>
/// The context menu on a remote's row in the sidebar: Open In Browser · Fetch · Prune · Edit… ·
/// Set Push URL… · Delete… · Copy URL, plus the Add remote flow behind the Remotes header's button. Fetch and Prune go through <see cref="IRepoActionBus"/>
/// so they share the network runner's progress, busy guard and credential fallback; Prune's
/// confirmation lives in that runner. The items that change the repository call <see cref="RemoteActions"/>,
/// where Delete confirms in the method that removes the remote.
/// </summary>
internal static class RemoteMenu
{
    private static readonly RemoteDialogs RemoteWindows = new();

    public static List<Control> Build(
        RemoteInfo remote, RepositoryContext ctx, IDialogService dialogs, INotificationService notifications,
        IRepoActionBus actions, IExternalLauncher launcher, Func<string, Task?> copy)
    {
        var items = new List<Control>();

        if (RemoteWebUrl.TryGet(remote.Url, out var webUrl))
        {
            items.Add(MenuLeaf("Open In Browser", () => launcher.OpenUrl(webUrl)));
            items.Add(new Separator());
        }

        items.Add(MenuLeaf("Fetch", () => actions.RequestNetworkAction(
            new RepoNetworkRequest(RepoNetworkAction.Fetch, remote.Name, null))));
        items.Add(MenuLeaf("Prune", () => actions.RequestNetworkAction(
            new RepoNetworkRequest(RepoNetworkAction.Prune, remote.Name, null))));
        items.Add(new Separator());

        items.Add(MenuLeaf("Edit…", () => RemoteActions.EditAsync(remote, ctx, notifications, RemoteWindows)));
        items.Add(MenuLeaf("Set Push URL…", () => RemoteActions.SetPushUrlAsync(remote, ctx, dialogs, notifications)));
        items.Add(MenuLeaf("Delete…", () => RemoteActions.DeleteAsync(remote, ctx, dialogs, notifications)));
        items.Add(new Separator());

        items.Add(MenuLeaf("Copy URL", () => copy(remote.Url)));
        return items;
    }

    /// <summary>Adds a remote from the Add remote dialog; see <see cref="RemoteActions.AddAsync"/>.</summary>
    internal static Task AddAsync(RepositoryContext ctx, INotificationService notifications, IRepoActionBus actions) =>
        RemoteActions.AddAsync(ctx, notifications, actions, RemoteWindows);
}
