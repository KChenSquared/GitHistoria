namespace Historia.Controls;

// The commit-row / branch-pill / drop-intent context
// menus and the git operations they invoke (checkout, create branch/tag, reset, cherry-pick,
// revert, merge, delete), as part of the CommitGraphControl partial class. The Avalonia
// Pen/Brush render pass, hit-testing and layout live in CommitGraphControl.cs.
public sealed partial class CommitGraphControl
{
    private void CheckoutRefEntry(DecorationType type, string label, bool isLocal, string[]? remotes)
    {
        if (type is DecorationType.LocalBranch or DecorationType.RemoteBranch)
        {
            CheckoutFromBadge(new GraphDecoration
            {
                RowIndex = 0,
                Type = type,
                Label = label,
                ColorIndex = 0,
                IsLocal = isLocal,
                RemotesOrNull = remotes,
            });
            return;
        }
        if (type == DecorationType.Tag && _context is { } ctx)
            _ = RunConfirmedDetachedCheckout(ctx, label, $"Check out tag '{label}' in a detached HEAD?");
    }

    private async Task RunConfirmedDetachedCheckout(RepositoryContext ctx, string refish, string prompt)
    {
        if (!await _dialogs.ConfirmAsync("Detached checkout", prompt))
            return;
        await BranchActions.RunWithStashRecovery(ctx, _notifications, _stashes,
            () => ctx.Gateway.CheckoutAsync(refish, new GitCheckoutOptions(), ctx.Lifetime), "Checkout");
    }

    // ── Context menus ──

    private void ShowCommitContextMenu(GitObjectId id)
    {
        if (_context is not { } ctx)
            return;
        var sha = id.ToString();
        var shortSha = sha[..7];
        var items = new List<Control>
        {
            MenuLeaf("Copy SHA", () => CopyText(sha)),
            MenuLeaf("Copy short SHA", () => CopyText(shortSha)),
            MenuLeaf("Copy commit message", () => CopyCommitMessage(id)),
            new Separator(),
            MenuLeaf("Create branch here…", () => PromptCreateBranch(ctx, sha, shortSha)),
            MenuLeaf("Create tag here…", () => PromptCreateTag(ctx, sha, shortSha)),
            MenuLeaf("Checkout this commit (detached)", () =>
                RunConfirmedDetachedCheckout(ctx, sha, $"Check out {shortSha} in a detached HEAD?")),
            new Separator(),
        };

        var resetItem = new MenuItem { Header = "Reset current branch to here" };
        resetItem.ItemsSource = new Control[]
        {
            MenuLeaf("Soft (keep index and working tree)", () => ResetAsync(ctx, sha, GitResetMode.Soft, shortSha)),
            MenuLeaf("Mixed (keep working tree)", () => ResetAsync(ctx, sha, GitResetMode.Mixed, shortSha)),
            MenuLeaf("Hard (discard changes)", () => ResetAsync(ctx, sha, GitResetMode.Hard, shortSha)),
        };
        items.Add(resetItem);

        items.Add(MenuLeaf("Merge into current branch", () => MergeAsync(ctx, sha, shortSha)));
        items.Add(new Separator());
        items.Add(MenuLeaf("Cherry-pick onto current branch", () => CherryPickAsync([id])));
        items.Add(MenuLeaf("Revert this commit", () => RevertAsync([id])));
        if (_multiSelect.Count > 1)
        {
            var ordered = _multiSelect.OrderByDescending(r => r)
                .Select(r => _layout!.Layout.Commits[r].Id).ToList();
            items.Add(MenuLeaf($"Cherry-pick {ordered.Count} selected (oldest first)", () => CherryPickAsync(ordered)));
            items.Add(MenuLeaf($"Revert {ordered.Count} selected", () => RevertAsync(ordered)));
        }

        OpenMenu(new ContextMenu { ItemsSource = items });
    }

