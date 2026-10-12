namespace Historia.ViewModels;

/// <summary>
/// The Git operations behind the branch context menus (graph pill, sidebar row) and the other
/// surfaces that create, check out, merge or delete branches. Each method guards, refreshes
/// repository state and reports failures itself, so callers only supply the collaborators.
/// </summary>
public static class BranchActions
{
    /// <summary>Checks out <paramref name="branch"/>. A remote branch whose local name already exists
    /// checks that local branch out; a remote branch with no local counterpart materialises a tracking
    /// branch. <paramref name="onSwitched"/> receives the local name once HEAD has moved to it, on
    /// the first attempt or after a successful "Stash &amp; retry"; checking out the branch HEAD is
    /// already on does not call it.</summary>
    public static Task CheckoutBranchAsync(RepositoryContext ctx, INotificationService notify, IStashService stash,
        BranchInfo branch, Action<string>? onSwitched = null) =>
        CheckoutBranchAsync(ctx, notify, stash, LocalName(branch), branch.Name, !branch.IsRemote, onSwitched);

    /// <summary>The same decision for callers that already hold the local name and the ref to track:
    /// <paramref name="trackingRef"/> is only used when the row is remote
    /// (<paramref name="isLocal"/> false) and no local branch called <paramref name="name"/> exists.</summary>
    public static Task CheckoutBranchAsync(RepositoryContext ctx, INotificationService notify, IStashService stash,
        string name, string trackingRef, bool isLocal, Action<string>? onSwitched = null)
    {
        var alreadyCurrent = string.Equals(ctx.State.CurrentBranch?.Name, name, StringComparison.Ordinal);
        Action? onSuccess = onSwitched is null || alreadyCurrent
            ? null
            : () =>
            {
                // The checkout reports its own failures; the refreshed state says whether it landed.
                if (string.Equals(ctx.State.CurrentBranch?.Name, name, StringComparison.Ordinal))
                    onSwitched(name);
            };

        var localExists = ctx.State.Branches.Any(x => !x.IsRemote && string.Equals(x.Name, name, StringComparison.Ordinal));
        return !isLocal && !localExists
            ? CheckoutTrackingAsync(ctx, notify, stash, name, trackingRef, onSuccess)
            : CheckoutAsync(ctx, notify, stash, name, onSuccess);
    }

    /// <summary>The local branch name for <paramref name="branch"/>: a remote branch loses its remote
    /// prefix (its <see cref="BranchInfo.RemoteName"/>, which may itself contain a slash, or the first
    /// segment when that is unknown); a local branch keeps its name.</summary>
    public static string LocalName(BranchInfo branch)
    {
        if (!branch.IsRemote)
            return branch.Name;
        if (branch.RemoteName is { } remote && branch.Name.StartsWith(remote + "/", StringComparison.Ordinal))
            return branch.Name[(remote.Length + 1)..];
        var slash = branch.Name.IndexOf('/');
        return slash >= 0 ? branch.Name[(slash + 1)..] : branch.Name;
    }

    /// <summary>The success notice the sidebar shows once a checkout has switched to <paramref name="name"/>.</summary>
    public static void AnnounceSwitch(INotificationService notify, string name) =>
        notify.Publish(NotificationSeverity.Success, $"Switched to {name}.");

    /// <summary>Checks out the existing local branch <paramref name="name"/>. <paramref name="onSuccess"/>
    /// runs once the checkout and the state refresh have succeeded, including after a successful
    /// "Stash &amp; retry".</summary>
    public static Task CheckoutAsync(RepositoryContext ctx, INotificationService notify, IStashService stash,
        string name, Action? onSuccess = null) =>
        RunWithStashRecovery(ctx, notify, stash,
            () => ctx.Gateway.CheckoutAsync(name, new GitCheckoutOptions(), ctx.Lifetime), "Checkout", onSuccess);

    /// <summary>Creates the local branch <paramref name="name"/> from <paramref name="trackingRef"/>, checks
    /// it out and wires its upstream to that ref. <paramref name="onSuccess"/> runs as for
    /// <see cref="CheckoutAsync"/>.</summary>
    public static Task CheckoutTrackingAsync(RepositoryContext ctx, INotificationService notify, IStashService stash,
        string name, string trackingRef, Action? onSuccess = null) =>
        RunWithStashRecovery(ctx, notify, stash, async () =>
        {
            await ctx.Gateway.CreateBranchAsync(name, trackingRef, checkout: true, ctx.Lifetime);
            await ctx.Gateway.SetUpstreamAsync(name, trackingRef, ctx.Lifetime);
        }, "Checkout", onSuccess);

