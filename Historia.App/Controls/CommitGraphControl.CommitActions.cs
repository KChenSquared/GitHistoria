namespace Historia.Controls;

// Tier 3.4 of the debloat audit (§5.1): the commit-row / branch-pill / drop-intent context
// menus and the git operations they invoke (checkout, create branch/tag, reset, cherry-pick,
// revert, merge, delete) split out of the CommitGraphControl god-file. Pure file split —
// same private members on the same partial class, no behaviour change. The Avalonia Pen/Brush
// render pass, hit-testing and layout stay in CommitGraphControl.cs.
public sealed partial class CommitGraphControl
{
    private void CheckoutRefEntry(DecorationType type, string label, bool isLocal, string[]? remotes)
    {
        if (type is DecorationType.LocalBranch or DecorationType.RemoteBranch)
        {
            CheckoutFromBadge(new GraphDecoration
            {
                RowIndex = 0, Type = type, Label = label, ColorIndex = 0,
                IsLocal = isLocal, RemotesOrNull = remotes,
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
        await BranchMenu.RunWithStashRecovery(ctx, _notifications, _stashes,
            () => ctx.Gateway.CheckoutAsync(refish, new GitCheckoutOptions(), ctx.Lifetime), "Checkout");
    }

    // ── Context menus (§7.6) ──

    private void ShowCommitContextMenu(ObjectId id)
    {
        if (_context is not { } ctx)
            return;
        var sha = id.Sha;
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

    /// <summary>v8 M3: prompt for a new branch at <paramref name="sha"/> and create it.
    /// Shared by the commit-row menu and the branch/tag pill menu.</summary>
    private Task PromptCreateBranch(RepositoryContext ctx, string sha, string shortSha)
        => BranchMenu.PromptCreateBranch(ctx, _dialogs, _notifications, _stashes, sha, shortSha);

    /// <summary>v8 M3: prompt for a new lightweight tag at <paramref name="sha"/>.</summary>
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
            _ = BranchMenu.RunWithStashRecovery(ctx, _notifications, _stashes,
                () => ctx.Gateway.CheckoutAsync(name, new GitCheckoutOptions(), ctx.Lifetime), "Checkout");
            return;
        }

        var remotes = deco.RemotesOrNull ?? [];
        var tracking = remotes.Length > 0 ? $"{remotes[0]}/{name}" : name;
        _ = BranchMenu.RunWithStashRecovery(ctx, _notifications, _stashes,
            () => ctx.Gateway.CreateBranchAsync(name, tracking, checkout: true, ctx.Lifetime), "Checkout");
    }

    private void ShowPillContextMenu(GraphDecoration deco)
    {
        if (_context is not { } ctx)
            return;
        var name = deco.Label;
        var remotes = deco.RemotesOrNull ?? [];
        var items = new List<Control>();

        // v8 M3: the pill's own commit — for "Create branch/tag here" on a branch or tag label.
        ObjectId? pillId = _layout is { } lay && deco.RowIndex >= 0 && deco.RowIndex < lay.Layout.Commits.Count
            ? lay.Layout.Commits[deco.RowIndex].Id
            : null;

        if (deco.Type is DecorationType.LocalBranch or DecorationType.RemoteBranch)
        {
            if (deco.IsLocal)
                items.Add(MenuLeaf($"Checkout {TruncateBranchLabelForMenu(name)}", () => BranchMenu.RunWithStashRecovery(ctx, _notifications, _stashes,
                    () => ctx.Gateway.CheckoutAsync(name, new GitCheckoutOptions(), ctx.Lifetime), "Checkout")));

            if (remotes.Length > 0)
            {
                if (deco.IsLocal)
                    items.Add(new Separator());
                foreach (var rem in remotes)
                {
                    var tracking = $"{rem}/{name}";
                    items.Add(MenuLeaf($"Checkout {TruncateBranchLabelForMenu(tracking)} as a new branch", () => BranchMenu.RunWithStashRecovery(ctx, _notifications, _stashes,
                        () => ctx.Gateway.CreateBranchAsync(name, tracking, checkout: true, ctx.Lifetime), "Checkout")));
                }
            }

            var primaryRef = deco.IsLocal ? name : (remotes.Length > 0 ? $"{remotes[0]}/{name}" : name);
            items.Add(new Separator());
            items.Add(MenuLeaf($"Merge {TruncateBranchLabelForMenu(primaryRef)} into current", () => MergeAsync(ctx, primaryRef, primaryRef)));
            items.Add(MenuLeaf($"Rebase current branch onto {TruncateBranchLabelForMenu(primaryRef)}", () => _nav.RequestNavigate($"rebase?onto={Uri.EscapeDataString(primaryRef)}")));

            if (deco.IsLocal)
            {
                items.Add(new Separator());
                items.Add(MenuLeaf("Rename…", async () =>
                {
                    var newName = await _dialogs.PromptAsync("Rename branch", $"New name for '{name}':", name);
                    if (string.IsNullOrWhiteSpace(newName) || newName == name) return;
                    await Guard(() => ctx.Gateway.RenameBranchAsync(name, newName.Trim(), ctx.Lifetime), ctx, "Rename");
                }));
                items.Add(MenuLeaf("Delete", () => BranchMenu.DeleteAsync(ctx, _dialogs, _notifications, name)));
            }
        }
        else if (deco.Type == DecorationType.Tag)
        {
            items.Add(MenuLeaf($"Delete tag {TruncateBranchLabelForMenu(name)}", async () =>
            {
                if (!await _dialogs.ConfirmAsync("Delete tag", $"Delete local tag '{name}'?", destructive: true, "Delete")) return;
                await Guard(() => ctx.Gateway.DeleteTagAsync(name, ctx.Lifetime), ctx, "Delete tag");
            }));
        }

        // v8 M3: "Create branch/tag here" act on the pill's own commit — present on the branch
        // label and tag menus, same as on the graph row menu.
        if (pillId is { } bid &&
            deco.Type is DecorationType.LocalBranch or DecorationType.RemoteBranch or DecorationType.Tag)
        {
            var shortSha = bid.Sha[..7];
            if (items.Count > 0) items.Add(new Separator());
            items.Add(MenuLeaf("Create branch here…", () => PromptCreateBranch(ctx, bid.Sha, shortSha)));
            items.Add(MenuLeaf("Create tag here…", () => PromptCreateTag(ctx, bid.Sha, shortSha)));
        }

        // v8 M4: "Copy branch name" — the one common-denominator item the pill menu still lacked.
        if (deco.Type is DecorationType.LocalBranch or DecorationType.RemoteBranch)
        {
            var copyName = deco.IsLocal ? name : (remotes.Length > 0 ? $"{remotes[0]}/{name}" : name);
            items.Add(new Separator());
            items.Add(MenuLeaf("Copy branch name", () => CopyText(copyName)));
        }

        if (items.Count > 0)
        {
            var menu = new ContextMenu { ItemsSource = items };

            // v8.4: long branch/tag names get ellipsis-trimmed in the menu items above (so the
            // popup never overflows the screen) — show the untrimmed name as a tooltip anchored
            // above the menu itself so it's still readable. Opened/Closed keep the tooltip's
            // lifetime tied to the menu's.
            if (deco.Type is DecorationType.LocalBranch or DecorationType.RemoteBranch or DecorationType.Tag)
            {
                var fullLabel = deco.Type == DecorationType.Tag ? name
                    : deco.IsLocal ? name : (remotes.Length > 0 ? $"{remotes[0]}/{name}" : name);
                if (fullLabel.Length > MenuNameMaxChars)
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

    /// <summary>Max characters of a branch/tag name shown in a context-menu item before it's
    /// ellipsis-trimmed (§ pill-menu fix) — long names otherwise force the right-click popup
    /// wider than the screen.</summary>
    private const int MenuNameMaxChars = 25;

    /// <summary>Ellipsis-trims a branch/tag name to <see cref="MenuNameMaxChars"/> for display in
    /// a menu item — e.g. "Checkout origin/feature/very-long-name as a new branch" — without
    /// affecting the untrimmed name callers still use for the actual git operation.</summary>
    private static string TruncateBranchLabelForMenu(string name) =>
        name.Length <= MenuNameMaxChars ? name : name[..(MenuNameMaxChars - 1)] + "…";

    private void ShowDropIntentMenu(ObjectId commitId, string shortSha, string branchLabel)
    {
        if (_context is not { } ctx)
            return;
        var items = new List<Control>
        {
            new MenuItem { Header = $"Drop {shortSha} → {branchLabel}", IsEnabled = false },
            new Separator(),
            MenuLeaf($"Merge {shortSha} into current branch", () => MergeAsync(ctx, commitId.Sha, shortSha)),
            MenuLeaf("Reset current branch to this commit (mixed)", () => ResetAsync(ctx, commitId.Sha, GitResetMode.Mixed, shortSha)),
            MenuLeaf($"Cherry-pick {shortSha} onto current branch", () => CherryPickAsync([commitId])),
            MenuLeaf($"Rebase current branch onto {branchLabel}", () => _nav.RequestNavigate($"rebase?onto={Uri.EscapeDataString(branchLabel)}")),
        };
        OpenMenu(new ContextMenu { ItemsSource = items });
    }

    private async Task ResetAsync(RepositoryContext ctx, string sha, GitResetMode mode, string shortSha)
    {
        var destructive = mode == GitResetMode.Hard;
        if (!await _dialogs.ConfirmAsync("Reset branch",
                $"Move {_state?.CurrentBranch?.Name ?? "HEAD"} to {shortSha} ({mode})?"
                + (destructive ? " Uncommitted changes will be lost." : ""),
                destructive, "Reset"))
            return;
        await Guard(() => ctx.Gateway.ResetAsync(sha, mode, ctx.Lifetime), ctx, "Reset");
    }

    private async Task CherryPickAsync(IReadOnlyList<ObjectId> ids)
    {
        if (_context is not { } ctx)
            return;
        var isMerge = ids.Count == 1 && await ctx.WithRepositoryAsync(r => r.Lookup<Commit>(ids[0])?.Parents.Count() > 1, ctx.Lifetime);
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

    private async Task RevertAsync(IReadOnlyList<ObjectId> ids)
    {
        if (_context is not { } ctx)
            return;
        var isMerge = ids.Count == 1 && await ctx.WithRepositoryAsync(r => r.Lookup<Commit>(ids[0])?.Parents.Count() > 1, ctx.Lifetime);
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
        await BranchMenu.RunWithStashRecovery(ctx, _notifications, _stashes, async () =>
        {
            await ctx.Gateway.MergeAsync(refish, noFastForward: false, ctx.Lifetime);
            await ctx.State.RefreshAllAsync(ctx.Lifetime);
            var conflicts = ctx.State.Status.Conflicted.Count;
            _notifications.Publish(conflicts > 0 ? NotificationSeverity.Warning : NotificationSeverity.Success,
                conflicts > 0 ? "Merge encountered conflicts, resolve before continue" : $"Merged {display}.");
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
            _notifications.Publish(NotificationSeverity.Error, $"{what} failed: {ex.Message}", ex.NotificationDetails());
        }
    }

    private void CopyText(string s)
        => TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(s);

    /// <summary>v8 M4: copy the full commit message (subject + body). Uses the M16 body cache
    /// when warm, else fetches the detail once.</summary>
    private void CopyCommitMessage(ObjectId id)
    {
        // _msgBodyCache (M16) holds the body only; combine with the subject.
        if (TryGetCachedBody(id, out var body))
        {
            var subject = FindSubject(id);
            CopyText(body.Length > 0 ? subject + "\n\n" + body : subject);
            return;
        }
        if (_context is not { } ctx)
            return;
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                var d = await ctx.Gateway.GetCommitDetailAsync(id, ctx.Lifetime);
                CacheMessageBody(id, d.MessageBody ?? "");
                CopyText(string.IsNullOrEmpty(d.MessageBody)
                    ? d.MessageShort
                    : d.MessageShort + "\n\n" + d.MessageBody);
            }
            catch (Exception ex)
            {
                _notifications.Publish(NotificationSeverity.Error, $"Copy message failed: {ex.Message}", ex.NotificationDetails());
            }
        });
    }

    private string FindSubject(ObjectId id)
    {
        if (_layout is { } lay && lay.Layout.RowByCommitId.TryGetValue(id, out var row)
            && row >= 0 && row < lay.Layout.Commits.Count)
        {
            return lay.Layout.Commits[row].MessageShort;
        }
        return id.Sha[..7];
    }
}
