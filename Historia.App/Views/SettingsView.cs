namespace Historia.Views;

/// <summary>Application settings (V4 port of Settings.razor).</summary>
public sealed class SettingsView : UserControl, IDisposable
{
    private readonly ISettingsService _cfg;
    private readonly IThemeService _theme;
    private readonly ICustomThemeService _customThemes;
    private readonly Historia.Services.UI.INavigationBus _nav;
    private readonly IDialogService _dialogs;
    private readonly IGraphLayoutService _graph;
    private readonly Historia.Services.Storage.IRepoStateCache _repoCache;
    private readonly StackPanel _root = new() { Spacing = 18, Margin = new Thickness(32), MaxWidth = 640 };

    // Survives the full Rebuild() that fires on every settings change.
    private bool _advExpanded;
    private bool _terminalExpanded;

    public SettingsView(IServiceProvider sp)
    {
        _cfg = sp.GetRequiredService<ISettingsService>();
        _theme = sp.GetRequiredService<IThemeService>();
        _customThemes = sp.GetRequiredService<ICustomThemeService>();
        _nav = sp.GetRequiredService<Historia.Services.UI.INavigationBus>();
        _dialogs = sp.GetRequiredService<IDialogService>();
        _graph = sp.GetRequiredService<IGraphLayoutService>();
        _repoCache = sp.GetRequiredService<Historia.Services.Storage.IRepoStateCache>();

        _onCfg = (_, _) => Post();
        _onTheme = (_, _) => Post();

        Content = ClosablePane(new ScrollViewer { Content = _root }, sp);
        this.Dyn(BackgroundProperty, "ColorChrome");
        _cfg.Changed += _onCfg;
        _theme.ThemeChanged += _onTheme;
        Rebuild();
    }

    private void Post() => Avalonia.Threading.Dispatcher.UIThread.Post(Rebuild);
    private readonly EventHandler<SettingsChangedEventArgs> _onCfg;
    private readonly EventHandler<ThemeChangedEventArgs> _onTheme;

    private void Rebuild()
    {
        var c = _cfg.Current;
        _root.Children.Clear();
        _root.Children.Add(Text("Settings", "ColorText", 24, FontWeight.SemiBold));

        // ── Appearance ──
        _root.Children.Add(Section("Appearance"));
        var themeRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var m in new[] { ThemeMode.Light, ThemeMode.Dark, ThemeMode.System })
        {
            var mode = m;
            themeRow.Children.Add(TextButton(m.ToString(), () => _ = _theme.ApplyModeAsync(mode), primary: _theme.CurrentMode == m));
        }

        // §V6-G9: a 4th "Custom" option. With ≥ 1 saved theme it activates the last-selected
        // (or first) one; with none it routes straight to the Themes panel.
        var savedThemes = _customThemes.List();
        themeRow.Children.Add(TextButton("Custom", () =>
        {
            if (savedThemes.Count == 0)
            {
                _nav.RequestNavigate("themes");
                return;
            }
            var stored = _cfg.Current.CustomThemeId;
            var pick = savedThemes.FirstOrDefault(t => t.Id == stored)?.Id ?? savedThemes[0].Id;
            _ = _theme.ApplyCustomThemeAsync(pick);
        }, primary: _theme.CurrentMode == ThemeMode.Custom));
        _root.Children.Add(themeRow);