    /// <summary>Asks for a branch name and creates it at <paramref name="sha"/>, checking it out.
    /// Unlike creating a branch at the current tip, this can check out a different commit than HEAD,
    /// so the same "commit or stash first" refusal as any other checkout applies.</summary>
    public static async Task CreateBranchAsync(RepositoryContext ctx, IDialogService dialogs,
        INotificationService notify, IStashService stash, string sha, string shortSha)
    {
        var name = await dialogs.PromptAsync("New branch", $"Branch name (from {shortSha}):");
        if (string.IsNullOrWhiteSpace(name)) return;
        await RunWithStashRecovery(ctx, notify, stash,
            () => ctx.Gateway.CreateBranchAsync(name.Trim(), sha, checkout: true, ctx.Lifetime), "Create branch");
    }

    /// <summary>Asks for a tag name and creates a lightweight tag at <paramref name="sha"/>.</summary>
    public static async Task CreateTagAsync(RepositoryContext ctx, IDialogService dialogs,
        INotificationService notify, string sha, string shortSha)
    {
        var name = await dialogs.PromptAsync("New tag", $"Tag name (at {shortSha}):");
        if (string.IsNullOrWhiteSpace(name)) return;
        try
        {
            await ctx.Gateway.CreateTagAsync(name.Trim(), sha, null, ctx.Lifetime);
            await ctx.State.RefreshAllAsync(ctx.Lifetime);
        }
        catch (Exception ex) { notify.Publish(NotificationSeverity.Error, ex.NotificationMessage("Create tag"), ex.NotificationDetails()); }
    }

    /// <summary>Renames the local branch <paramref name="name"/> to <paramref name="newName"/>.</summary>
    public static async Task RenameAsync(RepositoryContext ctx, INotificationService notify, string name, string newName)
    {
        try
        {
            await ctx.Gateway.RenameBranchAsync(name, newName, ctx.Lifetime);
            await ctx.State.RefreshAllAsync(ctx.Lifetime);
        }
        catch (Exception ex) { notify.Publish(NotificationSeverity.Error, ex.NotificationMessage("Rename"), ex.NotificationDetails()); }
    }

    /// <summary>Confirms, then merges <paramref name="mergeRef"/> into the checked-out branch. Reports a
    /// merge that paused on conflicts as a warning so the user knows to resolve and commit.</summary>
    public static async Task MergeIntoCurrentAsync(RepositoryContext ctx, IDialogService dialogs,
        INotificationService notify, IStashService stash, string mergeRef, string? currentBranch)
    {
        if (!await dialogs.ConfirmAsync("Merge", $"Merge {mergeRef} into {currentBranch ?? "the current branch"}?")) return;
        await RunWithStashRecovery(ctx, notify, stash, async () =>
        {
            await ctx.Gateway.MergeAsync(mergeRef, noFastForward: false, ctx.Lifetime);
            await ctx.State.RefreshAllAsync(ctx.Lifetime);
            var conflicts = ctx.State.Status.Conflicted.Count;
            notify.Publish(conflicts > 0 ? NotificationSeverity.Warning : NotificationSeverity.Success,
                conflicts > 0 ? $"Merge paused on {conflicts} conflict(s) — resolve, then commit." : $"Merged {mergeRef}.");
        }, "Merge");
    }

    /// <summary>Shared by the sidebar branch-row menu and the graph pill menu. A plain
    /// delete mirrors <c>git branch -d</c>'s safety check — refuses an unmerged branch with no
    /// upstream; on exactly that failure, offers a "Force delete" recovery action (its own
    /// destructive confirm) instead of just dumping the exception.</summary>
    public static async Task DeleteAsync(RepositoryContext ctx, IDialogService dialogs, INotificationService notify, string name)
    {
        if (!await dialogs.ConfirmDestructiveAsync("Delete branch", $"Delete '{name}'?", "Delete"))
            return;
        try
        {
            await ctx.Gateway.DeleteBranchAsync(name, force: false, ctx.Lifetime);
            await ctx.State.RefreshAllAsync(ctx.Lifetime);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("not fully merged", StringComparison.OrdinalIgnoreCase))
        {
            // Local-only risk, not a remote one: force-deleting can only strand commits that
            // exist nowhere else in this repo (recoverable from the reflog for a while, but not
            // guaranteed) — it never touches anything on a remote.
            notify.Publish(NotificationSeverity.Error, ex.Message, null,
            [
                new NotificationAction("Force delete", async () =>
                {
                    if (!await dialogs.ConfirmDestructiveAsync("Force delete branch",
                            $"'{name}' has commits that aren't merged anywhere else. Force-deleting can make them unreachable. Continue?",
                            "Force delete"))
                        return;
                    try { await ctx.Gateway.DeleteBranchAsync(name, force: true, ctx.Lifetime); }
                    finally { await ctx.State.RefreshAllAsync(ctx.Lifetime); }
                }),
            ]);
        }
        catch (Exception ex)
        {
            notify.Publish(NotificationSeverity.Error, ex.NotificationMessage("Delete branch"), ex.NotificationDetails());
        }
    }