    /// <summary>Prompt for a new branch at <paramref name="sha"/> and create it.
    /// Shared by the commit-row menu and the branch/tag pill menu.</summary>
    private Task PromptCreateBranch(RepositoryContext ctx, string sha, string shortSha)
        => BranchMenu.PromptCreateBranch(ctx, _dialogs, _notifications, _stashes, sha, shortSha);

    /// <summary>Prompt for a new lightweight tag at <paramref name="sha"/>.</summary>
    private Task PromptCreateTag(RepositoryContext ctx, string sha, string shortSha)
        => BranchMenu.PromptCreateTag(ctx, _dialogs, _notifications, sha, shortSha);

    private void CheckoutFromBadge(GraphDecoration deco)
    {
        if (_context is not { } ctx)
            return;
        if (deco.Type is not (DecorationType.LocalBranch or DecorationType.RemoteBranch))
            return;

        var name = deco.Label;
        var hasLocal = deco.IsLocal
            || (_state?.Branches.Any(b => !b.IsRemote && string.Equals(b.Name, name, StringComparison.Ordinal)) ?? false);

        if (hasLocal)
        {
            _ = CheckoutLocalThenMaybeSyncAsync(ctx, deco, name);
            return;
        }

        var remotes = deco.RemotesOrNull ?? [];
        var tracking = remotes.Length > 0 ? $"{remotes[0]}/{name}" : name;
        _ = BranchActions.RunWithStashRecovery(ctx, _notifications, _stashes,
            () => ctx.Gateway.CreateBranchAsync(name, tracking, checkout: true, ctx.Lifetime), "Checkout");
    }

    // Resolved on use: only a badge pull needs it, and it shares the repository's network gate
    // with Fetch / Pull / Push and auto-fetch.
    private Historia.Services.UI.IRepoNetworkActionRunner NetworkRunner =>
        _services.GetRequiredService<Historia.Services.UI.IRepoNetworkActionRunner>();

    /// <summary>Double-clicking a remote badge whose local counterpart exists switches to
    /// the local branch, then — only when the badge that was actually clicked was the remote one
    /// (a local-badge double-click stays a plain, silent checkout) — measures divergence against
    /// that remote and either fast-forwards silently (a guaranteed no-loss fast-forward) or asks
    /// before pulling a merge (the local branch also has commits the remote lacks).</summary>
    internal async Task CheckoutLocalThenMaybeSyncAsync(RepositoryContext ctx, GraphDecoration deco, string name)
    {
        await BranchActions.RunWithStashRecovery(ctx, _notifications, _stashes,
            () => ctx.Gateway.CheckoutAsync(name, new GitCheckoutOptions(), ctx.Lifetime), "Checkout");

        // The recovery helper reports failures itself and returns normally, so HEAD is the only reliable signal.
        if (!string.Equals(ctx.State.CurrentBranch?.Name, name, StringComparison.Ordinal))
            return;

        if (deco.IsLocal)
            return;
        var remotes = deco.RemotesOrNull ?? [];
        if (remotes.Length == 0)
            return;
        var remote = remotes[0];
        var upstream = $"{remote}/{name}";

        AheadBehind? ab;
        try { ab = await ctx.Gateway.GetDivergenceAsync(name, upstream, ctx.Lifetime); }
        catch { return; }
        if (ab is not { Behind: > 0 } div)
            return;

        if (div.Ahead == 0)
        {
            // ahead == 0 && behind > 0: a guaranteed fast-forward — nothing local to lose.
            await BranchActions.RunWithStashRecovery(ctx, _notifications, _stashes,
                () => NetworkRunner.RunExclusiveAsync(ctx, "Pull",
                    (p, ct) => ctx.Gateway.PullAsync(remote, name, GitPullOptions.From(_settings.Current), p, ct)), "Pull");
            return;
        }

        if (!await _dialogs.ConfirmAsync("Pull and merge?",
                $"{upstream} has {div.Behind} commit(s) you don't have, and you have {div.Ahead} it doesn't. Pull and merge?"))
            return;
        await BranchActions.RunWithStashRecovery(ctx, _notifications, _stashes,
            () => NetworkRunner.RunExclusiveAsync(ctx, "Pull",
                (p, ct) => ctx.Gateway.PullAsync(remote, name, GitPullOptions.From(_settings.Current), p, ct)), "Pull");
    }

