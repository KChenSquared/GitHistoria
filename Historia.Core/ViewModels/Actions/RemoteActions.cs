namespace Historia.ViewModels;

/// <summary>
/// The Git operations behind a remote's context menu and the Add remote flow: add, edit, set the push
/// URL and delete. Dialogs come in through <see cref="IRemoteDialogs"/> and <see cref="IDialogService"/>,
/// so the actions run without a window. Delete confirms here, in the method that removes the remote.
/// </summary>
public static class RemoteActions
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

    /// <summary>Asks for a push URL for <paramref name="remote"/> and applies what
    /// <see cref="DecidePushUrl"/> makes of the answer.</summary>
    public static async Task SetPushUrlAsync(
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
            notifications.Publish(NotificationSeverity.Error, ex.NotificationMessage("Set push URL"), ex.NotificationDetails());
            await RefreshAsync(ctx);
        }
    }

    /// <summary>Applies the Edit dialog's result in order — rename, then fetch URL, then push-URL
    /// reset — and stops at the first failure, naming the step. The sidebar is refreshed either
    /// way so it shows whatever was applied.</summary>
    public static async Task EditAsync(
        RemoteInfo remote, RepositoryContext ctx, INotificationService notifications, IRemoteDialogs remoteDialogs)
    {
        var others = ctx.State.Remotes
            .Where(r => !string.Equals(r.Name, remote.Name, StringComparison.Ordinal))
            .ToList();
        var edit = await remoteDialogs.ShowEditAsync(remote, others.Select(r => r.Name).ToList(), RemoteDialogViewModel.UrlOwners(others));
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

    /// <summary>Adds a remote from the Add remote dialog. When the user asked for it, the new
    /// remote is then fetched through <see cref="IRepoActionBus"/>, so the fetch shares the network
    /// runner's progress, busy guard and credential fallback.</summary>
    public static async Task AddAsync(
        RepositoryContext ctx, INotificationService notifications, IRepoActionBus actions, IRemoteDialogs remoteDialogs)
    {
        var add = await remoteDialogs.ShowAddAsync(ctx.State.Remotes);
        if (add is null)
            return;

        try
        {
            await ctx.Gateway.AddRemoteAsync(add.Name, add.Url, ctx.Lifetime);
            await RefreshAsync(ctx);
            notifications.Publish(NotificationSeverity.Success, $"Added remote '{add.Name}'.");
            if (add.Fetch)
                actions.RequestNetworkAction(new RepoNetworkRequest(RepoNetworkAction.Fetch, add.Name, null, Repo: ctx));
        }
        catch (OperationCanceledException) { /* tab closed or torn down mid-operation — not a failure */ }
        catch (Exception ex)
        {
            notifications.Publish(NotificationSeverity.Error, ex.NotificationMessage("Add remote"), ex.NotificationDetails());
            await RefreshAsync(ctx);
        }
    }

    /// <summary>Confirms, naming what goes and what stays, then removes <paramref name="remote"/>.</summary>
    public static async Task DeleteAsync(
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
            notifications.Publish(NotificationSeverity.Error, ex.NotificationMessage("Delete remote"), ex.NotificationDetails());
            await RefreshAsync(ctx);
        }
    }
}