        if (_theme.CurrentMode == ThemeMode.Custom)
        {
            var customRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            if (savedThemes.Count > 0)
            {
                var names = savedThemes.Select(t => t.Name).ToList();
                var combo = new ComboBox { Width = 220, ItemsSource = names };
                var activeIdx = savedThemes.ToList().FindIndex(t => t.Id == _cfg.Current.CustomThemeId);
                combo.SelectedIndex = activeIdx >= 0 ? activeIdx : 0;
                combo.SelectionChanged += (_, _) =>
                {
                    var i = combo.SelectedIndex;
                    if (i >= 0 && i < savedThemes.Count && savedThemes[i].Id != _cfg.Current.CustomThemeId)
                        _ = _theme.ApplyCustomThemeAsync(savedThemes[i].Id);
                };
                customRow.Children.Add(combo);
            }
            customRow.Children.Add(TextButton("Manage themes", () => _nav.RequestNavigate("themes")));
            _root.Children.Add(customRow);
        }
        _root.Children.Add(Check("High Contrast mode", _theme.HighContrastEnabled, v => _ = _theme.SetHighContrastAsync(v)));
        _root.Children.Add(Check("Multi-line repository tabs", c.EnableMultiLineTabs,
            v => _ = _cfg.UpdateAsync(s => s with { EnableMultiLineTabs = v })));
        if (c.EnableMultiLineTabs)
        {
            var maxRows = new NumericUpDown { Minimum = 2, Maximum = 5, Value = c.MaxTabRows, FormatString = "0", Width = 120, HorizontalAlignment = HorizontalAlignment.Left };
            maxRows.ValueChanged += (_, e) => _ = _cfg.UpdateAsync(s => s with { MaxTabRows = (int)Math.Clamp(e.NewValue ?? 3, 2, 5) });
            _root.Children.Add(LabeledRow("Max tab rows", maxRows));
        }
        _root.Children.Add(Check("Fixed-width repository tabs", c.TabWidthMode == TabWidthMode.Fixed,
            v => _ = _cfg.UpdateAsync(s => s with { TabWidthMode = v ? TabWidthMode.Fixed : TabWidthMode.Dynamic })));
        if (c.TabWidthMode == TabWidthMode.Fixed)
        {
            var tabLen = new NumericUpDown
            {
                Minimum = AppSettings.MinFixedTabWidth,
                Maximum = AppSettings.MaxFixedTabWidth,
                Increment = 4,
                Value = c.FixedTabWidth,
                FormatString = "0",
                Width = 120,
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            tabLen.ValueChanged += (_, e) => _ = _cfg.UpdateAsync(s => s with
            {
                FixedTabWidth = AppSettings.ClampFixedTabWidth((int)(e.NewValue ?? AppSettings.DefaultFixedTabWidth)),
            });
            _root.Children.Add(LabeledRow("Tab width (px)", tabLen));
        }
        else
        {
            _root.Children.Add(Muted("Tabs shrink to fit short repository names and grow up to ~25 characters for longer ones."));
        }
        _root.Children.Add(Muted("Graph-panel columns are configured per repository — the wrench on a repo tab."));

        // v8 M9: mouse-wheel / trackpad scroll direction for the graph and the tab strip.
        var wheelDir = new ComboBox { Width = 220 };
        wheelDir.Items.Add(new ComboBoxItem { Content = "Match system (natural / inverted)", Tag = WheelScrollDirection.Auto });
        wheelDir.Items.Add(new ComboBoxItem { Content = "Standard", Tag = WheelScrollDirection.Standard });
        wheelDir.Items.Add(new ComboBoxItem { Content = "Inverted", Tag = WheelScrollDirection.Inverted });
        wheelDir.SelectedIndex = c.WheelScrollDirection switch
        {
            WheelScrollDirection.Standard => 1,
            WheelScrollDirection.Inverted => 2,
            _ => 0,
        };
        wheelDir.SelectionChanged += (_, _) =>
        {
            if ((wheelDir.SelectedItem as ComboBoxItem)?.Tag is WheelScrollDirection wd)
                _ = _cfg.UpdateAsync(s => s with { WheelScrollDirection = wd });
        };
        _root.Children.Add(LabeledRow("Scroll direction", wheelDir));
        _root.Children.Add(Muted("Windows always scrolls standard. On macOS and Linux, \"Match system\" follows the OS natural-scroll setting."));
        _root.Children.Add(WithHint(Check("Store a local repository cache", c.PersistRepoCache,
            v =>
            {
                _ = _cfg.UpdateAsync(s => s with { PersistRepoCache = v });
                if (!v)
                    _repoCache.WipeAll(); // turning it off also clears what's on disk
            }),
            "Caches this repository's local branches, remotes, tags, stashes, submodules and worktrees so the sidebar appears instantly on the next launch. The real data is still read from Git and replaces it."));

        // ── Privacy & Network ──
        _root.Children.Add(Section("Privacy & Network"));
        _root.Children.Add(Muted("Network routing is per repository — pick an identity, and toggle \"SSH only\", in the shortcuts bar."));
        _root.Children.Add(Check("Allow background API polling", c.EnableBackgroundPolling,
            v => _ = _cfg.UpdateAsync(s => s with { EnableBackgroundPolling = v })));
        _root.Children.Add(Muted("Effective only while a repository's selected profile has a connected token."));
        _root.Children.Add(Check("Automatically fetch open repositories", c.AutoFetchEnabled,
            v => _ = _cfg.UpdateAsync(s => s with { AutoFetchEnabled = v })));
        _root.Children.Add(Muted("Periodically runs “git fetch” on every open repository (about every 5 minutes plus 1 minute per open tab)."));

        // ── Diff & Editor ──
        _root.Children.Add(Section("Diff & Editor"));
        var diffView = new ComboBox { Width = 160 };
        diffView.Items.Add(new ComboBoxItem { Content = "Unified", Tag = DiffViewMode.Unified });
        diffView.Items.Add(new ComboBoxItem { Content = "Side-by-Side", Tag = DiffViewMode.SideBySide });
        diffView.SelectedIndex = c.DefaultDiffView == DiffViewMode.SideBySide ? 1 : 0;
        diffView.SelectionChanged += (_, _) =>
        {
            if ((diffView.SelectedItem as ComboBoxItem)?.Tag is DiffViewMode dv)
                _ = _cfg.UpdateAsync(s => s with { DefaultDiffView = dv });
        };
        _root.Children.Add(LabeledRow("Default diff view", diffView));
        var ctxLines = new NumericUpDown { Minimum = 0, Maximum = 20, Value = c.DiffContextLines, FormatString = "0", Width = 120, HorizontalAlignment = HorizontalAlignment.Left };
        ctxLines.ValueChanged += (_, e) => _ = _cfg.UpdateAsync(s => s with { DiffContextLines = (int)(e.NewValue ?? 3) });
        _root.Children.Add(LabeledRow("Context lines", ctxLines));
        var tabW = new NumericUpDown { Minimum = 1, Maximum = 8, Value = c.TabWidth, FormatString = "0", Width = 120, HorizontalAlignment = HorizontalAlignment.Left };
        tabW.ValueChanged += (_, e) => _ = _cfg.UpdateAsync(s => s with { TabWidth = (int)(e.NewValue ?? 4) });
        _root.Children.Add(LabeledRow("Tab width", tabW));
        _root.Children.Add(Check("Word-wrap diff lines", c.DiffWordWrap,
            v => _ = _cfg.UpdateAsync(s => s with { DiffWordWrap = v })));
        var commitFont = new NumericUpDown { Minimum = 10, Maximum = 24, Value = c.CommitDetailFontSize, FormatString = "0", Width = 120, HorizontalAlignment = HorizontalAlignment.Left };
        commitFont.ValueChanged += (_, e) => _ = _cfg.UpdateAsync(s => s with { CommitDetailFontSize = (int)(e.NewValue ?? 14) });
        _root.Children.Add(LabeledRow("Commit-detail font size", commitFont));

        // ── Issue autolinks ──
        _root.Children.Add(Section("Issue autolinks"));
        _root.Children.Add(Muted("Turn issue keys in commit messages into links. $1, $2… are the regex capture groups."));
        for (var i = 0; i < c.IssueAutolinks.Count; i++)
        {
            var idx = i;
            var rule = c.IssueAutolinks[idx];
            var pat = new TextBox { Text = rule.Pattern, Watermark = "pattern (regex)", Width = 200 };
            var url = new TextBox { Text = rule.UrlTemplate, Watermark = "URL template", Width = 300 };
            pat.LostFocus += (_, _) => _ = _cfg.UpdateAsync(s => s with { IssueAutolinks = [.. s.IssueAutolinks.Select((r, j) => j == idx ? r with { Pattern = pat.Text ?? "" } : r)] });
            url.LostFocus += (_, _) => _ = _cfg.UpdateAsync(s => s with { IssueAutolinks = [.. s.IssueAutolinks.Select((r, j) => j == idx ? r with { UrlTemplate = url.Text ?? "" } : r)] });
            var rm = TextButton("Remove", () => _ = _cfg.UpdateAsync(s => s with { IssueAutolinks = [.. s.IssueAutolinks.Where((_, j) => j != idx)] }));
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            row.Children.Add(pat); row.Children.Add(url); row.Children.Add(rm);
            _root.Children.Add(row);
        }
        _root.Children.Add(TextButton("Add rule", () => _ = _cfg.UpdateAsync(s => s with { IssueAutolinks = [.. s.IssueAutolinks, new IssueAutolinkRule { Pattern = "", UrlTemplate = "" }] })));

        // ── Git ──
        _root.Children.Add(Section("Git"));
        _root.Children.Add(Check("Prune deleted remote branches on fetch", c.PruneOnFetch, v => _ = _cfg.UpdateAsync(s => s with { PruneOnFetch = v })));
        _root.Children.Add(Check("Pull with rebase by default", c.PullWithRebase, v => _ = _cfg.UpdateAsync(s => s with { PullWithRebase = v })));
        _root.Children.Add(Check("Reopen previous repository tabs on launch", c.RestoreSessionTabs, v => _ = _cfg.UpdateAsync(s => s with { RestoreSessionTabs = v })));
        _root.Children.Add(WithHint(Check("Use custom stash names", c.UseCustomStashNames,
            v => _ = _cfg.UpdateAsync(s => s with { UseCustomStashNames = v })),
            "When on, the \"Stash all\" buttons ask you for a message. When off, they name the stash \"Stash of <branch>\"."));

        // ── Terminal (V5 §13) — collapsed ──
        var termBody = new StackPanel { Spacing = 8, Margin = new Thickness(0, 10, 0, 0) };

        var shellPath = new TextBox { Text = c.TerminalShellPath ?? "", Watermark = "Platform default (pwsh / zsh / bash)", Width = 360, HorizontalAlignment = HorizontalAlignment.Left };
        shellPath.LostFocus += (_, _) => _ = _cfg.UpdateAsync(s => s with { TerminalShellPath = string.IsNullOrWhiteSpace(shellPath.Text) ? null : shellPath.Text.Trim() });
        var browse = TextButton("Browse…", async () =>
        {
            var picked = await _dialogs.PickFileAsync("Terminal shell", "Executable|*.exe;*|All files|*");
            if (!string.IsNullOrWhiteSpace(picked))
            {
                shellPath.Text = picked;
                await _cfg.UpdateAsync(s => s with { TerminalShellPath = picked });
            }
        });
        var shellRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        shellRow.Children.Add(Text("Shell path", "ColorText", 13));
        shellRow.Children.Add(shellPath);
        shellRow.Children.Add(browse);
        termBody.Children.Add(shellRow);

        var shellArgs = new TextBox { Text = string.Join(' ', c.TerminalShellArgs), Watermark = "(space separated)", Width = 360, HorizontalAlignment = HorizontalAlignment.Left };
        shellArgs.LostFocus += (_, _) => _ = _cfg.UpdateAsync(s => s with
        {
            TerminalShellArgs = shellArgs.Text?.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [],
        });
        termBody.Children.Add(LabeledRow("Shell arguments", shellArgs));

        var fontFamily = new TextBox { Text = c.TerminalFontFamily ?? "", Watermark = OperatingSystem.IsWindows() ? "Cascadia Mono, Consolas" : "platform monospace", Width = 280, HorizontalAlignment = HorizontalAlignment.Left };
        fontFamily.LostFocus += (_, _) => _ = _cfg.UpdateAsync(s => s with { TerminalFontFamily = string.IsNullOrWhiteSpace(fontFamily.Text) ? null : fontFamily.Text.Trim() });
        termBody.Children.Add(LabeledRow("Font family", fontFamily));

        var fontSize = new NumericUpDown { Minimum = 8, Maximum = 32, Value = c.TerminalFontSize, FormatString = "0", Width = 120, HorizontalAlignment = HorizontalAlignment.Left };
        fontSize.ValueChanged += (_, _) => _ = _cfg.UpdateAsync(s => s with { TerminalFontSize = (int)(fontSize.Value ?? 13) });
        termBody.Children.Add(LabeledRow("Font size", fontSize));

        var scrollback = new NumericUpDown
        {
            Minimum = AppSettings.MinTerminalScrollback,
            Maximum = AppSettings.MaxTerminalScrollback,
            Increment = 500,
            Value = c.TerminalScrollbackLines,
            FormatString = "0",
            Width = 140,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        scrollback.ValueChanged += (_, _) => _ = _cfg.UpdateAsync(s => s with
        {
            TerminalScrollbackLines = AppSettings.ClampTerminalScrollback((int)(scrollback.Value ?? 5000)),
        });
        termBody.Children.Add(WithHint(LabeledRow("Scrollback lines", scrollback),
            "Memory ≈ lines × columns × ~8 bytes, per session — a 5,000-line buffer on a 120-column grid is ~5 MB, and this multiplies by the number of open terminal sessions."));

        var cursorStyle = new ComboBox { Width = 160 };
        foreach (var st in Enum.GetValues<TerminalCursorStyle>())
            cursorStyle.Items.Add(new ComboBoxItem { Content = st.ToString(), Tag = st });
        cursorStyle.SelectedIndex = (int)c.TerminalCursorStyle;
        cursorStyle.SelectionChanged += (_, _) =>
        {
            if ((cursorStyle.SelectedItem as ComboBoxItem)?.Tag is TerminalCursorStyle st)
                _ = _cfg.UpdateAsync(s => s with { TerminalCursorStyle = st });
        };
        termBody.Children.Add(LabeledRow("Cursor style", cursorStyle));
        termBody.Children.Add(Check("Blink the cursor", c.TerminalCursorBlink, v => _ = _cfg.UpdateAsync(s => s with { TerminalCursorBlink = v })));

        var bell = new ComboBox { Width = 160 };
        foreach (var bs in Enum.GetValues<TerminalBellStyle>())
            bell.Items.Add(new ComboBoxItem { Content = bs.ToString(), Tag = bs });
        bell.SelectedIndex = (int)c.TerminalBellStyle;
        bell.SelectionChanged += (_, _) =>
        {
            if ((bell.SelectedItem as ComboBoxItem)?.Tag is TerminalBellStyle bs)
                _ = _cfg.UpdateAsync(s => s with { TerminalBellStyle = bs });
        };
        termBody.Children.Add(LabeledRow("Bell", bell));

        AddCollapsibleSection("Terminal", termBody, () => _terminalExpanded, v => _terminalExpanded = v);

        // Profiles & Accounts moved to its own page (route "profiles"), opened from the app
        // menu's Profile ▸ Manage profiles… entry — see ProfilesView.

        // ── Advanced (collapsed) ──
        var advBody = new StackPanel { Spacing = 8 };

        advBody.Children.Add(Check("Show the splash screen on launch", c.ShowSplashScreen,
            v => _ = _cfg.UpdateAsync(s => s with { ShowSplashScreen = v })));

        advBody.Children.Add(WithHint(Check("Read graph metadata from disk instead of memory", c.GraphMetadataFromDisk,
            v => _ = _cfg.UpdateAsync(s => s with { GraphMetadataFromDisk = v })),
            "Pages commit metadata (messages, author, dates) from the local cache folder to minimize memory usage on large repositories. Applies after restart of Historia."));

        advBody.Children.Add(WithHint(Check("Write a commit-graph file for large repositories", c.WriteCommitGraphForLargeRepos,
            v => _ = _cfg.UpdateAsync(s => s with { WriteCommitGraphForLargeRepos = v })),
            "Once a large repository's full history has loaded, writes a commit-graph file if it doesn't already have one. Lets Git's own history walk skip straight to recent commits on the next open instead of scanning the whole history."));

        var silentCrawlCap = new NumericUpDown
        {
            Minimum = AppSettings.MinSilentCrawlRowCap,
            Maximum = AppSettings.MaxSilentCrawlRowCap,
            Increment = 10_000,
            Value = c.SilentCrawlRowCap,
            FormatString = "0",
            Width = 140,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        silentCrawlCap.ValueChanged += (_, _) => _ = _cfg.UpdateAsync(s => s with
        {
            SilentCrawlRowCap = AppSettings.ClampSilentCrawlRowCap((int)(silentCrawlCap.Value ?? 100_000)),
        });
        advBody.Children.Add(WithHint(LabeledRow("Background auto-load row cap", silentCrawlCap),
            "How many rows the graph silently pre-loads in the background after first paint, before requiring you to scroll for more. 0 means uncapped. Applies after restart of Historia."));
        advBody.Margin = new Thickness(0, 10, 0, 0);

        AddCollapsibleSection("Advanced settings", advBody, () => _advExpanded, v => _advExpanded = v);
    }

    /// <summary>
    /// A section header that toggles <paramref name="body"/>'s visibility on click: left title,
    /// right chevron, a rule underneath — the whole row is the hit target, no box chrome.
    /// </summary>
    private void AddCollapsibleSection(string title, Control body, Func<bool> isExpanded, Action<bool> setExpanded)
    {
        body.IsVisible = isExpanded();

        var arrow = Text(isExpanded() ? "▼" : "▶", "ColorTextMuted", 13);
        arrow.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(arrow, 1);

        var titleText = Text(title, "ColorText", 13, FontWeight.SemiBold);
        titleText.VerticalAlignment = VerticalAlignment.Center;

        var headerGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        headerGrid.Children.Add(titleText);
        headerGrid.Children.Add(arrow);

        var header = new Border
        {
            Child = headerGrid,
            Padding = new Thickness(0, 8, 0, 8),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Background = Brushes.Transparent, // make the whole row hit-testable
            Cursor = new Cursor(StandardCursorType.Hand),
            Margin = new Thickness(0, 8, 0, 0),
        };
        header.Dyn(Border.BorderBrushProperty, "ColorBorder");
        header.PointerPressed += (_, _) =>
        {
            var next = !isExpanded();
            setExpanded(next);
            body.IsVisible = next;
            arrow.Text = next ? "▼" : "▶";
        };

        _root.Children.Add(header);
        _root.Children.Add(body);
    }

    private static Control Section(string title) => Text(title, "ColorTextMuted", 13, FontWeight.SemiBold);
    private static Control Muted(string text) { var t = Text(text, "ColorTextMuted", 11); t.TextWrapping = TextWrapping.Wrap; return t; }

    private static Control Check(string label, bool value, Action<bool> onChange, bool enabled = true)
    {
        var cb = new CheckBox { Content = label, IsChecked = value, IsEnabled = enabled };
        cb.IsCheckedChanged += (_, _) => onChange(cb.IsChecked == true);
        return cb;
    }

    private static Control LabeledRow(string label, Control control)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        row.Children.Add(Text(label, "ColorText", 13));
        row.Children.Add(control);
        return row;
    }

    public void Dispose()
    {
        _cfg.Changed -= _onCfg;
        _theme.ThemeChanged -= _onTheme;
    }
}

/// <summary>Per-repository settings (V4 port of RepoSettings.razor + GraphColumnSettings.razor).</summary>
public sealed class RepoSettingsView : UserControl, IDisposable
{
    private readonly IWorkspaceService _workspace;
    private readonly Historia.Services.Settings.IRepoLayoutService _repoLayout;
    private readonly StackPanel _root = new() { Spacing = 12, Margin = new Thickness(32), MaxWidth = 560 };
    private readonly Panel _overlay = new() { IsHitTestVisible = false };