    private void ShowPillContextMenu(GraphDecoration deco)
    {
        if (_context is not { } ctx)
            return;
        var name = deco.Label;
        var remotes = deco.RemotesOrNull ?? [];
        var items = new List<Control>();

        // The pill's own commit — for "Create branch/tag here" on a branch or tag label.
        GitObjectId? pillId = _layout is { } lay && deco.RowIndex >= 0 && deco.RowIndex < lay.Layout.Commits.Count
            ? lay.Layout.Commits[deco.RowIndex].Id
            : null;

        if (deco.Type is DecorationType.LocalBranch or DecorationType.RemoteBranch)
        {
            if (deco.IsLocal)
                items.Add(MenuLeaf($"Checkout {BranchMenu.TruncateBranchLabelForMenu(name)}", () => BranchActions.RunWithStashRecovery(ctx, _notifications, _stashes,
                    () => ctx.Gateway.CheckoutAsync(name, new GitCheckoutOptions(), ctx.Lifetime), "Checkout")));

            if (remotes.Length > 0)
            {
                if (deco.IsLocal)
                    items.Add(new Separator());
                foreach (var rem in remotes)
                {
                    var tracking = $"{rem}/{name}";
                    items.Add(MenuLeaf($"Checkout {BranchMenu.TruncateBranchLabelForMenu(tracking)} as a new branch", () => BranchActions.RunWithStashRecovery(ctx, _notifications, _stashes,
                        () => ctx.Gateway.CreateBranchAsync(name, tracking, checkout: true, ctx.Lifetime), "Checkout")));
                }
            }

            var primaryRef = deco.IsLocal ? name : (remotes.Length > 0 ? $"{remotes[0]}/{name}" : name);
            items.Add(new Separator());
            if (deco.IsLocal)
                items.Add(MenuLeaf($"Merge {BranchMenu.TruncateBranchLabelForMenu(name)} into current", () => MergeAsync(ctx, name, name)));
            items.Add(MenuLeaf($"Rebase current branch onto {BranchMenu.TruncateBranchLabelForMenu(primaryRef)}", () => _nav.RequestNavigate($"rebase?onto={Uri.EscapeDataString(primaryRef)}")));

            if (deco.IsLocal)
            {
                items.Add(new Separator());
                items.Add(MenuLeaf("Rename…", async () =>
                {
                    var newName = await _dialogs.PromptAsync("Rename branch", $"New name for '{name}':", name);
                    if (string.IsNullOrWhiteSpace(newName) || newName == name) return;
                    await Guard(() => ctx.Gateway.RenameBranchAsync(name, newName.Trim(), ctx.Lifetime), ctx, "Rename");
                }));
                items.Add(MenuLeaf("Delete local…", () => BranchActions.DeleteAsync(ctx, _dialogs, _notifications, name)));
            }
        }
        else if (deco.Type == DecorationType.Tag)
        {
            items.Add(MenuLeaf($"Delete tag {BranchMenu.TruncateBranchLabelForMenu(name)}", async () =>
            {
                if (!await _dialogs.ConfirmDestructiveAsync("Delete tag", $"Delete local tag '{name}'? This cannot be undone.", "Delete")) return;
                await Guard(() => ctx.Gateway.DeleteTagAsync(name, ctx.Lifetime), ctx, "Delete tag");
            }));
        }

        // "Create branch/tag here" act on the pill's own commit — present on the branch
        // label and tag menus, same as on the graph row menu.
        if (pillId is { } bid &&
            deco.Type is DecorationType.LocalBranch or DecorationType.RemoteBranch or DecorationType.Tag)
        {
            var shortSha = bid.ToShortString(7);
            if (items.Count > 0) items.Add(new Separator());
            items.Add(MenuLeaf("Create branch here…", () => PromptCreateBranch(ctx, bid.ToString(), shortSha)));
            items.Add(MenuLeaf("Create tag here…", () => PromptCreateTag(ctx, bid.ToString(), shortSha)));
        }

        // "Copy branch name", as on the sidebar branch menu.
        if (deco.Type is DecorationType.LocalBranch or DecorationType.RemoteBranch)
        {
            var copyName = deco.IsLocal ? name : (remotes.Length > 0 ? $"{remotes[0]}/{name}" : name);
            items.Add(new Separator());
            items.Add(MenuLeaf("Copy branch name", () => CopyText(copyName)));
        }

        var remoteSection = deco.Type is DecorationType.LocalBranch or DecorationType.RemoteBranch
            ? BranchMenu.BuildRemoteBranchSubmenus(
                name,
                remotes,
                _state?.CurrentBranch?.Name,
                r => MergeAsync(ctx, r, r),
                rem =>
                {
                    _actionBus.RequestNetworkAction(new RepoNetworkRequest(
                        RepoNetworkAction.DeleteRemoteBranch, rem, null, name, ctx));
                    return Task.CompletedTask;
                })
            : (IReadOnlyList<MenuItem>)[];

        var remoteControls = remoteSection.Count > 0
            ? (items.Count > 0 ? remoteSection.Cast<Control>().Concat([new Separator()]).ToList() : remoteSection.Cast<Control>().ToList())
            : [];

        var allItems = remoteControls.Concat(items).ToList();

        if (allItems.Count > 0)
        {
            var menu = new ContextMenu { ItemsSource = allItems };

            // Mirror the sidebar's divergence-aware push/pull/fast-forward items, resolved
            // lazily on Opening (git process) rather than at menu-build time.
            if (deco.Type is DecorationType.LocalBranch or DecorationType.RemoteBranch)
            {
                string? counterpartRef = null, remoteAlias = null;
                if (deco.IsLocal)
                {
                    var local = _state?.Branches.FirstOrDefault(b => !b.IsRemote && b.Name == name);
                    if (local?.UpstreamName is { } upstream)
                    {
                        counterpartRef = upstream;
                        remoteAlias = _state?.Branches.FirstOrDefault(b => b.IsRemote && b.Name == upstream)?.RemoteName;
                    }
                }
                else if (remotes.Length > 0)
                {
                    var hasLocalCounterpart = _state?.Branches.Any(b => !b.IsRemote && b.Name == name) ?? false;
                    if (hasLocalCounterpart)
                        counterpartRef = name;
                    remoteAlias = remotes[0];
                }

                if (counterpartRef is not null && remoteAlias is not null)
                {
                    var divergenceTarget = new BranchMenu.Target(
                        Name: name, CopyName: name, TrackingRef: deco.IsLocal ? name : $"{remoteAlias}/{name}",
                        IsLocal: deco.IsLocal, TipSha: null, CurrentBranch: _state?.CurrentBranch?.Name,
                        CounterpartRef: counterpartRef, RemoteAlias: remoteAlias);
                    var baseItems = items;
                    menu.Opening += async (_, _) =>
                    {
                        var opened = menu;
                        IReadOnlyList<Control> divergenceItems;
                        try { divergenceItems = await BranchMenu.BuildDivergenceItemsAsync(divergenceTarget, ctx.Gateway, _actionBus, ctx.Lifetime); }
                        catch { return; }
                        if (divergenceItems.Count == 0 || !ReferenceEquals(menu, opened) || !menu.IsOpen)
                            return;
                        menu.ItemsSource = remoteControls
                            .Concat(divergenceItems)
                            .Concat([new Separator()])
                            .Concat(baseItems)
                            .ToList();
                    };
                }
            }

            // Long branch/tag names get ellipsis-trimmed in the menu items above (so the
            // popup never overflows the screen) — show the untrimmed name as a tooltip anchored
            // above the menu itself so it's still readable. Opened/Closed keep the tooltip's
            // lifetime tied to the menu's.
            if (deco.Type is DecorationType.LocalBranch or DecorationType.RemoteBranch or DecorationType.Tag)
            {
                var fullLabel = deco.Type == DecorationType.Tag ? name
                    : deco.IsLocal ? name : (remotes.Length > 0 ? $"{remotes[0]}/{name}" : name);
                if (fullLabel.Length > BranchMenu.MenuNameMaxChars)
                {
                    ToolTip.SetTip(menu, fullLabel);
                    ToolTip.SetPlacement(menu, PlacementMode.TopEdgeAlignedLeft);
                    ToolTip.SetVerticalOffset(menu, -4);
                    ToolTip.SetHorizontalOffset(menu, 0);
                    // Deferred a frame: the menu reports a zero-size Bounds at Opened time (before
                    // its first layout pass), which placed the tooltip on top of the menu instead
                    // of above it.
                    menu.Opened += (_, _) => Dispatcher.UIThread.Post(() => ToolTip.SetIsOpen(menu, true), DispatcherPriority.Loaded);
                    menu.Closed += (_, _) => ToolTip.SetIsOpen(menu, false);
                }
            }

            OpenMenu(menu);
        }
    }

