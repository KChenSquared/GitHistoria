namespace Historia.ViewModels;

/// <summary>
/// The Git operations behind the branch context menus (graph pill, sidebar row) and the other
/// surfaces that create, check out, merge or delete branches. Each method guards, refreshes
/// repository state and reports failures itself, so callers only supply the collaborators.
/// </summary>
public static class BranchActions
{
    /// <summary>Checks out the existing local branch <paramref name="name"/>. A remote row whose short
    /// name already exists locally takes this path; only a remote row with no local counterpart
    /// materialises a tracking branch (<see cref="CheckoutTrackingAsync"/>).</summary>
    public static Task CheckoutAsync(RepositoryContext ctx, INotificationService notify, IStashService stash, string name) =>
        RunWithStashRecovery(ctx, notify, stash,
            () => ctx.Gateway.CheckoutAsync(name, new GitCheckoutOptions(), ctx.Lifetime), "Checkout");

    /// <summary>Creates the local branch <paramref name="name"/> from <paramref name="trackingRef"/>, checks
    /// it out and wires its upstream to that ref.</summary>
    public static Task CheckoutTrackingAsync(
        RepositoryContext ctx, INotificationService notify, IStashService stash, string name, string trackingRef) =>
        RunWithStashRecovery(ctx, notify, stash, async () =>
        {
            await ctx.Gateway.CreateBranchAsync(name, trackingRef, checkout: true, ctx.Lifetime);
            await ctx.Gateway.SetUpstreamAsync(name, trackingRef, ctx.Lifetime);
        }, "Checkout");

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
    /// Offers a "Stash & retry" recovery action in place of a dead-end error.</summary>
    public static async Task RunWithStashRecovery(RepositoryContext ctx, INotificationService notify,
        IStashService stash, Func<Task> op, string what)
    {
        try
        {
            await op();
            await ctx.State.RefreshAllAsync(ctx.Lifetime);
        }
        catch (HistoriaGitException ex) when (ex.Kind == GitErrorKind.UncommittedChanges)
        {
            notify.Publish(NotificationSeverity.Error, ex.Message, ex.Details,
            [
                new NotificationAction("Stash & retry", () => StashAndRetryAsync(ctx, notify, stash, op, what)),
            ]);
        }
        catch (Exception ex)
        {
            notify.Publish(NotificationSeverity.Error, ex.NotificationMessage(what), ex.NotificationDetails());
        }
    }

    /// <summary>Stashes (including untracked files), retries <paramref name="op"/>, then pops the
    /// stash back — even when the retry itself fails, so a failed retry never also stands between
    /// the user and the working tree they had a moment ago. If popping the stash back conflicts,
    /// it is deliberately left in the stash list (git's own behaviour) rather than dropped; the
    /// conflict markers land in the working tree and the existing conflict-resolver panel picks
    /// them up on the refresh below.</summary>
    public static async Task StashAndRetryAsync(RepositoryContext ctx, INotificationService notify,
        IStashService stash, Func<Task> op, string what)
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
            catch (Exception ex) { notify.Publish(NotificationSeverity.Error, ex.NotificationMessage(what), ex.NotificationDetails()); }
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
    }
}