    private readonly EventHandler<Historia.Services.Settings.RepoLayoutChangedEventArgs> _onLayout;

    // ── column-row drag reorder (mirrors the app-band tab reorder: live preview + ghost) ──
    private const double DragThreshold = 4;
    private readonly List<Border> _columnRows = [];
    private int _columnsBaseIndex;
    private string _dragRepoPath = "";
    private Border? _dragRow;
    private bool _dragging;
    private Point _pressPos;
    private Point _grabOffset;
    private Historia.Controls.AppBandDragGhost? _ghost;

    public RepoSettingsView(IServiceProvider sp)
    {
        _workspace = sp.GetRequiredService<IWorkspaceService>();
        _repoLayout = sp.GetRequiredService<Historia.Services.Settings.IRepoLayoutService>();
        _onLayout = (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(Rebuild);
        Content = new Panel { Children = { new ScrollViewer { Content = _root }, _overlay } };
        this.Dyn(BackgroundProperty, "ColorChrome");
        _root.AddHandler(PointerMovedEvent, OnRowsPointerMoved, RoutingStrategies.Bubble);
        _root.AddHandler(PointerReleasedEvent, OnRowsPointerReleased, RoutingStrategies.Bubble);
        _root.PointerCaptureLost += OnRowsCaptureLost;
        _workspace.FocusedRepositoryChanged += OnFocusChanged;
        _repoLayout.Changed += _onLayout;
        Rebuild();
    }

    private void OnFocusChanged(object? s, FocusedRepositoryChangedEventArgs e)
        => Avalonia.Threading.Dispatcher.UIThread.Post(Rebuild);

    private void Rebuild()
    {
        _root.Children.Clear();
        var ctx = _workspace.Focused;

        _root.Children.Add(Text(ctx?.DisplayName ?? "Repository settings", "ColorText", 22, FontWeight.SemiBold));
        if (ctx is not null)
            _root.Children.Add(Text(ctx.RepositoryPath, "ColorTextMuted", 12));

        _root.Children.Add(Text("Graph columns", "ColorTextMuted", 13, FontWeight.SemiBold));
        var hint = Text("Order and visibility apply to every repository. Widths are per repository — drag the column headers — and reset on every launch.", "ColorTextMuted", 11);
        hint.TextWrapping = TextWrapping.Wrap;
        _root.Children.Add(hint);

        // §V3.1: the editor works on the application-wide column set and shows even with no repo.
        var columns = _repoLayout.GlobalColumns;
        var path = ctx?.RepositoryPath ?? "";
        _dragRepoPath = path;
        _columnRows.Clear();
        _columnsBaseIndex = _root.Children.Count;

        foreach (var column in columns)
        {
            var col = column;
            var kind = col.Kind;

            var rowGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Height = 30 };
            var rowBorder = new Border { Child = rowGrid, Background = Brushes.Transparent, Tag = kind };
            _columnRows.Add(rowBorder);

            var handle = new Border
            {
                Child = Text("☰", "ColorTextMuted", 15),
                Padding = new Thickness(6, 0),
                Background = Brushes.Transparent,
                Cursor = new Cursor(StandardCursorType.SizeAll),
                VerticalAlignment = VerticalAlignment.Center,
            };
            handle.PointerPressed += (_, e) => OnHandlePressed(rowBorder, e);
            rowGrid.Children.Add(handle);

            var cb = new CheckBox { Content = kind.ToString(), IsChecked = col.Visible, Margin = new Thickness(6, 0, 0, 0) };
            cb.IsCheckedChanged += (_, _) =>
            {
                var vis = cb.IsChecked == true;
                var next = _repoLayout.GlobalColumns.Select(c => c.Kind == kind ? c with { Visible = vis } : c).ToList();
                if (next.Count(c => c.Visible) == 0 || next.Count(c => c.Visible && c.IsFlex) != 1)
                    return; // never zero visible / non-unique flex
                _ = _repoLayout.UpdateGlobalAsync(_ => GraphColumnDefaults.Normalize(next));
            };
            Grid.SetColumn(cb, 1);
            rowGrid.Children.Add(cb);

            _root.Children.Add(rowBorder);
        }

        _root.Children.Add(TextButton("Reset to defaults", () =>
        {
            _ = _repoLayout.UpdateGlobalAsync(_ => GraphColumnDefaults.All);
            if (path.Length > 0)
                _ = _repoLayout.UpdateAsync(path, l => l with
                {
                    Columns = GraphColumnDefaults.All,
                    GraphWidthPinned = false,
                });
        }));
    }