    private void ShowDropIntentMenu(GitObjectId commitId, string shortSha, string branchLabel)
    {
        if (_context is not { } ctx)
            return;
        var items = new List<Control>
        {
            new MenuItem { Header = $"Drop {shortSha} → {branchLabel}", IsEnabled = false },
            new Separator(),
            MenuLeaf($"Merge {shortSha} into current branch", () => MergeAsync(ctx, commitId.ToString(), shortSha)),
            MenuLeaf("Reset current branch to this commit (mixed)", () => ResetAsync(ctx, commitId.ToString(), GitResetMode.Mixed, shortSha)),
            MenuLeaf($"Cherry-pick {shortSha} onto current branch", () => CherryPickAsync([commitId])),
            MenuLeaf($"Rebase current branch onto {branchLabel}", () => _nav.RequestNavigate($"rebase?onto={Uri.EscapeDataString(branchLabel)}")),
        };
        OpenMenu(new ContextMenu { ItemsSource = items });
    }

    private async Task ResetAsync(RepositoryContext ctx, string sha, GitResetMode mode, string shortSha)
    {
        var target = _state?.CurrentBranch?.Name ?? "HEAD";
        if (mode == GitResetMode.Hard)
        {
            if (!await _dialogs.ConfirmDestructiveAsync("Reset branch",
                    $"Move {target} to {shortSha} (hard)? Uncommitted changes will be lost.", "Reset"))
                return;
        }
        else
        {
            // soft/mixed only rewrites the branch pointer, but when the target
            // isn't an ancestor of HEAD the commits between them leave the branch — reflog-
            // recoverable, but no longer reachable from any branch. Count with `rev-list --count`
            // (not libgit2's CalculateHistoryDivergence, which takes ~22 s on far-apart
            // commits) and only escalate to the destructive confirm when commits actually leave. If
            // the count itself fails, fall back to the ordinary confirm rather than block reset.
            int leaving;
            try { leaving = await ctx.Gateway.CountCommitsLeavingAsync(sha, ctx.Lifetime); }
            catch { leaving = 0; }
            if (leaving > 0)
            {
                if (!await _dialogs.ConfirmDestructiveAsync("Reset branch",
                        $"Move {target} to {shortSha} ({mode})? {leaving} commit(s) will leave " +
                        $"{target}; they stay recoverable only through the reflog.", "Reset"))
                    return;
            }
            else if (!await _dialogs.ConfirmAsync("Reset branch",
                         $"Move {target} to {shortSha} ({mode})?", confirmLabel: "Reset"))
            {
                return;
            }
        }
        await Guard(() => ctx.Gateway.ResetAsync(sha, mode, ctx.Lifetime), ctx, "Reset");
    }