    /// <summary>Shared by every checkout/pull/merge call site that can hit git's "commit your
    /// changes or stash them first" refusal (<see cref="GitErrorKind.UncommittedChanges"/>).
    /// Offers a "Stash &amp; retry" recovery action in place of a dead-end error.
    /// <paramref name="onSuccess"/> runs once <paramref name="op"/> and the state refresh have
    /// succeeded: on the first attempt, or after the recovery action's retry succeeded.</summary>
    public static async Task RunWithStashRecovery(RepositoryContext ctx, INotificationService notify,
        IStashService stash, Func<Task> op, string what, Action? onSuccess = null)
    {
        var succeeded = false;
        try
        {
            await op();
            await ctx.State.RefreshAllAsync(ctx.Lifetime);
            succeeded = true;
        }
        catch (HistoriaGitException ex) when (ex.Kind == GitErrorKind.UncommittedChanges)
        {
            notify.Publish(NotificationSeverity.Error, ex.Message, ex.Details,
            [
                new NotificationAction("Stash & retry", () => StashAndRetryAsync(ctx, notify, stash, op, what, onSuccess)),
            ]);
        }
        catch (Exception ex)
        {
            notify.Publish(NotificationSeverity.Error, ex.NotificationMessage(what), ex.NotificationDetails());
        }

        if (succeeded)
            onSuccess?.Invoke();
    }

    /// <summary>Stashes (including untracked files), retries <paramref name="op"/>, then pops the
    /// stash back — even when the retry itself fails, so a failed retry never also stands between
    /// the user and the working tree they had a moment ago. If popping the stash back conflicts,
    /// it is deliberately left in the stash list (git's own behaviour) rather than dropped; the
    /// conflict markers land in the working tree and the existing conflict-resolver panel picks
    /// them up on the refresh below. <paramref name="onSuccess"/> runs only when the retried
    /// <paramref name="op"/> succeeded, after the stash was popped back and the state refreshed; a
    /// stash that could not be restored does not stop it, since the operation itself completed.</summary>
    public static async Task StashAndRetryAsync(RepositoryContext ctx, INotificationService notify,
        IStashService stash, Func<Task> op, string what, Action? onSuccess = null)
    {
        bool stashed;
        try
        {
            stashed = await stash.SaveAsync(ctx,
                new StashSaveOptions { Message = $"Historia: before {what}", IncludeUntracked = true }, ctx.Lifetime);
        }
        catch (Exception ex)
        {
            notify.Publish(NotificationSeverity.Error, ex.NotificationMessage("Stash"), ex.NotificationDetails());
            return;
        }

        // Nothing left to stash (state moved on since the original failure) — just retry plainly.
        if (!stashed)
        {
            try { await op(); await ctx.State.RefreshAllAsync(ctx.Lifetime); }
            catch (Exception ex)
            {
                notify.Publish(NotificationSeverity.Error, ex.NotificationMessage(what), ex.NotificationDetails());
                return;
            }
            onSuccess?.Invoke();
            return;
        }

        var ourStash = (await stash.ListAsync(ctx, ctx.Lifetime)).FirstOrDefault(); // stash@{0} is what we just pushed

        Exception? failure = null;
        try { await op(); }
        catch (Exception ex) { failure = ex; }

        if (ourStash is not null)
        {
            try { await stash.ApplyAndDropAsync(ctx, ourStash.CommitId, ctx.Lifetime); }
            catch (Exception popEx)
            {
                notify.Publish(NotificationSeverity.Warning,
                    "Your stashed changes could not be restored automatically — they're still in the stash list.",
                    popEx.NotificationDetails());
            }
        }

        await ctx.State.RefreshAllAsync(ctx.Lifetime);

        if (failure is not null)
            notify.Publish(NotificationSeverity.Error, $"{what} failed: {failure.Message}", failure.NotificationDetails());
        else
            onSuccess?.Invoke();
    }
}