    // ── column-row drag reorder ─────────────────────────────────────────────────────────
    private void OnHandlePressed(Border row, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        _dragRow = row;
        _pressPos = e.GetPosition(this);
        _grabOffset = e.GetPosition(row);
        _dragging = false;
        e.Handled = true;
    }

    private void OnRowsPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragRow is null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        var here = e.GetPosition(this);
        if (!_dragging)
        {
            if (Math.Abs(here.Y - _pressPos.Y) < DragThreshold)
                return;
            _dragging = true;
            e.Pointer.Capture(_root);
            StartGhost(_dragRow);
            _dragRow.Opacity = 0.3;
        }

        ReorderTo(e.GetPosition(_root).Y);
        _ghost?.MoveTo(new Point(here.X - _grabOffset.X, here.Y - _grabOffset.Y));
    }

    private void OnRowsPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragRow is null)
            return;
        var wasDragging = _dragging;
        var dropped = _dragRow;
        _dragRow = null;
        _dragging = false;
        StopGhost();
        dropped.Opacity = 1;
        e.Pointer.Capture(null);

        if (!wasDragging)
            return;
        e.Handled = true;

        var order = _columnRows.Select(r => (GraphColumnKind)r.Tag!).ToList();
        var byKind = _repoLayout.GlobalColumns.ToDictionary(c => c.Kind);
        var reordered = order.Where(byKind.ContainsKey).Select(k => byKind[k]).ToList();
        _ = _repoLayout.UpdateGlobalAsync(_ => GraphColumnDefaults.Normalize(reordered));
    }

    private void OnRowsCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (_dragRow is not null)
            _dragRow.Opacity = 1;
        _dragRow = null;
        _dragging = false;
        StopGhost();
    }

    /// <summary>Live preview: shuffle the row currently under the cursor into place, like the
    /// app-band tab reorder.</summary>
    private void ReorderTo(double y)
    {
        if (_dragRow is null)
            return;
        var cur = _columnRows.IndexOf(_dragRow);
        if (cur < 0)
            return;

        var target = 0;
        for (var i = 0; i < _columnRows.Count; i++)
        {
            var b = _columnRows[i].Bounds;
            if (y < b.Y + b.Height / 2)
                break;
            target = i + 1;
        }
        target = Math.Clamp(target, 0, _columnRows.Count - 1);
        if (target == cur)
            return;

        _columnRows.RemoveAt(cur);
        _columnRows.Insert(target, _dragRow);

        foreach (var r in _columnRows)
            _root.Children.Remove(r);
        for (var i = 0; i < _columnRows.Count; i++)
            _root.Children.Insert(_columnsBaseIndex + i, _columnRows[i]);
    }

    private void StartGhost(Border row)
    {
        if (row.Bounds.Width <= 0 || row.Bounds.Height <= 0)
            return;
        var px = new PixelSize(
            Math.Max(1, (int)Math.Ceiling(row.Bounds.Width)),
            Math.Max(1, (int)Math.Ceiling(row.Bounds.Height)));
        var rtb = new RenderTargetBitmap(px);
        rtb.Render(row);
        _ghost = new Historia.Controls.AppBandDragGhost(rtb, row.Bounds.Size);
        _overlay.Children.Add(_ghost);
    }

    private void StopGhost()
    {
        if (_ghost is not null)
            _overlay.Children.Remove(_ghost);
        _ghost = null;
    }

    public void Dispose()
    {
        _workspace.FocusedRepositoryChanged -= OnFocusChanged;
        _repoLayout.Changed -= _onLayout;
    }
}