    private async Task CherryPickAsync(IReadOnlyList<GitObjectId> ids)
    {
        if (_context is not { } ctx)
            return;
        var isMerge = ids.Count == 1 && await CommitMergeDetection.IsMergeAsync(_layout, _layoutQuery, ctx.Gateway, ids[0], ctx.Lifetime);
        var mainline = isMerge ? 1 : (int?)null;
        if (!await _dialogs.ConfirmAsync("Cherry-pick",
                $"Cherry-pick {ids.Count} commit(s) onto {_state?.CurrentBranch?.Name ?? "the current branch"}?"
                + (isMerge ? " (merge commit — mainline parent 1)" : "")))
            return;
        try
        {
            await _cherryPick.CherryPickAsync(ctx, ids, noCommit: false, mainline, ctx.Lifetime);
            var c = ctx.State.Status.Conflicted.Count;
            _notifications.Publish(c > 0 ? NotificationSeverity.Warning : NotificationSeverity.Success,
                c > 0 ? $"Cherry-pick paused on {c} conflict(s)." : "Cherry-pick complete.");
        }
        catch (Exception ex)
        {
            _notifications.Publish(NotificationSeverity.Error, ex.Message, ex.NotificationDetails());
        }
    }

    private async Task RevertAsync(IReadOnlyList<GitObjectId> ids)
    {
        if (_context is not { } ctx)
            return;
        var isMerge = ids.Count == 1 && await CommitMergeDetection.IsMergeAsync(_layout, _layoutQuery, ctx.Gateway, ids[0], ctx.Lifetime);
        if (!await _dialogs.ConfirmAsync("Revert", $"Revert {ids.Count} commit(s)?" + (isMerge ? " (merge commit — mainline parent 1)" : "")))
            return;
        try
        {
            await _cherryPick.RevertAsync(ctx, ids, isMerge ? 1 : null, ctx.Lifetime);
            var c = ctx.State.Status.Conflicted.Count;
            _notifications.Publish(c > 0 ? NotificationSeverity.Warning : NotificationSeverity.Success,
                c > 0 ? $"Revert paused on {c} conflict(s)." : "Revert complete.");
        }
        catch (Exception ex)
        {
            _notifications.Publish(NotificationSeverity.Error, ex.Message, ex.NotificationDetails());
        }
    }

