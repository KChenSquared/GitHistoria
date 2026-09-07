using Avalonia.VisualTree;
using Historia.Services.Git;
using Historia.Services.Profiles;
using Historia.Services.Settings;
using Historia.Services.State;
using static Historia.Views.ViewKit;

namespace Historia.Views;

/// <summary>
/// Working tree, staging area and commit box. Changes (unstaged) and Staged are the two
/// resizable sections, with a checkbox column on each and a green ⬇ Stage / red ⬆ Unstage
/// arrow pair between them acting on the checked rows. The commit-message strip is fixed size
/// and pinned to the bottom, below both (v8.2) — it doesn't trade space with either list.
/// </summary>
public sealed class StagingPanelView : UserControl, IDisposable
{
    // Per-repo commit-message draft, kept for the session (V3 DraftByRepo).
    private static readonly Dictionary<string, string> DraftByRepo = new(StringComparer.Ordinal);

    private readonly RepositoryContext _ctx;
    private readonly INotificationService _notifications;
    private readonly IDialogService _dialogs;
    private readonly ISettingsService _settings;
    private readonly IProfileService _profiles;
    private readonly IKeyboardCommandRouter _keyboard;
    private readonly Historia.Services.UI.IExternalLauncher _launcher;
    private readonly Historia.Services.PowerTools.IStashService _stash;
    private IDisposable? _commitShortcut;

    private readonly TextBox _message = new()
    {
        AcceptsReturn = true,
        Height = 96,
        Watermark = "Commit message (subject; blank line; body)",
        TextWrapping = TextWrapping.Wrap,
        Padding = new Thickness(6, 6, 6, 20), // reserve space for the in-box counter
    };
    private readonly TextBox _author = new() { Watermark = "Author override (Name <email>)" };
    private readonly CheckBox _amend = new() { Content = "Amend last commit" };
    private readonly TextBlock _counter = Text("", "ColorTextMuted", 11);
    private readonly Button _commit;
    private readonly Button _stashStaged;

    private readonly StackPanel _changesRows = new() { Spacing = 1 };
    private readonly StackPanel _stagedRows = new() { Spacing = 1 };
    // Header labels and the bulk Stage-all / Unstage-all buttons share one font size (13 — the
    // midpoint of the old 12 header / 14 default-button sizes) so the section-header row reads
    // as one line.
    private const double HeaderRowFontSize = 13;
    private readonly TextBlock _changesHeader = Text("", "ColorTextMuted", HeaderRowFontSize, FontWeight.SemiBold);
    private readonly TextBlock _stagedHeader = Text("", "ColorTextMuted", HeaderRowFontSize, FontWeight.SemiBold);
    private readonly CheckBox _selectAllChanges = new();
    private readonly CheckBox _selectAllStaged = new();
    private readonly Button _stageArrow;
    private readonly Button _unstageArrow;
    private readonly Button _discardAllChanges;
    private readonly Border _conflictStrip = new() { IsVisible = false };
    private readonly StackPanel _conflictRows = new() { Spacing = 1 };

    // Checked paths per section, preserved across StatusChanged re-renders where the path still exists.
    private readonly HashSet<string> _checkedChanges = new(StringComparer.Ordinal);
    private readonly HashSet<string> _checkedStaged = new(StringComparer.Ordinal);

    private RepositoryStatus _status = RepositoryStatus.Empty;
    private bool _committing;
    private bool _stashing;
    private bool _suppressSelectAll;

    // v8.1: matches RefNavigatorView.SectionMaxHeight — the commit-message section caps its
    // content at the same height as the left-hand ref navigator's sections, so a long message
    // scrolls internally instead of growing the (fixed-size, v8.2) message strip without bound.
    private const double SectionMaxHeight = 240;
    private const double StageSplitterHeight = 28;

