namespace Historia.Views;

/// <summary>
/// The branch context-menu items common to GitKraken · Fork · GitComet · GitHub Desktop,
/// shared by the graph branch-label pill menu and the sidebar branch rows. Each item does its
/// own guard + <c>RefreshAllAsync</c> + toast so callers only supply the collaborators.
/// </summary>
internal static class BranchMenu
{
    internal readonly record struct Target(
        string Name,            // short branch name, e.g. "feature/x"
        string CopyName,        // "Copy branch name" payload (canonical for a remote ref)
        string TrackingRef,     // the ref to base a tracking local on for a remote row
        bool IsLocal,
        string? TipSha,         // for "Create branch/tag here"; null → omit those
        string? CurrentBranch,
        // The counterpart ref to measure divergence against — a local row's upstream
        // (e.g. "origin/main"), or a remote row's local branch of the same short name. Null
        // when there is no counterpart (no upstream / no local branch), in which case the menu
        // gets no divergence items.
        string? CounterpartRef = null,
        // The owning remote's alias (e.g. "origin"), resolved via BranchInfo.RemoteName rather
        // than string-splitting Name/UpstreamName, which may itself contain a slash.
        string? RemoteAlias = null);

    /// <summary>Resolves (ahead, behind) for <paramref name="t"/> against its counterpart
    /// and returns the matching push/pull/fast-forward menu item(s), or empty when there is no
    /// counterpart, the lookup fails, or the refs are identical. <c>git pull</c> always merges
    /// into whatever is currently checked out, so a Pull/Fast-forward item is only offered when
    /// the branch in question is the current branch; a Push item needs no such guard since
    /// <c>git push</c> takes an explicit local ref regardless of checkout state.</summary>
    internal static async Task<IReadOnlyList<Control>> BuildDivergenceItemsAsync(
        Target t, IGitGateway gateway, IRepoActionBus actions, CancellationToken ct)
    {
        if (t.CounterpartRef is not { } counterpart || t.RemoteAlias is not { } remoteAlias)
            return [];

        var (local, remote) = t.IsLocal ? (t.TrackingRef, counterpart) : (counterpart, t.TrackingRef);

        AheadBehind? ab;
        try { ab = await gateway.GetDivergenceAsync(local, remote, ct); }
        catch { return []; }
        if (ab is not { } div || (div.Ahead == 0 && div.Behind == 0))
            return [];

        var isCurrentLocalBranch = t.IsLocal
            ? string.Equals(t.Name, t.CurrentBranch, StringComparison.Ordinal)
            : string.Equals(counterpart, t.CurrentBranch, StringComparison.Ordinal);

        var prefix = $"{remoteAlias}/";
        var remoteBranch = t.IsLocal
            ? (counterpart.StartsWith(prefix, StringComparison.Ordinal) ? counterpart[prefix.Length..] : counterpart)
            : (t.TrackingRef.StartsWith(prefix, StringComparison.Ordinal) ? t.TrackingRef[prefix.Length..] : t.TrackingRef);

        void Push(string branchName) => actions.RequestNetworkAction(new RepoNetworkRequest(RepoNetworkAction.Push, remoteAlias, branchName, remoteBranch));
        void Pull(string branchName) => actions.RequestNetworkAction(new RepoNetworkRequest(RepoNetworkAction.Pull, remoteAlias, branchName, remoteBranch));

        var items = new List<Control>();
        if (t.IsLocal)
        {
            if (div.Ahead > 0 && div.Behind == 0)
                items.Add(MenuLeaf($"Push {div.Ahead} commit(s) to {remoteAlias}", () => Push(t.Name)));
            else if (div.Behind > 0 && isCurrentLocalBranch)
                items.Add(MenuLeaf(div.Ahead == 0 ? $"Fast-forward from {counterpart}" : $"Pull from {counterpart}", () => Pull(t.Name)));
        }
        else
        {
            if (div.Ahead > 0 && div.Behind == 0)
                items.Add(MenuLeaf($"Push {t.Name} to {remoteAlias}", () => Push(t.Name)));
            else if (div.Behind > 0 && isCurrentLocalBranch)
                items.Add(MenuLeaf($"Pull {t.TrackingRef}", () => Pull(t.Name)));
        }
        return items;
    }

