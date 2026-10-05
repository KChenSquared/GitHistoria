namespace Historia.Views;

/// <summary>
/// The sidebar ref navigator. Local / remote / tag trees + stash / submodule / worktree sections.
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
    private readonly IRepoActionBus _actions;
    private readonly IExternalLauncher _launcher;

    internal readonly StackPanel _root = new();
    private RepositoryContext? _repo;

    // Indentation: leaves and folder labels share this left edge (past the section-header
    // label); each nested folder level adds IndentStep.
    private const double LeafIndent = 30;
    private const double IndentStep = 14;
    private const double SectionMaxHeight = 240;

    // Reserves space at the right of every row for the Fluent scroll bar's overlay hit area
    // (ScrollBarSize = 16 DIU, verified in Avalonia 11.3.20's FluentControlResources.xaml —
    // it's the ScrollBar control's own MinWidth/MinHeight, constant whether or not the bar is
    // expanded; only the visual thumb scales via a RenderTransform) plus 2 DIU of breathing room,
    // so trimmed text and row actions never sit under it.
    internal const double RowRightGutter = 18;

    // Section defaults: Local Branches / Remotes start expanded; Tags / Stashes / Submodules /
    // Worktrees start collapsed. Session-only, like _expanded below — the state survives section
    // toggles and repo switches, but is reset to these defaults on every app launch and whenever
    // the repo being viewed is closed from the app-band (see ResetSectionState / Bind). Folders
    // within a section have their own, depth-dependent default — see IsOpen.
    private bool _localOpen = true, _remotesOpen = true, _tagsOpen = false;
    private bool _stashesOpen = false, _submodulesOpen = false, _worktreesOpen = false;
    private bool _localFilterOpen, _remotesFilterOpen, _tagsFilterOpen, _stashesFilterOpen;

    internal readonly TextBox _localFilterBox;
    internal readonly TextBox _remotesFilterBox;
    internal readonly TextBox _tagsFilterBox;
    internal readonly TextBox _stashesFilterBox;

    internal bool LocalFilterOpen => _localFilterOpen;
    internal bool RemotesFilterOpen => _remotesFilterOpen;
    internal bool TagsFilterOpen => _tagsFilterOpen;
    internal bool StashesFilterOpen => _stashesFilterOpen;
    internal bool LocalOpen => _localOpen;
    internal bool RemotesOpen => _remotesOpen;
    internal bool TagsOpen => _tagsOpen;

    private void ResetSectionState()
    {
        _localOpen = _remotesOpen = true;
        _tagsOpen = _stashesOpen = _submodulesOpen = _worktreesOpen = false;
        _localFilterOpen = _remotesFilterOpen = _tagsFilterOpen = _stashesFilterOpen = false;
        if (_localFilterBox is not null) _localFilterBox.Text = string.Empty;
        if (_remotesFilterBox is not null) _remotesFilterBox.Text = string.Empty;
        if (_tagsFilterBox is not null) _tagsFilterBox.Text = string.Empty;
        if (_stashesFilterBox is not null) _stashesFilterBox.Text = string.Empty;
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

    /// <summary>Replaces the Add remote dialog flow behind the Remotes header's button; tests set
    /// it so pressing the button doesn't open a window.</summary>
    internal Action? AddRemoteOverrideForTesting;

    // The cloud on a remote's root row, and the slot it takes between the caret and the name.
    private const double RemoteCloudSize = 14;
    private const double RemoteCloudSlot = RemoteCloudSize + 3;

    private static readonly TimeSpan PowerToolsPaintCeiling = TimeSpan.FromSeconds(8);

    // Cached power-tool lists — null = not loaded yet. Filled once per bind / RefsChanged so a
    // section toggle re-renders synchronously instead of flashing empty during a re-fetch.
    private IReadOnlyList<StashEntry>? _stashList;
    private IReadOnlyList<SubmoduleInfo>? _submoduleList;
    private IReadOnlyList<WorktreeInfo>? _worktreeList;
    private bool _submodulesFetched;
    private bool _worktreesFetched;

    private long? _loadedStashStamp;
    private IReadOnlyList<BranchInfo>? _powerToolsBranches;
    private IReadOnlyList<RemoteInfo>? _powerToolsRemotes;
    private int _stashListGen;

    // Submodule paths (or AllSubmodulesBusyKey for "Update all") with an in-flight
    // "Init & update" — the row renders a busy label instead of its menu while present.
    private readonly HashSet<string> _submodulesBusy = new(StringComparer.Ordinal);
    private const string AllSubmodulesBusyKey = "*";

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
        _actions = sp.GetRequiredService<IRepoActionBus>();
        _launcher = sp.GetRequiredService<IExternalLauncher>();

        _localFilterBox = CreateFilterBox(() => _localFilterOpen = false);
        _remotesFilterBox = CreateFilterBox(() => _remotesFilterOpen = false);
        _tagsFilterBox = CreateFilterBox(() => _tagsFilterOpen = false);
        _stashesFilterBox = CreateFilterBox(() => _stashesFilterOpen = false);

        Content = new ScrollViewer { Content = _root, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        this.Dyn(BackgroundProperty, "ColorChrome");

        _workspace.FocusedRepositoryChanged += OnFocusChanged;
        Bind(_workspace.Focused);

        // a click on empty sidebar space returns the detail panel to the repo. Rows
        // that do something set e.Handled, so this only fires for background clicks.
        AddHandler(PointerReleasedEvent, (_, e) =>
        {
            if (!e.Handled && _repo is not null)
                _nav.RequestNavigate("repo");
        });
    }

    private TextBox CreateFilterBox(Action onClose)
    {
        var box = new TextBox
        {
            Watermark = "Filter…",
            FontSize = 12,
            Height = 24,
            Padding = new Thickness(6, 2),
            VerticalAlignment = VerticalAlignment.Center,
        };
        box.TextChanged += (_, _) => Rebuild();
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                onClose();
                box.Text = string.Empty;
                Rebuild();
            }
        };
        return box;
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
        {
            _loadedStashStamp = _repo.State.StashStamp;
            _powerToolsBranches = _repo.State.Branches;
            _powerToolsRemotes = _repo.State.Remotes;
            _ = LoadPowerToolsAsync(_repo, listStashes: true);
        }
        else
        {
            _loadedStashStamp = null;
            _powerToolsBranches = null;
            _powerToolsRemotes = null;
        }
    }

    private void OnRefsChanged(object? s, RefsChangedEventArgs e)
        => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            // A raise posted just before a rebind belongs to the previous repo; comparing it
            // against this repo's baselines would overwrite them with the old repo's state.
            if (_repo is null || !ReferenceEquals(s, _repo.State))
                return;

            var stashChanged = _loadedStashStamp is null || e.StashStamp != _loadedStashStamp;
            var refsChanged = _powerToolsBranches is null || _powerToolsRemotes is null
                || !e.Branches.SequenceEqual(_powerToolsBranches)
                || !e.Remotes.SequenceEqual(_powerToolsRemotes);

            // Stashes depend only on the stash stamp, and submodules and worktrees don't depend on tags.
            // When neither changed (a tags-only refresh), just rebuild the tree.
            if (!stashChanged && !refsChanged)
            {
                Rebuild();
                return;
            }

            if (stashChanged)
                _loadedStashStamp = e.StashStamp;
            if (refsChanged)
            {
                _powerToolsBranches = e.Branches;
                _powerToolsRemotes = e.Remotes;
                _submodulesFetched = false;
                _worktreesFetched = false;
            }

            var snap = _repoCache.TryLoad(_repo.RepositoryPath);
            if (stashChanged)
                _stashList = snap?.Stashes;
            if (refsChanged)
            {
                _submoduleList = snap?.Submodules;
                _worktreeList = snap?.Worktrees;
            }

            Rebuild();
            _ = LoadPowerToolsAsync(_repo, listStashes: stashChanged);
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

    private async Task LoadPowerToolsAsync(RepositoryContext repo, bool listStashes = true)
    {
        await Task.WhenAny(repo.FirstGraphPaint, Task.Delay(PowerToolsPaintCeiling, repo.Lifetime));
        if (!ReferenceEquals(repo, _repo) || repo.Lifetime.IsCancellationRequested)
            return;

        int stashGen = 0;
        Task<IReadOnlyList<StashEntry>?>? stashTask = null;
        if (listStashes)
        {
            stashGen = ++_stashListGen;
            stashTask = Safe(() => _stashes.ListAsync(repo, repo.Lifetime));
        }

        var subsTask = _submodulesOpen ? Safe(() => _submodules.ListAsync(repo, repo.Lifetime)) : Task.FromResult<IReadOnlyList<SubmoduleInfo>?>(_submoduleList);
        var wtsTask = _worktreesOpen ? Safe(() => _worktrees.ListAsync(repo, repo.Lifetime)) : Task.FromResult<IReadOnlyList<WorktreeInfo>?>(_worktreeList);

        var stash = stashTask is not null ? await stashTask : null;
        var subs = await subsTask;
        var wts = await wtsTask;
        if (!ReferenceEquals(repo, _repo) || repo.Lifetime.IsCancellationRequested)
            return;

        if (listStashes && stashGen == _stashListGen)
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
        TextBox? focusedBox = null;
        int selStart = 0;
        int selEnd = 0;
        if (_localFilterBox is not null && _localFilterBox.IsFocused) { focusedBox = _localFilterBox; selStart = _localFilterBox.SelectionStart; selEnd = _localFilterBox.SelectionEnd; }
        else if (_remotesFilterBox is not null && _remotesFilterBox.IsFocused) { focusedBox = _remotesFilterBox; selStart = _remotesFilterBox.SelectionStart; selEnd = _remotesFilterBox.SelectionEnd; }
        else if (_tagsFilterBox is not null && _tagsFilterBox.IsFocused) { focusedBox = _tagsFilterBox; selStart = _tagsFilterBox.SelectionStart; selEnd = _tagsFilterBox.SelectionEnd; }
        else if (_stashesFilterBox is not null && _stashesFilterBox.IsFocused) { focusedBox = _stashesFilterBox; selStart = _stashesFilterBox.SelectionStart; selEnd = _stashesFilterBox.SelectionEnd; }

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
            () => { _localOpen = !_localOpen; Rebuild(); },
            filter: () =>
            {
                _localFilterOpen = !_localFilterOpen;
                if (_localFilterOpen) _localOpen = true;
                Rebuild();
                if (_localFilterOpen) _localFilterBox?.Focus();
            },
            filterOpen: _localFilterOpen,
            filterBox: _localFilterBox);
        if (localBody is not null)
        {
            if (_localFilterOpen && !string.IsNullOrWhiteSpace(_localFilterBox?.Text))
            {
                var query = FuzzyScorer.PrepareQuery(_localFilterBox.Text);
                var ranked = RefSearch.Rank(local, b => b.Name, query);
                if (ranked.Count == 0)
                {
                    localBody.Children.Add(Leaf("No matches.", 0, "ColorTextFaint", null, null));
                }
                else
                {
                    foreach (var item in ranked)
                    {
                        var b = item.Item;
                        var current = !b.IsRemote && string.Equals(b.CanonicalName, currentCanonical, StringComparison.Ordinal);
                        var row = Leaf(b.Name, depth: 0,
                            current ? "ColorAccent" : "ColorText",
                            select: () => Select(b.TipCommitId),
                            action: null,
                            current: current,
                            tooltip: b.Name);
                        row.DoubleTapped += (_, e) => { e.Handled = true; _ = CheckoutAsync(b, isRemote: false); };
                        AttachBranchMenu(row, b, isRemote: false);
                        localBody.Children.Add(row);
                    }
                }
                localBody.Children.Add(new Border { Height = 1, Margin = new Thickness(0, 2) }.Dyn(Border.BackgroundProperty, "ColorBorder"));
            }

            foreach (var node in RefTree.Build(local, b => b.Name))
                AddNode(localBody, node, depth: 0, isRemote: false, currentCanonical);
        }

        // ── Remotes: one collapsible folder per remote ──
        var addRemote = IconButton("add", "Add remote…", AddRemote);
        var remoteBody = Section($"Remotes ({repo.State.Remotes.Count})", _remotesOpen,
            () => { _remotesOpen = !_remotesOpen; Rebuild(); },
            filter: () =>
            {
                _remotesFilterOpen = !_remotesFilterOpen;
                if (_remotesFilterOpen) _remotesOpen = true;
                Rebuild();
                if (_remotesFilterOpen) _remotesFilterBox?.Focus();
            },
            filterOpen: _remotesFilterOpen,
            filterBox: _remotesFilterBox,
            extra: (addRemote, AddRemote));
        if (remoteBody is not null)
        {
            if (_remotesFilterOpen && !string.IsNullOrWhiteSpace(_remotesFilterBox?.Text))
            {
                var query = FuzzyScorer.PrepareQuery(_remotesFilterBox.Text);
                var ranked = RefSearch.Rank(remote, b => b.Name, query);
                if (ranked.Count == 0)
                {
                    remoteBody.Children.Add(Leaf("No matches.", 0, "ColorTextFaint", null, null));
                }
                else
                {
                    foreach (var item in ranked)
                    {
                        var b = item.Item;
                        var row = Leaf(b.Name, depth: 0,
                            "ColorText",
                            select: () => Select(b.TipCommitId),
                            action: null,
                            current: false,
                            tooltip: b.Name);
                        row.DoubleTapped += (_, e) => { e.Handled = true; _ = CheckoutAsync(b, isRemote: true); };
                        AttachBranchMenu(row, b, isRemote: true);
                        remoteBody.Children.Add(row);
                    }
                }
                remoteBody.Children.Add(new Border { Height = 1, Margin = new Thickness(0, 2) }.Dyn(Border.BackgroundProperty, "ColorBorder"));
            }

            // Every configured remote gets a row, including one with no remote-tracking branches
            // yet. Tracking-branch groups whose alias is no longer configured (stale refs) still
            // render, without a remote menu or a number. Rows sort with the numbering's comparer,
            // so a remote's number matches its position among the configured rows.
            var configured = repo.State.Remotes.ToDictionary(r => r.Name, StringComparer.Ordinal);
            var numbering = RemoteNumbering.From(configured.Keys);
            var byRemote = remote
                .GroupBy(b => b.RemoteName ?? (b.Name.Contains('/') ? b.Name[..b.Name.IndexOf('/')] : "origin"))
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
            foreach (var alias in configured.Keys.Union(byRemote.Keys, StringComparer.Ordinal).OrderBy(a => a, RemoteNumbering.Comparer))
            {
                var key = "remote:" + alias;
                configured.TryGetValue(alias, out var info);
                remoteBody.Children.Add(FolderRow(alias, key, depth: 0, remote: info, numbering: numbering));
                if (IsOpen(key, depth: 0) && byRemote.TryGetValue(alias, out var branchesOfRemote))
                {
                    var prefix = alias + "/";
                    foreach (var node in RefTree.Build(branchesOfRemote, b => b.Name.StartsWith(prefix, StringComparison.Ordinal) ? b.Name[prefix.Length..] : b.Name))
                        AddNode(remoteBody, node, depth: 1, isRemote: true, currentCanonical);
                }
            }
        }

        // ── Tags ──
        var tagBody = Section($"Tags ({tags.Count})", _tagsOpen,
            () => { _tagsOpen = !_tagsOpen; Rebuild(); },
            filter: () =>
            {
                _tagsFilterOpen = !_tagsFilterOpen;
                if (_tagsFilterOpen) _tagsOpen = true;
                Rebuild();
                if (_tagsFilterOpen) _tagsFilterBox?.Focus();
            },
            filterOpen: _tagsFilterOpen,
            filterBox: _tagsFilterBox);
        if (tagBody is not null)
        {
            if (_tagsFilterOpen && !string.IsNullOrWhiteSpace(_tagsFilterBox?.Text))
            {
                var query = FuzzyScorer.PrepareQuery(_tagsFilterBox.Text);
                var ranked = RefSearch.Rank(tags, t => t.Name, query);
                if (ranked.Count == 0)
                {
                    tagBody.Children.Add(Leaf("No matches.", 0, "ColorTextFaint", null, null));
                }
                else
                {
                    foreach (var item in ranked)
                    {
                        var tag = item.Item;
                        tagBody.Children.Add(Leaf(tag.Name, 0, "ColorText",
                            select: () => Select(tag.TargetCommitId),
                            action: IconButton("delete", "Delete tag", () => _ = DeleteTagAsync(tag))));
                    }
                }
                tagBody.Children.Add(new Border { Height = 1, Margin = new Thickness(0, 2) }.Dyn(Border.BackgroundProperty, "ColorBorder"));
            }

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
        var stashBody = Section($"Stashes{Count(_stashList)}", _stashesOpen,
            () => { _stashesOpen = !_stashesOpen; Rebuild(); },
            filter: () =>
            {
                _stashesFilterOpen = !_stashesFilterOpen;
                if (_stashesFilterOpen) _stashesOpen = true;
                Rebuild();
                if (_stashesFilterOpen) _stashesFilterBox?.Focus();
            },
            filterOpen: _stashesFilterOpen,
            filterBox: _stashesFilterBox);
        if (stashBody is not null)
        {
            if (_stashList is null) stashBody.Children.Add(Leaf("Loading…", 0, "ColorTextFaint", null, null));
            else if (_stashList.Count == 0) stashBody.Children.Add(Leaf("No stashes.", 0, "ColorTextFaint", null, null));
            else
            {
                if (_stashesFilterOpen && !string.IsNullOrWhiteSpace(_stashesFilterBox?.Text))
                {
                    var query = FuzzyScorer.PrepareQuery(_stashesFilterBox.Text);
                    var ranked = RefSearch.Rank(_stashList, s => $"stash@{{{s.Index}}}  {s.Message}", query);
                    if (ranked.Count == 0)
                    {
                        stashBody.Children.Add(Leaf("No matches.", 0, "ColorTextFaint", null, null));
                    }
                    else
                    {
                        foreach (var item in ranked)
                        {
                            var entry = item.Item;
                            stashBody.Children.Add(Leaf($"stash@{{{entry.Index}}}  {entry.Message}", 0, "ColorText",
                                select: () => Select(entry.CommitId), action: StashMenuButton(entry)));
                        }
                    }
                    stashBody.Children.Add(new Border { Height = 1, Margin = new Thickness(0, 2) }.Dyn(Border.BackgroundProperty, "ColorBorder"));
                }

                foreach (var s in _stashList)
                {
                    var entry = s;
                    stashBody.Children.Add(Leaf($"stash@{{{s.Index}}}  {s.Message}", 0, "ColorText",
                        select: () => Select(entry.CommitId), action: StashMenuButton(entry)));
                }
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
                var busy = _submodulesBusy.Contains(sm.Path);
                if (busy)
                {
                    subBody.Children.Add(Leaf(SubmoduleRowLabel(sm, busy: true), 0, "ColorTextFaint", null, null, tooltip: sm.Path));
                    continue;
                }

                var menuBtn = new Button { Content = "⋯", Padding = new Thickness(4, 0), Background = Brushes.Transparent, BorderThickness = default };
                menuBtn.Flyout = new MenuFlyout { ItemsSource = BuildSubmoduleMenuItems(sm) };
                var row = Leaf(SubmoduleRowLabel(sm, busy: false), 0, "ColorText", null, menuBtn, tooltip: sm.Path);
                row.ContextMenu = new ContextMenu { ItemsSource = BuildSubmoduleMenuItems(sm) };
                row.DoubleTapped += (_, e) => { e.Handled = true; _ = OpenSubmoduleTabAsync(sm); };
                subBody.Children.Add(row);
            }
            if (_submoduleList is { Count: > 0 })
            {
                var allBusy = _submodulesBusy.Contains(AllSubmodulesBusyKey);
                subBody.Children.Add(Leaf(allBusy ? "Update all — Initializing…" : "Update all", 0, "ColorTextMuted",
                    select: allBusy ? null : () => _ = SubmoduleInitAndUpdateAsync(null), null));
            }
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

        if (focusedBox is not null)
        {
            var target = focusedBox;
            var start = selStart;
            var end = selEnd;
            Dispatcher.UIThread.Post(() =>
            {
                target.Focus();
                target.SelectionStart = start;
                target.SelectionEnd = end;
            });
        }
    }

    private Button StashMenuButton(StashEntry entry)
    {
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
        return menuBtn;
    }

    private static string Count<T>(IReadOnlyList<T>? list) => list is null ? "" : $" ({list.Count})";

    /// <summary>Section header; returns the capped body panel when expanded, else null.</summary>
    private StackPanel? Section(string title, bool open, Action toggle, Action? filter = null, bool filterOpen = false,
        TextBox? filterBox = null, (Button Button, Action Run)? extra = null)
    {
        _root.Children.Add(SectionHeader(title, open, toggle, filter, extra));
        if (open && filterOpen && filterBox is not null)
        {
            filterBox.Margin = new Thickness(LeafIndent, 2, RowRightGutter, 4);
            _root.Children.Add(filterBox);
        }
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

    /// <summary>A section's header row. <paramref name="extra"/> is an optional action button drawn
    /// before the filter button; like the filter button it consumes its own press so it never
    /// toggles the section.</summary>
    private Control SectionHeader(string title, bool open, Action toggle, Action? filter = null,
        (Button Button, Action Run)? extra = null)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"), Height = 28 };
        var caret = Text(open ? "▾" : "▸", "ColorTextMuted", 10);
        caret.Margin = new Thickness(8, 0, 6, 0);
        grid.Children.Add(caret);
        var label = Text(title.ToUpperInvariant(), "ColorTextMuted", 11, FontWeight.SemiBold);
        label.LetterSpacing = 0.4;
        Grid.SetColumn(label, 1);
        grid.Children.Add(label);
        if (extra is { } ex)
        {
            var (extraBtn, run) = ex;
            extraBtn.AddHandler(PointerPressedEvent, (_, e) =>
            {
                e.Handled = true;
                if (e.GetCurrentPoint(extraBtn).Properties.IsLeftButtonPressed)
                    run();
            }, RoutingStrategies.Tunnel);
            Grid.SetColumn(extraBtn, 2);
            grid.Children.Add(extraBtn);
        }
        if (filter is not null)
        {
            var filterBtn = IconButton("filter_alt", "Filter this section", () => filter());
            filterBtn.AddHandler(PointerPressedEvent, (_, e) =>
            {
                e.Handled = true;
                filter();
            }, RoutingStrategies.Tunnel);
            Grid.SetColumn(filterBtn, 3);
            grid.Children.Add(filterBtn);
        }
        var b = new Border { Child = grid, Cursor = new Cursor(StandardCursorType.Hand), Padding = new Thickness(0, 0, RowRightGutter, 0) };
        b.Dyn(Border.BorderBrushProperty, "ColorBorder");
        b.BorderThickness = new Thickness(0, 1, 0, 0);
        b.PointerPressed += (_, e) =>
        {
            if (e.Handled) return;
            e.Handled = true;
            toggle();
        };
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
            current: current,
            tooltip: b.Name);
        row.DoubleTapped += (_, e) => { e.Handled = true; _ = CheckoutAsync(b, isRemote); };
        AttachBranchMenu(row, b, isRemote);
        target.Children.Add(row);
    }

    /// <summary>A collapsible folder row (remote root or path segment). Its label aligns with leaves.
    /// <paramref name="numbering"/> is non-null only for a remote's own root row (configured or
    /// stale), which then shows a cloud carrying the remote's number between the caret and the
    /// name. <paramref name="remote"/> is non-null only for a configured remote's root row and adds
    /// the remote context menu.</summary>
    private Border FolderRow(string segment, string path, int depth, RemoteInfo? remote = null, RemoteNumbering? numbering = null)
    {
        var open = IsOpen(path, depth);
        var caret = Text(open ? "▾" : "▸", "ColorTextMuted", 10);
        // Caret sits 12 DIU left of the shared leaf indent so its label lands on that edge; a
        // remote root's cloud takes its slot out of the caret's indent, down to a 4 DIU inset.
        var caretLeft = LeafIndent + depth * IndentStep - 12;
        if (numbering is not null)
            caretLeft = Math.Max(4, caretLeft - RemoteCloudSlot);
        caret.Margin = new Thickness(caretLeft, 0, 2, 0);
        var name = Text(segment, "ColorTextMuted", 12);
        name.VerticalAlignment = VerticalAlignment.Center;

        Grid grid;
        if (numbering is not null)
        {
            grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*") };
            var cloud = RemoteCloudGlyph.CreateIcon(numbering.NumberOf(segment), RemoteCloudSize, "ColorTextMuted", "ColorChrome");
            cloud.Margin = new Thickness(0, 0, RemoteCloudSlot - RemoteCloudSize, 0);
            Grid.SetColumn(cloud, 1);
            grid.Children.Add(caret);
            grid.Children.Add(cloud);
            Grid.SetColumn(name, 2);
            grid.Children.Add(name);
        }
        else
        {
            grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
            grid.Children.Add(caret);
            Grid.SetColumn(name, 1);
            grid.Children.Add(name);
        }

        // Transparent, not null: the whole row (caret gutter and the space past the name) is the
        // click and context-menu target, not just the glyphs.
        var b = new Border
        {
            Child = grid, Height = 24, Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand), Padding = new Thickness(0, 0, RowRightGutter, 0),
        };
        ToolTip.SetTip(b, numbering?.TooltipOf(segment) ?? segment);
        // Trap: only a left press toggles. The context menu opens on the right button's release,
        // and Rebuild() replaces every row, so rebuilding on a right press detaches this row (and
        // moves pointer capture to the root panel) before the menu can open.
        b.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(b).Properties.IsLeftButtonPressed)
                return;
            e.Handled = true;
            if (!_expanded.Remove(path)) _expanded.Add(path);
            Rebuild();
        };

        if (remote is not null && _repo is { } ctx)
        {
            b.ContextMenu = new ContextMenu
            {
                ItemsSource = RemoteMenu.Build(remote, ctx, _dialogs, _notifications, _actions, _launcher,
                    s => TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(s)),
            };
        }
        return b;
    }

    private void AddRemote()
    {
        if (AddRemoteOverrideForTesting is { } over)
        {
            over();
            return;
        }
        if (_repo is { } ctx)
            _ = RemoteMenu.AddAsync(ctx, _notifications, _actions);
    }

    private Border Leaf(string text, int depth, string fgKey, Action? select, Control? action, bool current = false, string? tooltip = null)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto"), Height = 24 };

        // Fixed indent spacer, then a 7-DIU dot slot (always present so labels align with or
        // without the checked-out marker), then the label in the '*' column so it gets a finite
        // width and CharacterEllipsis engages, then the row action.
        var spacer = new Border { Width = LeafIndent + depth * IndentStep - 11 };
        grid.Children.Add(spacer);

        var dot = new Ellipse { Width = 7, Height = 7, Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };
        dot.Dyn(Shape.FillProperty, current ? "ColorSuccess" : "ColorChrome");
        if (!current) dot.Opacity = 0;
        Grid.SetColumn(dot, 1);
        grid.Children.Add(dot);

        var label = Text(text, fgKey, 12, current ? FontWeight.SemiBold : FontWeight.Normal);
        label.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(label, 2);
        grid.Children.Add(label);

        if (action is not null)
        {
            Grid.SetColumn(action, 3);
            grid.Children.Add(action);
        }
        var b = new Border { Child = grid, Height = 24, Cursor = new Cursor(StandardCursorType.Hand), Padding = new Thickness(0, 0, RowRightGutter, 0) };
        if (select is not null)
            b.PointerPressed += (_, e) => { e.Handled = true; select(); };
        ToolTip.SetTip(b, tooltip ?? text);
        return b;
    }

    /// <summary>The shared branch context menu on a sidebar branch row.</summary>
    private void AttachBranchMenu(Control row, BranchInfo b, bool isRemote)
    {
        if (_repo is not { } ctx)
            return;

        var shortLocal = isRemote && b.RemoteName is { } r && b.Name.StartsWith(r + "/", StringComparison.Ordinal)
            ? b.Name[(r.Length + 1)..]
            : b.Name;

        // The counterpart to measure divergence against, and the remote alias that owns
        // it — resolved via BranchInfo.RemoteName (reliable even when the alias itself contains
        // a slash), never by splitting Name/UpstreamName on '/'.
        string? counterpartRef = null, remoteAlias = null;
        if (!isRemote && b.UpstreamName is { } upstream)
        {
            counterpartRef = upstream;
            remoteAlias = ctx.State.Branches.FirstOrDefault(x => x.IsRemote && x.Name == upstream)?.RemoteName;
        }
        else if (isRemote)
        {
            var localCounterpart = ctx.State.Branches.FirstOrDefault(x => !x.IsRemote && x.Name == shortLocal);
            if (localCounterpart is not null)
                counterpartRef = shortLocal;
            remoteAlias = b.RemoteName;
        }

        var target = new BranchMenu.Target(
            Name: shortLocal,
            CopyName: b.Name,
            TrackingRef: b.Name,
            IsLocal: !isRemote,
            TipSha: b.TipCommitId?.ToString(),
            CurrentBranch: ctx.State.CurrentBranch?.Name,
            CounterpartRef: counterpartRef,
            RemoteAlias: remoteAlias);

        var baseItems = BranchMenu.Build(target, ctx, _dialogs, _notifications, _stashes,
            s => TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(s));

        var menu = new ContextMenu { ItemsSource = baseItems };
        // The divergence lookup shells out to git — resolve it lazily on Opening so the
        // popup never blocks on a git process, and show the base menu immediately.
        menu.Opening += async (_, _) =>
        {
            var opened = menu;
            IReadOnlyList<Control> divergenceItems;
            try { divergenceItems = await BranchMenu.BuildDivergenceItemsAsync(target, ctx.Gateway, _actions, ctx.Lifetime); }
            catch { return; }
            if (divergenceItems.Count == 0 || !ReferenceEquals(menu, opened) || !menu.IsOpen)
                return;
            var afterCheckout = baseItems.Take(1).ToList();
            afterCheckout.AddRange(divergenceItems);
            afterCheckout.AddRange(baseItems.Skip(1));
            menu.ItemsSource = afterCheckout;
        };
        row.ContextMenu = menu;
    }

    private void Select(GitObjectId? id)
    {
        if (id is null || _repo is null) return;
        _repo.State.SetSelectedCommit(id);
        _repo.State.RequestRevealCommit(id.Value);
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
            _notifications.Publish(NotificationSeverity.Error, ex.NotificationMessage("Delete"), ex.NotificationDetails());
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
        catch (OperationCanceledException) { /* tab closed or torn down mid-operation — not a failure */ }
        catch (Exception ex) { _notifications.Publish(NotificationSeverity.Error, ex.NotificationMessage("Operation"), ex.NotificationDetails()); }
    }

    /// <summary>Wraps a submodule "Init & update" (per-row, or <c>null</c> path for "Update
    /// all") with a busy state so the row shows feedback instead of looking hung.</summary>
    private async Task SubmoduleInitAndUpdateAsync(string? path)
    {
        var key = path ?? AllSubmodulesBusyKey;
        _submodulesBusy.Add(key);
        Rebuild();
        try
        {
            await PowerOp(r => _submodules.InitAndUpdateAsync(r, path, true, r.Lifetime));
        }
        finally
        {
            _submodulesBusy.Remove(key);
            Rebuild();
        }
    }

    /// <summary>A fresh <see cref="MenuItem"/> list per call — an <see cref="ItemsControl"/>
    /// item can only have one visual parent, so the "⋯" flyout and the row's right-click
    /// <see cref="ContextMenu"/> each need their own instances.</summary>
    private List<Control> BuildSubmoduleMenuItems(SubmoduleInfo sm)
    {
        var list = new List<Control>();
        void Add(string label, Action onClick)
        {
            var mi = new MenuItem { Header = label };
            mi.Click += (_, _) => onClick();
            list.Add(mi);
        }
        Add("Init & update", () => _ = SubmoduleInitAndUpdateAsync(sm.Path));
        Add("Sync URL", () => _ = PowerOp(r => _submodules.SyncAsync(r, sm.Path, r.Lifetime)));
        Add("Remove", () => _ = SubmoduleRemoveAsync(sm));
        return list;
    }

    /// <summary>The submodule row's label, busy or not — extracted so it can be unit tested
    /// without constructing the view. Shows the leaf folder name only; the full path is
    /// surfaced via the row's tooltip instead, since nested submodule paths get too long
    /// to read comfortably in the panel.</summary>
    internal static string SubmoduleRowLabel(SubmoduleInfo sm, bool busy)
    {
        var name = SubmoduleFolderName(sm.Name);
        return busy ? $"{name}  — Initializing…" : $"{name}  ({sm.Status})";
    }

    /// <summary>Leaf folder name of a submodule path (which always uses '/' separators,
    /// even on Windows, since it comes from .gitmodules / git plumbing).</summary>
    internal static string SubmoduleFolderName(string path)
    {
        var trimmed = path.TrimEnd('/');
        var i = trimmed.LastIndexOf('/');
        return i >= 0 ? trimmed[(i + 1)..] : trimmed;
    }

    /// <summary>Double-clicking a submodule row opens (or focuses) its linked tab.</summary>
    private async Task OpenSubmoduleTabAsync(SubmoduleInfo sm)
    {
        if (_repo is null) return;
        try
        {
            await _workspace.OpenSubmoduleAsync(_repo, sm, _repo.Lifetime);
            _nav.RequestNavigate("repo");
        }
        catch (Exception ex)
        {
            _notifications.Publish(NotificationSeverity.Error,
                $"Could not open submodule: {ex.Message}", ex.NotificationDetails());
        }
    }

    private async Task WorktreeRemoveAsync(WorktreeInfo wt)
    {
        if (_repo is null) return;
        if (!await _dialogs.ConfirmDestructiveAsync("Remove worktree", $"Remove the worktree at '{wt.Path}'?", "Remove")) return;
        await PowerOp(r => _worktrees.RemoveAsync(r, wt.Path, force: false, r.Lifetime));
    }

    /// <summary>Removing a submodule deinitialises it, removes it from the index
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
        catch (OperationCanceledException) { /* tab closed or torn down mid-operation — not a failure */ }
        catch (Exception ex) { _notifications.Publish(NotificationSeverity.Error, ex.NotificationMessage("Stash op")); }
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