    public StagingPanelView(IServiceProvider sp, RepositoryContext ctx)
    {
        _ctx = ctx;
        _notifications = sp.GetRequiredService<INotificationService>();
        _dialogs = sp.GetRequiredService<IDialogService>();
        _settings = sp.GetRequiredService<ISettingsService>();
        _profiles = sp.GetRequiredService<IProfileService>();
        _keyboard = sp.GetRequiredService<IKeyboardCommandRouter>();
        _launcher = sp.GetRequiredService<Historia.Services.UI.IExternalLauncher>();
        _stash = sp.GetRequiredService<Historia.Services.PowerTools.IStashService>();

        _commit = TextButton("Commit", () => _ = CommitAsync(false), primary: true);
        var commitPush = TextButton("Commit & Push", () => _ = CommitAsync(true));
        _stashStaged = TextButton("Stash Staged", () => _ = StashStagedAsync());
        ToolTip.SetTip(_stashStaged, "Stash only the staged changes, leaving unstaged edits in the working tree");

        _message.Text = DraftByRepo.GetValueOrDefault(ctx.Id, "");
        ScrollViewer.SetVerticalScrollBarVisibility(_message, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
        _message.TextChanged += (_, _) => UpdateCounter();
        UpdateCounter();
        _commitShortcut = _keyboard.Register("Ctrl+Enter", () => CommitAsync(false));

        _selectAllChanges.IsThreeState = true;
        _selectAllStaged.IsThreeState = true;
        _selectAllChanges.Click += (_, _) => ToggleSelectAll(staged: false);
        _selectAllStaged.Click += (_, _) => ToggleSelectAll(staged: true);

        _stageArrow = ArrowButton("arrow-down", "ColorSuccess", "Stage", () => _ = MoveCheckedAsync(stage: true));
        _unstageArrow = ArrowButton("arrow-up", "ColorDanger", "Unstage", () => _ = MoveCheckedAsync(stage: false));

        // Glyph sized just above the 13 px header-row text so the bordered trash pill matches
        // the Stage-all / Unstage-all pill height.
        var trashGlyph = IconGlyphPath("trash", 14, "ColorDanger");
        _discardAllChanges = new Button
        {
            Content = trashGlyph,
            Padding = new Thickness(6, 2),
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
            VerticalAlignment = VerticalAlignment.Center,
        };
        _discardAllChanges.Classes.Add("danger");
        _discardAllChanges.Dyn(Button.BorderBrushProperty, "ColorDanger");
        ToolTip.SetTip(_discardAllChanges, "Discard all changes");
        _discardAllChanges.Click += (_, e) => { e.Handled = true; _ = DiscardAllChangesAsync(); };

        // ── message box + in-box counter ──
        var messageCell = new Grid();
        messageCell.Children.Add(_message);
        _counter.HorizontalAlignment = HorizontalAlignment.Right;
        _counter.VerticalAlignment = VerticalAlignment.Bottom;
        _counter.Margin = new Thickness(0, 0, 8, 4);
        _counter.IsHitTestVisible = false;
        messageCell.Children.Add(_counter);

        // Co-authored-by trailers from every other profile.
        var activeEmail = ctx.State.ActiveProfile?.GitUserEmail;
        var others = _profiles.GetAllProfiles()
            .Where(p => !string.Equals(p.GitUserEmail, activeEmail, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var guideRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        guideRow.Children.Add(_amend);
        if (others.Count > 0)
        {
            var coBtn = new Button { Content = "+ Co-authored-by", Background = Brushes.Transparent, BorderThickness = default, Padding = new Thickness(0) };
            var coMenu = new MenuFlyout();
            var coItems = new List<Control>();
            foreach (var p in others)
            {
                var pp = p;
                var mi = new MenuItem { Header = $"{p.GitUserName} <{p.GitUserEmail}>" };
                mi.Click += (_, _) => AddCoAuthor(pp.GitUserName, pp.GitUserEmail);
                coItems.Add(mi);
            }
            coMenu.ItemsSource = coItems;
            coBtn.Flyout = coMenu;
            guideRow.Children.Add(coBtn);
        }

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(_commit);
        actions.Children.Add(commitPush);
        actions.Children.Add(_stashStaged);

        var messageBox = new StackPanel { Spacing = 6, Margin = new Thickness(12, 8, 12, 12) };
        messageBox.Children.Add(messageCell);
        messageBox.Children.Add(guideRow);
        messageBox.Children.Add(_author);
        messageBox.Children.Add(actions);
        var messageSection = new Border
        {
            Child = new ScrollViewer
            {
                Content = messageBox,
                MaxHeight = SectionMaxHeight,
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            },
            MinHeight = 64,
            BorderThickness = new Thickness(1),
        }.Dyn(Border.BorderBrushProperty, "ColorBorder");

        // ── conflicted strip (only when there are conflicts) ──
        _conflictStrip.Padding = new Thickness(12, 6);
        _conflictStrip.BorderThickness = new Thickness(0, 0, 0, 1);
        _conflictStrip.Dyn(Border.BackgroundProperty, "ColorSurface").Dyn(Border.BorderBrushProperty, "ColorBorder");
        var conflictInner = new StackPanel { Spacing = 2 };
        conflictInner.Children.Add(Text("Conflicted — resolve these before committing", "ColorDanger", 12, FontWeight.SemiBold));
        conflictInner.Children.Add(_conflictRows);
        _conflictStrip.Child = conflictInner;

        // ── two resizable sections (Changes / Staged) plus a fixed-size commit-message strip
        // at the bottom. v8.2: the message box previously sat at the TOP of the resizable grid
        // and was itself draggable; now it's pinned to the bottom, sized to its own content
        // (capped at SectionMaxHeight, same as the left-hand ref navigator's sections, via the
        // ScrollViewer built above) rather than to a Grid row a user can drag — only Changes and
        // Staged trade space with each other. An Auto row does that for free: Grid gives Auto
        // rows exactly what their content asks for and splits whatever's left between the Star
        // rows, so nothing needs computing by hand the way the three-way split briefly did.
        var lists = new Grid { RowDefinitions = new RowDefinitions("1*,Auto,1*,Auto") };
        var changesSection = Section(_changesHeader, _selectAllChanges, _changesRows,
            bulkLabel: "Stage all", bulkClass: "success",
            bulk: () => _ = BulkAsync(_status.Unstaged.Concat(_status.Untracked).ToList(), staged: false),
            extraAction: _discardAllChanges);
        var stagedSection = Section(_stagedHeader, _selectAllStaged, _stagedRows,
            bulkLabel: "Unstage all", bulkClass: "danger",
            bulk: () => _ = BulkAsync(_status.Staged, staged: true));
        Grid.SetRow(changesSection, 0);
        Grid.SetRow(stagedSection, 2);
        Grid.SetRow(messageSection, 3);
        lists.Children.Add(changesSection);
        lists.Children.Add(stagedSection);
        lists.Children.Add(messageSection);

        // Tall enough for the bordered arrow buttons (~24 px) to sit inside the row instead of
        // bleeding over the section borders above and below.
        var stageSplitter = new GridSplitter
        {
            ResizeDirection = GridResizeDirection.Rows,
            Height = StageSplitterHeight,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Background = Brushes.Transparent,
        };
        Grid.SetRow(stageSplitter, 1);
        lists.Children.Add(stageSplitter);

        var arrowStrip = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        arrowStrip.Children.Add(_stageArrow);
        arrowStrip.Children.Add(_unstageArrow);
        Grid.SetRow(arrowStrip, 1);
        lists.Children.Add(arrowStrip);

        var root = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(_conflictStrip, Dock.Top);
        root.Children.Add(_conflictStrip);
        root.Children.Add(lists);
        Content = root;

        _status = ctx.State.Status;
        ctx.State.StatusChanged += OnStatusChanged;
        RenderLists();
    }

    // ── section scaffolding ──

    private static Border Section(TextBlock header, CheckBox selectAll, StackPanel rows, string bulkLabel, Action bulk,
        string? bulkClass = null, Control? extraAction = null)
    {
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto"), Margin = new Thickness(0, 0, 0, 2) };
        selectAll.VerticalAlignment = VerticalAlignment.Center;
        selectAll.Margin = new Thickness(2, 0, 6, 0);
        head.Children.Add(selectAll);
        Grid.SetColumn(header, 1);
        header.VerticalAlignment = VerticalAlignment.Center;
        head.Children.Add(header);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        if (extraAction is not null)
            actions.Children.Add(extraAction);

        // "Stage all" reads green-bordered, "Unstage all" red-bordered (Button.success /
        // Button.danger in App.axaml) so the bulk action's direction is legible at a glance.
        var bulkBtn = new Button { Content = bulkLabel, FontSize = HeaderRowFontSize, Padding = new Thickness(8, 2), VerticalAlignment = VerticalAlignment.Center };
        if (bulkClass is not null)
            bulkBtn.Classes.Add(bulkClass);
        else
        {
            bulkBtn.Background = Brushes.Transparent;
            bulkBtn.BorderThickness = default;
        }
        bulkBtn.Click += (_, _) => bulk();
        actions.Children.Add(bulkBtn);

        Grid.SetColumn(actions, 3);
        head.Children.Add(actions);

        var inner = new DockPanel { LastChildFill = true, Margin = new Thickness(8) };
        DockPanel.SetDock(head, Dock.Top);
        inner.Children.Add(head);
        inner.Children.Add(new ScrollViewer { Content = rows, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });

        return new Border
        {
            Child = inner,
            MinHeight = 64,
            BorderThickness = new Thickness(1),
        }.Dyn(Border.BorderBrushProperty, "ColorBorder");
    }

    private Button ArrowButton(string icon, string tintKey, string tip, Action onClick)
    {
        var glyph = IconGlyphPath(icon, 16, tintKey);
        var b = new Button
        {
            Content = glyph,
            Padding = new Thickness(6, 2),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(1),
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
        };
        b.Dyn(Button.BorderBrushProperty, "ColorBorder");
        ToolTip.SetTip(b, tip);
        b.Click += (_, e) => { e.Handled = true; onClick(); };
        return b;
    }

    private RepositoryStatus? _lastRenderedStatus;
    private readonly Dictionary<string, (Border Row, CheckBox CheckBox, FileChangeKind Kind)> _changesRowCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (Border Row, CheckBox CheckBox, FileChangeKind Kind)> _stagedRowCache = new(StringComparer.Ordinal);

    private void OnStatusChanged(object? s, StatusChangedEventArgs e)
        => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (_lastRenderedStatus is not null && StatusEquivalent(_lastRenderedStatus, e.Status))
                return;
            _status = e.Status;
            RenderLists();
        });

    private static bool StatusEquivalent(RepositoryStatus a, RepositoryStatus b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a.Unstaged.Count != b.Unstaged.Count || a.Untracked.Count != b.Untracked.Count
            || a.Staged.Count != b.Staged.Count || a.Conflicted.Count != b.Conflicted.Count)
            return false;

        for (int i = 0; i < a.Unstaged.Count; i++)
            if (a.Unstaged[i].Path != b.Unstaged[i].Path || a.Unstaged[i].Kind != b.Unstaged[i].Kind) return false;
        for (int i = 0; i < a.Untracked.Count; i++)
            if (a.Untracked[i].Path != b.Untracked[i].Path || a.Untracked[i].Kind != b.Untracked[i].Kind) return false;
        for (int i = 0; i < a.Staged.Count; i++)
            if (a.Staged[i].Path != b.Staged[i].Path || a.Staged[i].Kind != b.Staged[i].Kind) return false;
        for (int i = 0; i < a.Conflicted.Count; i++)
            if (a.Conflicted[i].Path != b.Conflicted[i].Path || a.Conflicted[i].Kind != b.Conflicted[i].Kind) return false;

        return true;
    }

