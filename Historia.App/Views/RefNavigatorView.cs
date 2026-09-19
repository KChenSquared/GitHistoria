namespace Historia.Views;

/// <summary>
/// The sidebar ref navigator (V4 port of Sidebar.razor + RefNavigator.razor +
/// RefTreeNodeView.razor). Local / remote / tag trees + stash / submodule / worktree sections.
/// Single click selects + reveals a tip commit; double click checks out. Folders (remote roots
/// and path segments) are collapsible. Every leaf across every category shares one left indent.
/// </summary>
public sealed class RefNavigatorView : UserControl, IDisposable
{
    private readonly IWorkspaceService _workspace;
    private readonly INavigationBus _nav;
    private readonly IDialogService _dialogs;
    private readonly INotificationService _notifications;
    private readonly IStashService _stashes;
    private readonly ISubmoduleService _submodules;
    private readonly IWorktreeService _worktrees;
    private readonly IRepoStateCache _repoCache;

    private readonly StackPanel _root = new();
    private RepositoryContext? _repo;

    // Indentation: leaves and folder labels share this left edge (past the section-header
    // label); each nested folder level adds IndentStep.
    private const double LeafIndent = 30;
    private const double IndentStep = 14;
    private const double SectionMaxHeight = 240;

    // Section defaults: Local Branches / Remotes start expanded; Tags / Stashes / Submodules /
    // Worktrees start collapsed. Session-only, like _expanded below — the state survives section
    // toggles and repo switches, but is reset to these defaults on every app launch and whenever
    // the repo being viewed is closed from the app-band (see ResetSectionState / Bind). Folders
    // within a section have their own, depth-dependent default — see IsOpen.
    private bool _localOpen = true, _remotesOpen = true, _tagsOpen = false;
    private bool _stashesOpen = false, _submodulesOpen = false, _worktreesOpen = false;

    private void ResetSectionState()
    {
        _localOpen = _remotesOpen = true;
        _tagsOpen = _stashesOpen = _submodulesOpen = _worktreesOpen = false;
        _expanded.Clear();
    }

    // Folder expansion, keyed by RefTreeNode.FullPath (or "remote:<name>") — holds paths
    // TOGGLED AWAY from their depth's default, not "is open" directly, so depth 0 (Local
    // Branches' own top-level folders, and each remote's own root — the level "origin" sits at)
    // can default to expanded while every folder nested inside one of those (depth 1+) still
    // defaults to collapsed. See IsOpen. Session-only: reset to defaults on every app launch,
    // remembered across toggles in between.
    private readonly HashSet<string> _expanded = new(StringComparer.Ordinal);

    /// <summary>Depth-0 folders (a remote's own root, or a top-level local-branch folder) start
    /// expanded; anything nested inside one (depth 1+) starts collapsed. <see cref="_expanded"/>
    /// records deviations from that default, so the actual open/closed state is the default
    /// XORed with "has this path been toggled".</summary>
    private bool IsOpen(string path, int depth) => (depth == 0) ^ _expanded.Contains(path);

    // Cached power-tool lists — null = not loaded yet. Filled once per bind / RefsChanged so a
    // section toggle re-renders synchronously instead of flashing empty during a re-fetch.
    private IReadOnlyList<StashEntry>? _stashList;
    private IReadOnlyList<SubmoduleInfo>? _submoduleList;
    private IReadOnlyList<WorktreeInfo>? _worktreeList;
    private bool _submodulesFetched;
    private bool _worktreesFetched;

