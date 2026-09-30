namespace Historia.Views;

/// <summary>
/// The context menu on a remote's row in the sidebar: Open In Browser · Fetch · Prune · Edit… ·
/// Set Push URL… · Delete… · Copy URL. Fetch and Prune go through <see cref="IRepoActionBus"/>
/// so they share the network runner's progress, busy guard and credential fallback; Prune's
/// confirmation lives in that runner. Delete confirms here, in the method that removes the remote.
/// </summary>
internal static class RemoteMenu
{
    internal enum PushUrlAction
    {
        NoOp,
        Clear,
        Set,
    }

    /// <summary>What saving the Set Push URL prompt should do.</summary>
    internal readonly record struct PushUrlChange(PushUrlAction Action, string? Url = null)
    {
        internal static PushUrlChange NoOp => new(PushUrlAction.NoOp);
        internal static PushUrlChange Clear => new(PushUrlAction.Clear);
        internal static PushUrlChange Set(string url) => new(PushUrlAction.Set, url);
    }

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

        items.Add(MenuLeaf("Edit…", () => EditAsync(remote, ctx, notifications)));
        items.Add(MenuLeaf("Set Push URL…", () => SetPushUrlAsync(remote, ctx, dialogs, notifications)));
        items.Add(MenuLeaf("Delete…", () => DeleteAsync(remote, ctx, dialogs, notifications)));
        items.Add(new Separator());

        items.Add(MenuLeaf("Copy URL", () => copy(remote.Url)));
        return items;
    }

    /// <summary>The push-URL change implied by <paramref name="input"/>. Empty input, or the
    /// fetch URL itself, means "push to the fetch URL": that clears a separate push URL when one
    /// exists and is otherwise nothing to do. Re-entering the current push URL changes nothing.</summary>
    internal static PushUrlChange DecidePushUrl(RemoteInfo remote, string? input)
    {
        var trimmed = input?.Trim() ?? "";
        if (trimmed.Length == 0 || string.Equals(trimmed, remote.Url, StringComparison.Ordinal))
            return remote.HasDistinctPushUrl ? PushUrlChange.Clear : PushUrlChange.NoOp;
        if (remote.HasDistinctPushUrl && string.Equals(trimmed, remote.PushUrl, StringComparison.Ordinal))
            return PushUrlChange.NoOp;
        return PushUrlChange.Set(trimmed);
    }

    /// <summary>The confirmation text for deleting a remote: what goes, and what stays.</summary>
    internal static string DeleteEffect(string name, string url, int trackingBranchCount)
    {
        var branches = trackingBranchCount switch
        {
            0 => "It has no remote-tracking branches.",
            1 => $"Its 1 remote-tracking branch ({name}/…) is deleted too.",
            _ => $"Its {trackingBranchCount} remote-tracking branches ({name}/…) are deleted too.",
        };
        return $"Delete remote '{name}' ({url})? Its configuration is removed. {branches} " +
               "Local branches that track it lose their upstream. Commits reachable only from its " +
               "remote-tracking branches become unreferenced locally; anything your local branches " +
               "or tags reach is kept. Nothing on the server is changed.";
    }

    private static async Task RefreshAsync(RepositoryContext ctx)
    {
        try { await ctx.State.RefreshAllAsync(ctx.Lifetime); }
        catch (OperationCanceledException) { /* tab closed or torn down mid-operation — not a failure */ }
    }

    private static async Task SetPushUrlAsync(
        RemoteInfo remote, RepositoryContext ctx, IDialogService dialogs, INotificationService notifications)
    {
        var input = await dialogs.PromptAsync("Set push URL",
            $"Push URL for '{remote.Name}' (leave empty to push to the fetch URL):",
            remote.HasDistinctPushUrl ? remote.PushUrl : remote.Url, "Save", primaryConfirm: true);
        if (input is null)
            return;

        var change = DecidePushUrl(remote, input);
        if (change.Action == PushUrlAction.NoOp)
            return;

        try
        {
            await ctx.Gateway.SetRemotePushUrlAsync(
                remote.Name, change.Action == PushUrlAction.Set ? change.Url : null, ctx.Lifetime);
            await RefreshAsync(ctx);
        }
        catch (OperationCanceledException) { /* tab closed or torn down mid-operation — not a failure */ }
        catch (Exception ex)
        {
            notifications.Publish(NotificationSeverity.Error, $"Set push URL failed: {ex.Message}", ex.NotificationDetails());
            await RefreshAsync(ctx);
        }
    }

    /// <summary>Applies the Edit dialog's result in order — rename, then fetch URL, then push-URL
    /// reset — and stops at the first failure, naming the step. The sidebar is refreshed either
    /// way so it shows whatever was applied.</summary>
    private static async Task EditAsync(RemoteInfo remote, RepositoryContext ctx, INotificationService notifications)
    {
        var otherNames = ctx.State.Remotes
            .Where(r => !string.Equals(r.Name, remote.Name, StringComparison.Ordinal))
            .Select(r => r.Name)
            .ToList();
        var edit = await EditRemoteDialog.ShowAsync(remote, otherNames);
        if (edit is null)
            return;

        var name = remote.Name;
        var step = "";
        try
        {
            if (!string.Equals(edit.Name, remote.Name, StringComparison.Ordinal))
            {
                step = "renaming the remote";
                var untouched = await ctx.Gateway.RenameRemoteAsync(remote.Name, edit.Name, ctx.Lifetime);
                name = edit.Name;
                if (untouched.Count > 0)
                    notifications.Publish(NotificationSeverity.Warning,
                        $"'{remote.Name}' renamed to '{edit.Name}', but these fetch refspecs need manual fixing: {string.Join(", ", untouched)}");
            }

            if (!string.Equals(edit.Url, remote.Url, StringComparison.Ordinal))
            {
                step = "setting the URL";
                await ctx.Gateway.SetRemoteUrlAsync(name, edit.Url, ctx.Lifetime);
            }

            if (edit.ResetPushUrl)
            {
                step = "resetting the push URL";
                await ctx.Gateway.SetRemotePushUrlAsync(name, null, ctx.Lifetime);
            }
        }
        catch (OperationCanceledException) { /* tab closed or torn down mid-operation — not a failure */ }
        catch (Exception ex)
        {
            notifications.Publish(NotificationSeverity.Error,
                $"Edit remote failed while {step}: {ex.Message}", ex.NotificationDetails());
        }
        await RefreshAsync(ctx);
    }

    private static async Task DeleteAsync(
        RemoteInfo remote, RepositoryContext ctx, IDialogService dialogs, INotificationService notifications)
    {
        var trackingBranches = ctx.State.Branches.Count(b =>
            b.IsRemote && string.Equals(b.RemoteName, remote.Name, StringComparison.Ordinal));
        if (!await dialogs.ConfirmDestructiveAsync("Delete remote — destructive",
                DeleteEffect(remote.Name, remote.Url, trackingBranches), "Delete"))
            return;

        try
        {
            await ctx.Gateway.RemoveRemoteAsync(remote.Name, ctx.Lifetime);
            await ctx.State.RefreshAllAsync(ctx.Lifetime);
            notifications.Publish(NotificationSeverity.Success, $"Deleted remote '{remote.Name}'.");
        }
        catch (OperationCanceledException) { /* tab closed or torn down mid-operation — not a failure */ }
        catch (Exception ex)
        {
            notifications.Publish(NotificationSeverity.Error, $"Delete remote failed: {ex.Message}", ex.NotificationDetails());
            await RefreshAsync(ctx);
        }
    }
}