    private void RenderLists()
    {
        var changes = _status.Unstaged.Concat(_status.Untracked).OrderBy(f => f.Path, StringComparer.Ordinal).ToList();
        var staged = _status.Staged.OrderBy(f => f.Path, StringComparer.Ordinal).ToList();

        // Drop checks for paths that no longer exist in the section.
        _checkedChanges.IntersectWith(changes.Select(f => f.Path));
        _checkedStaged.IntersectWith(staged.Select(f => f.Path));

        _changesHeader.Text = $"Changes ({changes.Count})";
        _stagedHeader.Text = $"Staged ({staged.Count})";

        FillRows(_changesRows, changes, _checkedChanges, staged: false, _changesRowCache);
        FillRows(_stagedRows, staged, _checkedStaged, staged: true, _stagedRowCache);

        _conflictStrip.IsVisible = _status.Conflicted.Count > 0;
        _conflictRows.Children.Clear();
        foreach (var f in _status.Conflicted)
        {
            var r = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Height = 22 };
            r.Children.Add(KindLabel(f.Kind));
            var p = Text(f.Path, "ColorText", 12);
            p.Margin = new Thickness(8, 0, 0, 0);
            Grid.SetColumn(p, 1);
            r.Children.Add(p);
            _conflictRows.Children.Add(r);
        }