    private async Task MergeAsync(RepositoryContext ctx, string refish, string display)
    {
        if (!await _dialogs.ConfirmAsync("Merge", $"Merge {display} into {_state?.CurrentBranch?.Name ?? "the current branch"}?"))
            return;
        await BranchActions.RunWithStashRecovery(ctx, _notifications, _stashes, async () =>
        {
            await ctx.Gateway.MergeAsync(refish, noFastForward: false, ctx.Lifetime);
            await ctx.State.RefreshAllAsync(ctx.Lifetime);
            var conflicts = ctx.State.Status.Conflicted.Count;
            _notifications.Publish(conflicts > 0 ? NotificationSeverity.Warning : NotificationSeverity.Success,
                conflicts > 0 ? $"Merge paused on {conflicts} conflict(s) — resolve, then commit." : $"Merged {display}.");
        }, "Merge");
    }

    private async Task Guard(Func<Task> op, RepositoryContext ctx, string what)
    {
        try
        {
            await op();
            await ctx.State.RefreshAllAsync(ctx.Lifetime);
        }
        catch (Exception ex)
        {
            _notifications.Publish(NotificationSeverity.Error, ex.NotificationMessage(what), ex.NotificationDetails());
        }
    }

    private void CopyText(string s)
        => TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(s);

    /// <summary>Copies the full commit message exactly as committed. Uses the message cache
    /// when warm, else fetches the detail once.</summary>
    private void CopyCommitMessage(GitObjectId id)
    {
        if (TryGetCachedMessage(id, out var message))
        {
            CopyText(message);
            return;
        }
        if (_context is not { } ctx)
            return;
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                var d = await ctx.Gateway.GetCommitDetailAsync(id, ctx.Lifetime);
                CacheMessage(id, d.Message);
                CopyText(d.Message);
            }
            catch (Exception ex)
            {
                _notifications.Publish(NotificationSeverity.Error, ex.NotificationMessage("Copy message"), ex.NotificationDetails());
            }
        });
    }
}