    public static List<Control> Build(
        Target t, RepositoryContext ctx, IDialogService dialogs, INotificationService notify,
        IStashService stash, Action<string> copyToClipboard)
    {
        var items = new List<Control>();

        async Task Run(Func<Task> op, string what)
        {
            try { await op(); await ctx.State.RefreshAllAsync(ctx.Lifetime); }
            catch (Exception ex) { notify.Publish(NotificationSeverity.Error, ex.NotificationMessage(what), ex.NotificationDetails()); }
        }

        // A local branch matching the short name already exists → just check it out. Only when a
        // remote row has no local counterpart do we materialise a tracking branch (and wire its
        // upstream). Mirrors RefNavigatorView.CheckoutAsync so the row menu and double-tap agree.
        var localExists = ctx.State.Branches.Any(x =>
            !x.IsRemote && string.Equals(x.Name, t.Name, StringComparison.Ordinal));
        var isCurrent = t.IsLocal && string.Equals(t.Name, t.CurrentBranch, StringComparison.Ordinal);

        items.Add(MenuLeaf("Checkout", () => RunWithStashRecovery(ctx, notify, stash, async () =>
        {
            if (!t.IsLocal && !localExists)
            {
                await ctx.Gateway.CreateBranchAsync(t.Name, t.TrackingRef, checkout: true, ctx.Lifetime);
                await ctx.Gateway.SetUpstreamAsync(t.Name, t.TrackingRef, ctx.Lifetime);
            }
            else
            {
                await ctx.Gateway.CheckoutAsync(t.Name, new GitCheckoutOptions(), ctx.Lifetime);
            }
        }, "Checkout")));

        if (t.TipSha is { Length: > 0 } sha)
        {
            var short7 = sha.Length >= 7 ? sha[..7] : sha;
            items.Add(MenuLeaf("Create branch here…", () => PromptCreateBranch(ctx, dialogs, notify, stash, sha, short7)));
            items.Add(MenuLeaf("Create tag here…", () => PromptCreateTag(ctx, dialogs, notify, sha, short7)));
        }

        if (t.IsLocal)
        {
            items.Add(MenuLeaf("Rename…", async () =>
            {
                var n = await dialogs.PromptAsync("Rename branch", $"New name for '{t.Name}':", t.Name);
                if (string.IsNullOrWhiteSpace(n) || n == t.Name) return;
                await Run(() => ctx.Gateway.RenameBranchAsync(t.Name, n.Trim(), ctx.Lifetime), "Rename");
            }));
            // Git refuses to delete the checked-out branch — omit the item rather than offer a
            // guaranteed failure.
            if (!isCurrent)
                items.Add(MenuLeaf("Delete…", () => DeleteAsync(ctx, dialogs, notify, t.Name)));
        }

        // Merging a branch into itself is a no-op at best — only offer it for a non-current target.
        if (!isCurrent)
        {
            var mergeRef = t.IsLocal ? t.Name : t.CopyName;
            var into = t.CurrentBranch ?? "current";
            items.Add(MenuLeaf($"Merge into {into} branch", async () =>
            {
                if (!await dialogs.ConfirmAsync("Merge", $"Merge {mergeRef} into {t.CurrentBranch ?? "the current branch"}?")) return;
                await RunWithStashRecovery(ctx, notify, stash, async () =>
                {
                    await ctx.Gateway.MergeAsync(mergeRef, noFastForward: false, ctx.Lifetime);
                    await ctx.State.RefreshAllAsync(ctx.Lifetime);
                    var conflicts = ctx.State.Status.Conflicted.Count;
                    notify.Publish(conflicts > 0 ? NotificationSeverity.Warning : NotificationSeverity.Success,
                        conflicts > 0 ? $"Merge paused on {conflicts} conflict(s) — resolve, then commit." : $"Merged {mergeRef}.");
                }, "Merge");
            }));
        }

        items.Add(MenuLeaf("Copy branch name", () => { copyToClipboard(t.CopyName); return Task.CompletedTask; }));

        return items;
    }

    /// <summary>Deferred so the prompt parents cleanly after the menu tears down.</summary>
    public static Task PromptCreateBranch(RepositoryContext ctx, IDialogService dialogs, INotificationService notify,
        IStashService stash, string sha, string shortSha)
    {
        Dispatcher.UIThread.Post(async () =>
        {
            var name = await dialogs.PromptAsync("New branch", $"Branch name (from {shortSha}):");
            if (string.IsNullOrWhiteSpace(name)) return;
            // Unlike creating a branch at the current tip, this can check out a DIFFERENT commit
            // than HEAD — the same "commit or stash first" refusal as any other checkout applies.
            await RunWithStashRecovery(ctx, notify, stash,
                () => ctx.Gateway.CreateBranchAsync(name.Trim(), sha, checkout: true, ctx.Lifetime), "Create branch");
        });
        return Task.CompletedTask;
    }

    public static Task PromptCreateTag(RepositoryContext ctx, IDialogService dialogs, INotificationService notify,
        string sha, string shortSha)
    {
        Dispatcher.UIThread.Post(async () =>
        {
            var name = await dialogs.PromptAsync("New tag", $"Tag name (at {shortSha}):");
            if (string.IsNullOrWhiteSpace(name)) return;
            try
            {
                await ctx.Gateway.CreateTagAsync(name.Trim(), sha, null, ctx.Lifetime);
                await ctx.State.RefreshAllAsync(ctx.Lifetime);
            }
            catch (Exception ex) { notify.Publish(NotificationSeverity.Error, ex.NotificationMessage("Create tag"), ex.NotificationDetails()); }
        });
        return Task.CompletedTask;
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