        _lastRenderedStatus = _status;
        RefreshSelectionState();
    }

    private void FillRows(
        StackPanel host,
        IReadOnlyList<FileStatusEntry> files,
        HashSet<string> checkedSet,
        bool staged,
        Dictionary<string, (Border Row, CheckBox CheckBox, FileChangeKind Kind)> cache)
    {
        if (files.Count == 0)
        {
            host.Children.Clear();
            cache.Clear();
            host.Children.Add(Text(staged ? "Nothing staged." : "No changes.", "ColorTextFaint", 12));
            return;
        }

        var currentPaths = new HashSet<string>(files.Select(f => f.Path), StringComparer.Ordinal);
        var removed = cache.Keys.Where(k => !currentPaths.Contains(k)).ToList();
        foreach (var k in removed)
            cache.Remove(k);

        var newChildren = new List<Control>(files.Count);
        foreach (var f in files)
        {
            var file = f;
            if (cache.TryGetValue(file.Path, out var cached) && cached.Kind == file.Kind)
            {
                cached.CheckBox.IsChecked = checkedSet.Contains(file.Path);
                newChildren.Add(cached.Row);
                continue;
            }

            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto"), Height = 24 };

            var cb = new CheckBox
            {
                IsChecked = checkedSet.Contains(file.Path),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(2, 0, 6, 0),
            };
            cb.IsCheckedChanged += (_, _) =>
            {
                if (cb.IsChecked == true) checkedSet.Add(file.Path);
                else checkedSet.Remove(file.Path);
                RefreshSelectionState();
            };
            row.Children.Add(cb);

            var kind = KindLabel(file.Kind);
            Grid.SetColumn(kind, 1);
            row.Children.Add(kind);

            var path = Text(file.Path, "ColorText", 12);
            path.Margin = new Thickness(8, 0, 0, 0);
            path.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(path, 2);
            row.Children.Add(path);

            if (!staged)
            {
                var discard = new Button { Content = "↺", Padding = new Thickness(4, 0), Background = Brushes.Transparent, BorderThickness = default };
                ToolTip.SetTip(discard, "Discard");
                discard.Click += (_, e) => { e.Handled = true; _ = DiscardAsync(file); };
                Grid.SetColumn(discard, 3);
                row.Children.Add(discard);
            }

            var b = new Border { Child = row, Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand), Background = Brushes.Transparent };
            if (!_ctx.IsBare)
            {
                b.WithRevealMenu(_launcher, () => System.IO.Path.Combine(_ctx.RepositoryPath, file.Path),
                    extraItems: [MenuLeaf("Discard changes", () => _ = DiscardAsync(file))]);
            }
            b.PointerPressed += (_, e) =>
            {
                // e.Source is the leaf visual under the pointer — a Border/Path inside the
                // CheckBox template, not the CheckBox itself — so walk up before deciding.
                if (e.Source is Visual v && (v is CheckBox || v.FindAncestorOfType<CheckBox>() is not null))
                    return; // let the checkbox handle its own click
                _ctx.State.SetOpenDiff(new DiffRequest
                {
                    Source = staged ? DiffSource.StagedChanges : DiffSource.UnstagedChanges,
                    Path = file.Path,
                    ContextLines = _settings.Current.DiffContextLines,
                });
            };

            cache[file.Path] = (b, cb, file.Kind);
            newChildren.Add(b);
        }

        host.Children.Clear();
        foreach (var c in newChildren)
            host.Children.Add(c);
    }

    private static Control KindLabel(FileChangeKind k)
    {
        var (label, key) = k switch
        {
            FileChangeKind.Added or FileChangeKind.Untracked => ("New", "ColorSuccess"),
            FileChangeKind.Deleted => ("Deleted", "ColorDanger"),
            FileChangeKind.Modified => ("Modified", "ColorWarning"),
            FileChangeKind.Renamed => ("Renamed", "ColorAccent"),
            FileChangeKind.Copied => ("Copied", "ColorAccent"),
            FileChangeKind.TypeChanged => ("Type-changed", "ColorWarning"),
            FileChangeKind.Conflicted => ("Conflict", "ColorDanger"),
            _ => ("Changed", "ColorTextMuted"),
        };
        var t = Text(label, key, 11, FontWeight.SemiBold);
        t.Width = 84;
        t.VerticalAlignment = VerticalAlignment.Center;
        return t;
    }

    // ── selection + stage/unstage arrows ──

    private void RefreshSelectionState()
    {
        var changesCount = _status.Unstaged.Count + _status.Untracked.Count;
        var stagedCount = _status.Staged.Count;

        _stageArrow.IsEnabled = _checkedChanges.Count > 0;
        _unstageArrow.IsEnabled = _checkedStaged.Count > 0;
        _stashStaged.IsEnabled = !_stashing && stagedCount > 0;
        _discardAllChanges.IsEnabled = changesCount > 0;

        _suppressSelectAll = true;
        _selectAllChanges.IsChecked = TriState(_checkedChanges.Count, changesCount);
        _selectAllStaged.IsChecked = TriState(_checkedStaged.Count, stagedCount);
        _suppressSelectAll = false;
    }

    private static bool? TriState(int chosen, int total)
        => total == 0 || chosen == 0 ? false : chosen >= total ? true : null;

    private void ToggleSelectAll(bool staged)
    {
        if (_suppressSelectAll) return;
        var set = staged ? _checkedStaged : _checkedChanges;
        var all = staged
            ? _status.Staged.Select(f => f.Path)
            : _status.Unstaged.Concat(_status.Untracked).Select(f => f.Path);
        var want = (staged ? _selectAllStaged : _selectAllChanges).IsChecked == true;
        set.Clear();
        if (want)
            foreach (var p in all) set.Add(p);
        RenderLists();
    }

    private async Task MoveCheckedAsync(bool stage)
    {
        var paths = (stage ? _checkedChanges : _checkedStaged).ToList();
        if (paths.Count == 0) return;
        (stage ? _checkedChanges : _checkedStaged).Clear();
        await Run(() => stage
            ? _ctx.Gateway.StageAsync(paths, _ctx.Lifetime)
            : _ctx.Gateway.UnstageAsync(paths, _ctx.Lifetime));
    }

    private async Task DiscardAsync(FileStatusEntry f)
    {
        if (!await _dialogs.ConfirmAsync("Discard changes", $"Discard all changes to '{f.Path}'?", destructive: true, "Discard"))
            return;
        _checkedChanges.Remove(f.Path);
        _checkedStaged.Remove(f.Path);
        if (f.OldPath is not null)
        {
            _checkedChanges.Remove(f.OldPath);
            _checkedStaged.Remove(f.OldPath);
        }
        RefreshSelectionState();
        if (_ctx.State.OpenDiff?.Path == f.Path || (f.OldPath is not null && _ctx.State.OpenDiff?.Path == f.OldPath))
            _ctx.State.SetOpenDiff(null);

        var paths = f.OldPath is not null ? new[] { f.Path, f.OldPath } : new[] { f.Path };
        await Run(() => _ctx.Gateway.DiscardAsync(paths, _ctx.Lifetime));
    }

    private async Task DiscardAllChangesAsync()
    {
        var paths = _status.Unstaged.Concat(_status.Untracked).Select(f => f.Path).ToList();
        if (paths.Count == 0) return;
        if (!await _dialogs.ConfirmAsync(
            "Discard all changes",
            "Discard all unstaged changes? Untracked files are moved to the recycle bin; "
            + "changes to tracked files cannot be undone.",
            destructive: true,
            "Discard all"))
            return;
        _checkedChanges.Clear();
        RefreshSelectionState();
        if (_ctx.State.OpenDiff is { } diff && (diff.Source == DiffSource.UnstagedChanges || (diff.Path is not null && paths.Contains(diff.Path))))
            _ctx.State.SetOpenDiff(null);
        await Run(() => _ctx.Gateway.DiscardAsync(paths, _ctx.Lifetime));
    }

    private async Task BulkAsync(IReadOnlyList<FileStatusEntry> files, bool staged)
    {
        var paths = files.Select(f => f.Path).ToList();
        if (paths.Count == 0) return;
        await Run(() => staged ? _ctx.Gateway.UnstageAsync(paths, _ctx.Lifetime) : _ctx.Gateway.StageAsync(paths, _ctx.Lifetime));
    }

    private async Task Run(Func<Task> op)
    {
        try { await op(); await _ctx.State.RefreshStatusAsync(_ctx.Lifetime); }
        catch (Exception ex) { _notifications.Publish(NotificationSeverity.Error, ex.Message, ex.NotificationDetails()); }
    }

    // v8 M15: stash only the staged set; unstaged working-tree edits are left in place.
    private async Task StashStagedAsync()
    {
        if (_stashing || _status.Staged.Count == 0)
            return;
        _stashing = true;
        _stashStaged.IsEnabled = false;
        try
        {
            var stashed = await _stash.StashStagedAsync(_ctx, null, _ctx.Lifetime);
            _notifications.Publish(
                stashed ? NotificationSeverity.Success : NotificationSeverity.Info,
                stashed ? "Stashed the staged changes." : "Nothing staged to stash.");
        }
        catch (Exception ex)
        {
            _notifications.Publish(NotificationSeverity.Error, $"Stash staged failed: {ex.Message}", ex.NotificationDetails());
        }
        finally
        {
            _stashing = false;
            RefreshSelectionState();
        }
    }

    private async Task CommitAsync(bool push)
    {
        if (_committing || _status.Conflicted.Count > 0) return;
        if (string.IsNullOrWhiteSpace(_message.Text) || (_status.Staged.Count == 0 && _amend.IsChecked != true))
        {
            _notifications.Publish(NotificationSeverity.Warning, "Stage changes and enter a message before committing.");
            return;
        }
        _committing = true;
        try
        {
            var profile = _ctx.State.ActiveProfile;
            var name = profile?.GitUserName ?? "";
            var email = profile?.GitUserEmail ?? "";

            // §14.4: under Local config (no active profile) the identity comes from git config.
            if (profile is null && (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(email)))
            {
                var (cfgName, cfgEmail) = await _ctx.Gateway.GetConfiguredIdentityAsync(_ctx.Lifetime);
                if (string.IsNullOrWhiteSpace(cfgName) || string.IsNullOrWhiteSpace(cfgEmail))
                    (cfgName, cfgEmail) = await _ctx.Gateway.GetGlobalIdentityAsync(_ctx.Lifetime);
                name = cfgName ?? name;
                email = cfgEmail ?? email;
            }

            if (TryParseAuthor(_author.Text ?? "", out var oN, out var oE)) { name = oN; email = oE; }
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(email))
            {
                _notifications.Publish(NotificationSeverity.Warning,
                    "No author identity. Set user.name / user.email in Git config, choose a profile, or set an author override.");
                return;
            }

            // §14.4: with no profile, honour whatever git config says about signing.
            var sign = profile?.SignCommits ?? await _ctx.Gateway.GetGpgSignConfiguredAsync(_ctx.Lifetime);

            await _ctx.Gateway.CommitAsync(new CommitRequest
            {
                Message = _message.Text!.Trim(),
                AuthorName = name, AuthorEmail = email,
                CommitterName = profile?.GitUserName ?? name, CommitterEmail = profile?.GitUserEmail ?? email,
                AmendPreviousCommit = _amend.IsChecked == true,
                Sign = sign,
            }, _ctx.Lifetime);
            _message.Text = "";
            _amend.IsChecked = false;
            DraftByRepo.Remove(_ctx.Id);
            await _ctx.State.RefreshAllAsync(_ctx.Lifetime);
            _notifications.Publish(NotificationSeverity.Success, "Commit created.");

            if (push)
            {
                var remote = _ctx.State.Remotes.FirstOrDefault(r => r.Name == "origin")?.Name ?? _ctx.State.Remotes.FirstOrDefault()?.Name;
                var branch = _ctx.State.CurrentBranch?.Name;
                if (remote is not null && branch is not null)
                {
                    await _ctx.Gateway.PushAsync(remote, new PushSpec { RefSpec = branch, SetUpstream = _ctx.State.CurrentBranch?.UpstreamName is null }, null, _ctx.Lifetime);
                    await _ctx.State.RefreshAllAsync(_ctx.Lifetime);
                    _notifications.Publish(NotificationSeverity.Success, $"Pushed to {remote}.");
                }
            }
        }
        catch (Exception ex)
        {
            _notifications.Publish(NotificationSeverity.Error, $"Commit failed: {ex.Message}", ex.NotificationDetails());
        }
        finally { _committing = false; }
    }

    private void UpdateCounter()
    {
        var len = (_message.Text ?? "").Length;
        string secondPart;
        string colorKey;

        if (len < 72)
        {
            secondPart = "72";
            colorKey = "ColorTextMuted";
        }
        else if (len == 72)
        {
            secondPart = "72";
            colorKey = "ColorWarning";
        }
        else if (len < 500)
        {
            secondPart = "500";
            colorKey = "ColorWarning";
        }
        else
        {
            secondPart = "-";
            colorKey = "ColorDanger";
        }

        _counter.Text = $"{len} / {secondPart}";
        _counter.Dyn(TextBlock.ForegroundProperty, colorKey);
    }

    private void AddCoAuthor(string name, string email)
    {
        var trailer = $"Co-authored-by: {name} <{email}>";
        var msg = _message.Text ?? "";
        if (msg.Contains(trailer, StringComparison.OrdinalIgnoreCase))
            return;
        var sep = msg.Length == 0 || msg.EndsWith('\n') ? "" : "\n";
        if (!msg.Contains("Co-authored-by:", StringComparison.OrdinalIgnoreCase) && msg.Length > 0)
            sep += "\n";
        _message.Text = msg + sep + trailer + "\n";
    }

    private static bool TryParseAuthor(string text, out string name, out string email)
    {
        name = ""; email = "";
        if (string.IsNullOrWhiteSpace(text)) return false;
        var lt = text.IndexOf('<');
        var gt = text.IndexOf('>');
        if (lt < 0 || gt < lt) return false;
        name = text[..lt].Trim();
        email = text[(lt + 1)..gt].Trim();
        return name.Length > 0 && email.Length > 0;
    }

    public void Dispose()
    {
        _ctx.State.StatusChanged -= OnStatusChanged;
        _commitShortcut?.Dispose();
        var msg = _message.Text ?? "";
        if (msg.Length > 0) DraftByRepo[_ctx.Id] = msg;
        else DraftByRepo.Remove(_ctx.Id);
    }
}