    public RefNavigatorView(IServiceProvider sp)
    {
        _workspace = sp.GetRequiredService<IWorkspaceService>();
        _nav = sp.GetRequiredService<INavigationBus>();
        _dialogs = sp.GetRequiredService<IDialogService>();
        _notifications = sp.GetRequiredService<INotificationService>();
        _stashes = sp.GetRequiredService<IStashService>();
        _submodules = sp.GetRequiredService<ISubmoduleService>();
        _worktrees = sp.GetRequiredService<IWorktreeService>();
        _repoCache = sp.GetRequiredService<IRepoStateCache>();

        Content = new ScrollViewer { Content = _root, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        this.Dyn(BackgroundProperty, "ColorChrome");

        _workspace.FocusedRepositoryChanged += OnFocusChanged;
        Bind(_workspace.Focused);

        // §V3-10.4: a click on empty sidebar space returns the detail panel to the repo. Rows
        // that do something set e.Handled, so this only fires for background clicks.
        AddHandler(PointerReleasedEvent, (_, e) =>
        {
            if (!e.Handled && _repo is not null)
                _nav.RequestNavigate("repo");
        });
    }

    private void OnFocusChanged(object? s, FocusedRepositoryChangedEventArgs e)
        => Avalonia.Threading.Dispatcher.UIThread.Post(() => Bind(e.New));

    private void Bind(RepositoryContext? ctx)
    {
        if (_repo is not null)
            _repo.State.RefsChanged -= OnRefsChanged;

        // If the repo we were showing has left the open set, it was closed from the app-band
        // (rather than just switched away from) — start the next repo from the default section
        // layout instead of inheriting the closed repo's expand/collapse state.
        if (_repo is not null && !_workspace.OpenRepositories.Contains(_repo))
            ResetSectionState();

        _repo = ctx;
        _submodulesFetched = false;
        _worktreesFetched = false;
        SeedPowerToolsFromCache();
        if (_repo is not null)
            _repo.State.RefsChanged += OnRefsChanged;
        Rebuild();
        if (_repo is not null)
            _ = LoadPowerToolsAsync(_repo);
    }

    private void OnRefsChanged(object? s, RefsChangedEventArgs e)
        => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _submodulesFetched = false;
            _worktreesFetched = false;
            SeedPowerToolsFromCache();
            Rebuild();
            if (_repo is not null)
                _ = LoadPowerToolsAsync(_repo);
        });

    /// <summary>Show last-known stash / submodule / worktree lists from the on-disk cache while
    /// the real enumeration runs; null (→ "loading") when nothing is cached.</summary>
    private void SeedPowerToolsFromCache()
    {
        var snap = _repo is not null ? _repoCache.TryLoad(_repo.RepositoryPath) : null;
        _stashList = snap?.Stashes;
        _submoduleList = snap?.Submodules;
        _worktreeList = snap?.Worktrees;
    }

    private async Task LoadPowerToolsAsync(RepositoryContext repo)
    {
        var stashTask = Safe(() => _stashes.ListAsync(repo, repo.Lifetime));
        var subsTask = _submodulesOpen ? Safe(() => _submodules.ListAsync(repo, repo.Lifetime)) : Task.FromResult<IReadOnlyList<SubmoduleInfo>?>(_submoduleList);
        var wtsTask = _worktreesOpen ? Safe(() => _worktrees.ListAsync(repo, repo.Lifetime)) : Task.FromResult<IReadOnlyList<WorktreeInfo>?>(_worktreeList);

        var stash = await stashTask;
        var subs = await subsTask;
        var wts = await wtsTask;
        if (!ReferenceEquals(repo, _repo))
            return;
        _stashList = stash ?? _stashList ?? [];
        if (_submodulesOpen)
        {
            _submoduleList = subs ?? _submoduleList ?? [];
            _submodulesFetched = true;
        }
        if (_worktreesOpen)
        {
            _worktreeList = wts ?? _worktreeList ?? [];
            _worktreesFetched = true;
        }
        _repoCache.SavePowerTools(repo.RepositoryPath, _stashList, _submoduleList, _worktreeList);
        Rebuild();

        static async Task<T?> Safe<T>(Func<Task<T>> op)
        {
            try { return await op(); }
            catch { return default; }
        }
    }

    // ── Build ──

    private void Rebuild()
    {
        _root.Children.Clear();
        if (_repo is null)
            return;

        var repo = _repo;
        var branches = repo.State.Branches;
        var local = branches.Where(b => !b.IsRemote).OrderByDescending(b => b.IsCurrent).ThenBy(b => b.Name).ToList();
        var remote = branches.Where(b => b.IsRemote).OrderBy(b => b.Name).ToList();
        var tags = repo.State.Tags.OrderByDescending(t => t.Name).ToList();
        var currentCanonical = repo.State.CurrentBranch?.CanonicalName;

        // ── Local branches ──
        var localBody = Section($"Local Branches ({local.Count})", _localOpen,
            () => { _localOpen = !_localOpen; Rebuild(); }, add: () => _ = CreateBranchAsync());
        if (localBody is not null)
            foreach (var node in RefTree.Build(local, b => b.Name))
                AddNode(localBody, node, depth: 0, isRemote: false, currentCanonical);

        // ── Remotes: one collapsible folder per remote ──
        var remoteBody = Section($"Remotes ({repo.State.Remotes.Count})", _remotesOpen,
            () => { _remotesOpen = !_remotesOpen; Rebuild(); });
        if (remoteBody is not null)
        {
            var byRemote = remote
                .GroupBy(b => b.RemoteName ?? (b.Name.Contains('/') ? b.Name[..b.Name.IndexOf('/')] : "origin"))
                .OrderBy(g => g.Key, StringComparer.Ordinal);
            foreach (var g in byRemote)
            {
                var key = "remote:" + g.Key;
                remoteBody.Children.Add(FolderRow(g.Key, key, depth: 0));
                if (IsOpen(key, depth: 0))
                {
                    var prefix = g.Key + "/";
                    foreach (var node in RefTree.Build(g, b => b.Name.StartsWith(prefix, StringComparison.Ordinal) ? b.Name[prefix.Length..] : b.Name))
                        AddNode(remoteBody, node, depth: 1, isRemote: true, currentCanonical);
                }
            }
        }

        // ── Tags ──
        var tagBody = Section($"Tags ({tags.Count})", _tagsOpen, () => { _tagsOpen = !_tagsOpen; Rebuild(); });
        if (tagBody is not null)
        {
            if (tags.Count == 0)
                tagBody.Children.Add(Leaf("No tags.", 0, "ColorTextFaint", null, null));
            foreach (var t in tags)
            {
                var tag = t;
                tagBody.Children.Add(Leaf(t.Name, 0, "ColorText",
                    select: () => Select(tag.TargetCommitId),
                    action: IconButton("delete", "Delete tag", () => _ = DeleteTagAsync(tag))));
            }
        }

        // ── Stashes / Submodules / Worktrees (cached) ──
        var stashBody = Section($"Stashes{Count(_stashList)}", _stashesOpen, () => { _stashesOpen = !_stashesOpen; Rebuild(); });
        if (stashBody is not null)
        {
            if (_stashList is null) stashBody.Children.Add(Leaf("Loading…", 0, "ColorTextFaint", null, null));
            else if (_stashList.Count == 0) stashBody.Children.Add(Leaf("No stashes.", 0, "ColorTextFaint", null, null));
            else foreach (var s in _stashList)
            {
                var entry = s;
                var menuBtn = new Button { Content = "⋯", Padding = new Thickness(4, 0), Background = Brushes.Transparent, BorderThickness = default };
                var mf = new MenuFlyout();
                var mfItems = new List<Control>();
                foreach (var (label, act) in new (string, Action)[]
                {
                    ("Apply", () => _ = StashOp(() => _stashes.ApplyAsync(_repo!, entry.CommitId, restoreIndex: true, _repo!.Lifetime))),
                    ("Apply & drop", () => _ = StashOp(() => _stashes.ApplyAndDropAsync(_repo!, entry.CommitId, _repo!.Lifetime))),
                    ("New branch from stash…", () => _ = StashBranchAsync(entry)),
                    ("Drop", () => _ = StashDropAsync(entry)),
                })
                {
                    var mi = new MenuItem { Header = label };
                    var a = act;
                    mi.Click += (_, _) => a();
                    mfItems.Add(mi);
                }
                mf.ItemsSource = mfItems;
                menuBtn.Flyout = mf;
                stashBody.Children.Add(Leaf($"stash@{{{s.Index}}}  {s.Message}", 0, "ColorText",
                    select: () => Select(entry.CommitId), action: menuBtn));
            }
        }

        var subBody = Section($"Submodules{Count(_submoduleList)}", _submodulesOpen, () =>
        {
            _submodulesOpen = !_submodulesOpen;
            Rebuild();
            if (_submodulesOpen && (!_submodulesFetched || _submoduleList is null) && _repo is not null)
                _ = LoadPowerToolsAsync(_repo);
        });
        if (subBody is not null)
        {
            if (_submoduleList is null) subBody.Children.Add(Leaf("Loading…", 0, "ColorTextFaint", null, null));
            else if (_submoduleList.Count == 0) subBody.Children.Add(Leaf("No submodules.", 0, "ColorTextFaint", null, null));
            else foreach (var m in _submoduleList)
            {
                var sm = m;
                var menuBtn = new Button { Content = "⋯", Padding = new Thickness(4, 0), Background = Brushes.Transparent, BorderThickness = default };
                var mf = new MenuFlyout();
                AddMi(mf, "Init & update", () => _ = PowerOp(r => _submodules.InitAndUpdateAsync(r, sm.Path, recursive: true, r.Lifetime)));
                AddMi(mf, "Sync URL", () => _ = PowerOp(r => _submodules.SyncAsync(r, sm.Path, r.Lifetime)));
                AddMi(mf, "Remove", () => _ = SubmoduleRemoveAsync(sm));
                menuBtn.Flyout = mf;
                subBody.Children.Add(Leaf($"{m.Name}  ({m.Status})", 0, "ColorText", null, menuBtn));
            }
            if (_submoduleList is { Count: > 0 })
                subBody.Children.Add(Leaf("Update all", 0, "ColorTextMuted",
                    select: () => _ = PowerOp(r => _submodules.InitAndUpdateAsync(r, null, recursive: true, r.Lifetime)), null));
        }

        var wtBody = Section($"Worktrees{Count(_worktreeList)}", _worktreesOpen, () =>
        {
            _worktreesOpen = !_worktreesOpen;
            Rebuild();
            if (_worktreesOpen && (!_worktreesFetched || _worktreeList is null) && _repo is not null)
                _ = LoadPowerToolsAsync(_repo);
        });
        if (wtBody is not null)
        {
            if (_worktreeList is null) wtBody.Children.Add(Leaf("Loading…", 0, "ColorTextFaint", null, null));
            else if (_worktreeList.Count == 0) wtBody.Children.Add(Leaf("No linked worktrees.", 0, "ColorTextFaint", null, null));
            else foreach (var w in _worktreeList)
            {
                var wt = w;
                Button? menuBtn = null;
                if (!w.IsMainWorktree)
                {
                    menuBtn = new Button { Content = "⋯", Padding = new Thickness(4, 0), Background = Brushes.Transparent, BorderThickness = default };
                    var mf = new MenuFlyout();
                    if (wt.IsLocked) AddMi(mf, "Unlock", () => _ = PowerOp(r => _worktrees.UnlockAsync(r, wt.Path, r.Lifetime)));
                    else AddMi(mf, "Lock", () => _ = PowerOp(r => _worktrees.LockAsync(r, wt.Path, "", r.Lifetime)));
                    AddMi(mf, "Remove", () => _ = WorktreeRemoveAsync(wt));
                    menuBtn.Flyout = mf;
                }
                wtBody.Children.Add(Leaf($"{w.Branch}{(w.IsMainWorktree ? "  (main)" : "")}{(w.IsLocked ? "  🔒" : "")}", 0, "ColorText", null, menuBtn));
            }
            if (_worktreeList is { Count: > 0 })
                wtBody.Children.Add(Leaf("Prune stale", 0, "ColorTextMuted",
                    select: () => _ = PowerOp(r => _worktrees.PruneAsync(r, r.Lifetime)), null));
        }
    }

    private static string Count<T>(IReadOnlyList<T>? list) => list is null ? "" : $" ({list.Count})";

    /// <summary>Section header; returns the capped body panel when expanded, else null.</summary>
    private StackPanel? Section(string title, bool open, Action toggle, Action? add = null)
    {
        _root.Children.Add(SectionHeader(title, open, toggle, add));
        if (!open)
            return null;
        var body = new StackPanel();
        _root.Children.Add(new ScrollViewer
        {
            Content = body,
            MaxHeight = SectionMaxHeight,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        });
        return body;
    }

    private Control SectionHeader(string title, bool open, Action toggle, Action? add)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Height = 28 };
        var caret = Text(open ? "▾" : "▸", "ColorTextMuted", 10);
        caret.Margin = new Thickness(8, 0, 6, 0);
        grid.Children.Add(caret);
        var label = Text(title.ToUpperInvariant(), "ColorTextMuted", 11, FontWeight.SemiBold);
        label.LetterSpacing = 0.4;
        Grid.SetColumn(label, 1);
        grid.Children.Add(label);
        if (add is not null)
        {
            var plus = new Button { Content = "＋", Padding = new Thickness(8, 0), Background = Brushes.Transparent, BorderThickness = default };
            ToolTip.SetTip(plus, "New branch");
            plus.Click += (_, e) => { e.Handled = true; add(); };
            Grid.SetColumn(plus, 2);
            grid.Children.Add(plus);
        }
        var b = new Border { Child = grid, Cursor = new Cursor(StandardCursorType.Hand) };
        b.Dyn(Border.BorderBrushProperty, "ColorBorder");
        b.BorderThickness = new Thickness(0, 1, 0, 0);
        b.PointerPressed += (_, e) => { e.Handled = true; toggle(); };
        return b;
    }

    private void AddNode(StackPanel target, RefTreeNode node, int depth, bool isRemote, string? currentCanonical)
    {
        if (node.IsFolder)
        {
            target.Children.Add(FolderRow(node.Segment, node.FullPath, depth));
            if (IsOpen(node.FullPath, depth))
                foreach (var child in node.Children)
                    AddNode(target, child, depth + 1, isRemote, currentCanonical);
            return;
        }

        var b = node.Leaf!;
        var current = !isRemote && string.Equals(b.CanonicalName, currentCanonical, StringComparison.Ordinal);
        var row = Leaf(node.Segment, depth,
            current ? "ColorAccent" : "ColorText",
            select: () => Select(b.TipCommitId),
            action: null,
            current: current);
        row.DoubleTapped += (_, e) => { e.Handled = true; _ = CheckoutAsync(b, isRemote); };
        AttachBranchMenu(row, b, isRemote);
        target.Children.Add(row);
    }

    /// <summary>A collapsible folder row (remote root or path segment). Its label aligns with leaves.</summary>
    private Border FolderRow(string segment, string path, int depth)
    {
        var open = IsOpen(path, depth);
        var caret = Text(open ? "▾" : "▸", "ColorTextMuted", 10);
        // Caret sits 12 DIU left of the shared leaf indent so its label lands on that edge.
        caret.Margin = new Thickness(LeafIndent + depth * IndentStep - 12, 0, 2, 0);
        var name = Text(segment, "ColorTextMuted", 12);

        var stack = new StackPanel { Orientation = Orientation.Horizontal };
        stack.Children.Add(caret);
        stack.Children.Add(name);

        var b = new Border { Child = stack, Height = 24, Cursor = new Cursor(StandardCursorType.Hand) };
        b.PointerPressed += (_, e) =>
        {
            e.Handled = true;
            if (!_expanded.Remove(path)) _expanded.Add(path);
            Rebuild();
        };
        return b;
    }

    private Border Leaf(string text, int depth, string fgKey, Action? select, Control? action, bool current = false)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Height = 24 };

        var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        // Fixed indent spacer, then a 7-DIU dot slot (always present so labels align with or
        // without the checked-out marker), then the label.
        left.Children.Add(new Border { Width = LeafIndent + depth * IndentStep - 11 });
        var dot = new Ellipse { Width = 7, Height = 7, Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };
        dot.Dyn(Shape.FillProperty, current ? "ColorSuccess" : "ColorChrome");
        if (!current) dot.Opacity = 0;
        left.Children.Add(dot);
        left.Children.Add(Text(text, fgKey, 12, current ? FontWeight.SemiBold : FontWeight.Normal));
        grid.Children.Add(left);

        if (action is not null)
        {
            Grid.SetColumn(action, 1);
            grid.Children.Add(action);
        }
        var b = new Border { Child = grid, Height = 24, Cursor = new Cursor(StandardCursorType.Hand) };
        if (select is not null)
            b.PointerPressed += (_, e) => { e.Handled = true; select(); };
        return b;
    }

    /// <summary>v8 M4: the shared branch context menu on a sidebar branch row.</summary>
    private void AttachBranchMenu(Control row, BranchInfo b, bool isRemote)
    {
        if (_repo is not { } ctx)
            return;

        var shortLocal = isRemote && b.RemoteName is { } r && b.Name.StartsWith(r + "/", StringComparison.Ordinal)
            ? b.Name[(r.Length + 1)..]
            : b.Name;

        var target = new BranchMenu.Target(
            Name: shortLocal,
            CopyName: b.Name,
            TrackingRef: b.Name,
            IsLocal: !isRemote,
            TipSha: b.TipCommitId?.Sha,
            CurrentBranch: ctx.State.CurrentBranch?.Name);

        row.ContextMenu = new ContextMenu
        {
            ItemsSource = BranchMenu.Build(target, ctx, _dialogs, _notifications, _stashes,
                s => TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(s)),
        };
    }

    private void Select(LibGit2Sharp.ObjectId? id)
    {
        if (id is null || _repo is null) return;
        _repo.State.SetSelectedCommit(id);
        _repo.State.RequestRevealCommit(id);
    }

    private async Task CheckoutAsync(BranchInfo b, bool isRemote)
    {
        if (_repo is null || (b.IsCurrent && !isRemote)) return;
        var repo = _repo;

        var name = isRemote
            ? (b.RemoteName is { } rn && b.Name.StartsWith(rn + "/") ? b.Name[(rn.Length + 1)..]
               : b.Name.Contains('/') ? b.Name[(b.Name.IndexOf('/') + 1)..] : b.Name)
            : b.Name;

        await BranchMenu.RunWithStashRecovery(repo, _notifications, _stashes, async () =>
        {
            var localExists = repo.State.Branches.Any(x => !x.IsRemote && x.Name == name);
            if (isRemote && !localExists)
            {
                await repo.Gateway.CreateBranchAsync(name, b.Name, checkout: true, repo.Lifetime);
                await repo.Gateway.SetUpstreamAsync(name, b.Name, repo.Lifetime);
            }
            else
            {
                await repo.Gateway.CheckoutAsync(name, new GitCheckoutOptions(), repo.Lifetime);
            }
            _notifications.Publish(NotificationSeverity.Success, $"Switched to {name}.");
        }, "Checkout");
    }

    private async Task CreateBranchAsync()
    {
        if (_repo is null) return;
        var name = await _dialogs.PromptAsync("New branch", "Branch name:");
        if (string.IsNullOrWhiteSpace(name)) return;
        try
        {
            var start = _repo.State.CurrentBranch?.CanonicalName ?? "HEAD";
            await _repo.Gateway.CreateBranchAsync(name.Trim(), start, checkout: true, _repo.Lifetime);
            await _repo.State.RefreshAllAsync(_repo.Lifetime);
            _notifications.Publish(NotificationSeverity.Success, $"Created and checked out {name.Trim()}.");
        }
        catch (Exception ex)
        {
            _notifications.Publish(NotificationSeverity.Error, $"Could not create branch: {ex.Message}", ex.NotificationDetails());
        }
    }

    private async Task DeleteTagAsync(TagInfo tag)
    {
        if (_repo is null) return;
        if (!await _dialogs.ConfirmDestructiveAsync("Delete tag", $"Delete local tag '{tag.Name}'? This cannot be undone.", "Delete"))
            return;
        try
        {
            await _repo.Gateway.DeleteTagAsync(tag.Name, _repo.Lifetime);
            await _repo.State.RefreshRefsAsync(_repo.Lifetime);
        }
        catch (Exception ex)
        {
            _notifications.Publish(NotificationSeverity.Error, $"Delete failed: {ex.Message}", ex.NotificationDetails());
        }
    }

    private static void AddMi(MenuFlyout mf, string label, Action onClick)
    {
        var mi = new MenuItem { Header = label };
        mi.Click += (_, _) => onClick();
        if (mf.ItemsSource is not List<Control> list)
        {
            list = [];
            mf.ItemsSource = list;
        }
        list.Add(mi);
    }

    private async Task PowerOp(Func<RepositoryContext, Task> op)
    {
        if (_repo is null) return;
        try
        {
            await op(_repo);
            await _repo.State.RefreshAllAsync(_repo.Lifetime);
            _submoduleList = null;
            _worktreeList = null;
            Rebuild();
            _ = LoadPowerToolsAsync(_repo);
        }
        catch (Exception ex) { _notifications.Publish(NotificationSeverity.Error, $"Operation failed: {ex.Message}", ex.NotificationDetails()); }
    }

    private async Task WorktreeRemoveAsync(WorktreeInfo wt)
    {
        if (_repo is null) return;
        if (!await _dialogs.ConfirmDestructiveAsync("Remove worktree", $"Remove the worktree at '{wt.Path}'?", "Remove")) return;
        await PowerOp(r => _worktrees.RemoveAsync(r, wt.Path, force: false, r.Lifetime));
    }

    /// <summary>#19 (Rule 00): removing a submodule deinitialises it, removes it from the index
    /// and deletes .git/modules/&lt;name&gt; — the submodule's own repository — so its
    /// uncommitted changes and unpushed commits are gone for good.</summary>
    private async Task SubmoduleRemoveAsync(SubmoduleInfo sm)
    {
        if (_repo is null) return;
        if (!await _dialogs.ConfirmDestructiveAsync("Remove submodule",
                $"Remove submodule '{sm.Path}'? This deinitialises it, removes it from the " +
                $"index and deletes .git/modules/{sm.Name} — uncommitted changes and unpushed " +
                "commits inside the submodule are lost.", "Remove"))
            return;
        await PowerOp(r => _submodules.RemoveAsync(r, sm, r.Lifetime));
    }

    private async Task StashOp(Func<Task> op)
    {
        if (_repo is null) return;
        try
        {
            await op();
            await _repo.State.RefreshAllAsync(_repo.Lifetime);
            _stashList = null;
            Rebuild();
            _ = LoadPowerToolsAsync(_repo);
        }
        catch (Exception ex) { _notifications.Publish(NotificationSeverity.Error, $"Stash op failed: {ex.Message}"); }
    }

    private async Task StashBranchAsync(StashEntry entry)
    {
        if (_repo is null) return;
        var name = await _dialogs.PromptAsync("Branch from stash", "New branch name:");
        if (string.IsNullOrWhiteSpace(name)) return;
        await StashOp(() => _stashes.BranchFromAsync(_repo!, entry.CommitId, name.Trim(), _repo!.Lifetime));
    }

    private async Task StashDropAsync(StashEntry entry)
    {
        if (_repo is null) return;
        if (!await _dialogs.ConfirmDestructiveAsync("Drop stash", $"Drop '{entry.Message}'? This cannot be undone.", "Drop")) return;
        await StashOp(() => _stashes.DropAsync(_repo!, entry.CommitId, _repo!.Lifetime));
    }

    public void Dispose()
    {
        _workspace.FocusedRepositoryChanged -= OnFocusChanged;
        if (_repo is not null)
            _repo.State.RefsChanged -= OnRefsChanged;
    }
}
