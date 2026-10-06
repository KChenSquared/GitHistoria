namespace Historia.Views;

/// <summary>Application settings.</summary>
public sealed class SettingsView : UserControl, IDisposable
{
    private readonly ISettingsService _cfg;
    private readonly IThemeService _theme;
    private readonly IDialogService _dialogs;
    private readonly IGraphLayoutService _graph;
    private readonly Historia.Services.Storage.IRepoStateCache _repoCache;
    private readonly StackPanel _root = new() { Spacing = 18, Margin = new Thickness(32), MaxWidth = 640 };

    // Survives the full Rebuild() that fires on every settings change.
    private bool _appearanceExpanded;
    private bool _networkExpanded;
    private bool _diffExpanded;
    private bool _gitExpanded;
    private bool _advExpanded;
    private bool _terminalExpanded;

    public SettingsView(IServiceProvider sp)
    {
        _cfg = sp.GetRequiredService<ISettingsService>();
        _theme = sp.GetRequiredService<IThemeService>();
        _dialogs = sp.GetRequiredService<IDialogService>();
        _graph = sp.GetRequiredService<IGraphLayoutService>();
        _repoCache = sp.GetRequiredService<Historia.Services.Storage.IRepoStateCache>();

        _onCfg = (_, _) => Post();
        _onTheme = (_, _) => Post();

        Content = ClosablePane(new ScrollViewer { Content = _root }, sp);
        this.Dyn(BackgroundProperty, "ColorChrome");
        // On the view itself, not _root: empirically, a Tunnel handler on _root (the
        // ScrollViewer's content host) never saw a press that landed on a plain TextBlock —
        // only on box-typed controls — while the same handler on the UserControl root sees
        // every press in the subtree, close button included.
        this.AddHandler(PointerPressedEvent, OnRootPointerPressedTunnel, RoutingStrategies.Tunnel);
        _cfg.Changed += _onCfg;
        _theme.ThemeChanged += _onTheme;
        Rebuild();
    }

    /// <summary>Every settings textbox commits on <c>LostFocus</c>, but clicking a
    /// non-focusable label or blank space never moves focus, so an edit sits uncommitted until
    /// the user happens to click another input. Clearing focus here on any press that does not
    /// land on a real input lets the previously focused textbox's own LostFocus commit handler
    /// run — no commit logic moves or is duplicated. Tunnel routing so this runs before the
    /// pressed control's own handler, and <c>e.Handled</c> is left alone so that control still
    /// receives the press normally.</summary>
    private static void OnRootPointerPressedTunnel(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is not Visual v)
            return;
        if (v is TextBox or ComboBox or ToggleSwitch or CheckBox or Button or NumericUpDown)
            return;
        if (v.FindAncestorOfType<TextBox>() is not null
            || v.FindAncestorOfType<ComboBox>() is not null
            || v.FindAncestorOfType<ToggleSwitch>() is not null
            || v.FindAncestorOfType<CheckBox>() is not null
            || v.FindAncestorOfType<Button>() is not null
            || v.FindAncestorOfType<NumericUpDown>() is not null)
            return;

        TopLevel.GetTopLevel(v)?.FocusManager?.ClearFocus();
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
        var appearanceBody = new StackPanel { Spacing = 8, Margin = new Thickness(0, 10, 0, 0) };
        // The custom-theme override lives in the Developer panel, so only developer mode
        // mentions it here.
        var appearanceHint = "Graph columns are set in Repository settings: click the cog in the graph's GRAPH header.";
        if (Historia.Platform.DevMode.Enabled)
            appearanceHint += "\n\nIn developer mode, a custom theme from the Developer panel overrides Light, Dark and System.";
        var themeRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var m in new[] { ThemeMode.Light, ThemeMode.Dark, ThemeMode.System })
        {
            var mode = m;
            themeRow.Children.Add(TextButton(m.ToString(), () => _ = _theme.ApplyModeAsync(mode), primary: _theme.CurrentMode == m));
        }
        appearanceBody.Children.Add(LabeledRow("Theme", themeRow));
        appearanceBody.Children.Add(WithHint(Check("Show splash screen", c.ShowSplashScreen,
            v => _ = _cfg.UpdateAsync(s => s with { ShowSplashScreen = v })),
            "A custom theme uses its own splash instead."));
        appearanceBody.Children.Add(WithHint(Check("High contrast", _theme.HighContrastEnabled, v => _ = _theme.SetHighContrastAsync(v)),
            "Overrides every other theme. Also turns on when Windows high contrast is on."));
        appearanceBody.Children.Add(WithHint(Check("Wrap tabs onto more rows", c.EnableMultiLineTabs,
            v => _ = _cfg.UpdateAsync(s => s with { EnableMultiLineTabs = v })),
            "When tabs don't fit, they wrap to a new row instead of scrolling."));
        if (c.EnableMultiLineTabs)
        {
            var maxRows = new NumericUpDown { Minimum = 2, Maximum = 5, Value = c.MaxTabRows, FormatString = "0", Width = 120, HorizontalAlignment = HorizontalAlignment.Left };
            WireNumericCommit(maxRows, c.MaxTabRows, v => Math.Clamp(v, 2, 5),
                next => _ = _cfg.UpdateAsync(s => s with { MaxTabRows = next }));
            appearanceBody.Children.Add(LabeledRow("Maximum rows", maxRows));
        }
        appearanceBody.Children.Add(WithHint(Check("Fixed tab width", c.TabWidthMode == TabWidthMode.Fixed,
            v => _ = _cfg.UpdateAsync(s => s with { TabWidthMode = v ? TabWidthMode.Fixed : TabWidthMode.Dynamic })),
            "When off, each tab fits its name, up to about 25 characters."));
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
            WireNumericCommit(tabLen, c.FixedTabWidth, AppSettings.ClampFixedTabWidth,
                next => _ = _cfg.UpdateAsync(s => s with { FixedTabWidth = next }));
            appearanceBody.Children.Add(LabeledRow("Tab width (px)", tabLen));
        }
        appearanceBody.Children.Add(WithHint(Check("Show author date", c.ShowAuthorDate, v => _ = _cfg.UpdateAsync(s => s with { ShowAuthorDate = v })),
            "Shows when a commit was written, not when it was committed. The graph stays sorted by commit date."));

        // Mouse-wheel / trackpad scroll direction for the graph and the tab strip.
        var wheelDir = new ComboBox { Width = 220 };
        wheelDir.Items.Add(new ComboBoxItem { Content = "Match system", Tag = WheelScrollDirection.Auto });
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
        appearanceBody.Children.Add(WithHint(LabeledRow("Scroll direction", wheelDir),
            "Applies to the graph and the tab strip. On Windows, Match system is always Standard."));
        appearanceBody.Children.Add(WithHint(Check("Cache sidebar between launches", c.PersistRepoCache,
            v =>
            {
                _ = _cfg.UpdateAsync(s => s with { PersistRepoCache = v });
                if (!v)
                    _repoCache.WipeAll(); // turning it off also clears what's on disk
            }),
            "Saves each repository's branches, tags, stashes and more so the sidebar appears instantly, then refreshes from Git. Turning it off deletes the cache."));
        AddCollapsibleSection("Appearance", appearanceBody, () => _appearanceExpanded, v => _appearanceExpanded = v, appearanceHint);

        // ── Privacy & Network ──
        var networkBody = new StackPanel { Spacing = 8, Margin = new Thickness(0, 10, 0, 0) };
        networkBody.Children.Add(WithHint(Check("Auto-fetch open repositories", c.AutoFetchEnabled,
            v => _ = _cfg.UpdateAsync(s => s with { AutoFetchEnabled = v })),
            "Fetches each open repository in turn: a 5-minute pause, then one repository a minute. Turn off to stop all background network traffic."));
        AddCollapsibleSection("Privacy & Network", networkBody, () => _networkExpanded, v => _networkExpanded = v,
            "Identity and SSH-only are set per repository, in Repository settings.");

        // ── Diff viewer ──
        var diffBody = new StackPanel { Spacing = 8, Margin = new Thickness(0, 10, 0, 0) };
        var diffView = new ComboBox { Width = 160 };
        diffView.Items.Add(new ComboBoxItem { Content = "Unified", Tag = DiffViewMode.Unified });
        diffView.Items.Add(new ComboBoxItem { Content = "Side by side", Tag = DiffViewMode.SideBySide });
        diffView.SelectedIndex = c.DefaultDiffView == DiffViewMode.SideBySide ? 1 : 0;
        diffView.SelectionChanged += (_, _) =>
        {
            if ((diffView.SelectedItem as ComboBoxItem)?.Tag is DiffViewMode dv)
                _ = _cfg.UpdateAsync(s => s with { DefaultDiffView = dv });
        };
        diffBody.Children.Add(WithHint(LabeledRow("Layout", diffView),
            "Also changes when you switch layout in the diff viewer."));
        var ctxLines = new NumericUpDown { Minimum = 0, Maximum = 20, Value = c.DiffContextLines, FormatString = "0", Width = 120, HorizontalAlignment = HorizontalAlignment.Left };
        WireNumericCommit(ctxLines, c.DiffContextLines, v => v,
            next => _ = _cfg.UpdateAsync(s => s with { DiffContextLines = next }));
        diffBody.Children.Add(WithHint(LabeledRow("Context lines", ctxLines),
            "Unchanged lines shown around each change."));
        diffBody.Children.Add(WithHint(Check("Wrap long lines", c.DiffWordWrap,
            v => _ = _cfg.UpdateAsync(s => s with { DiffWordWrap = v })),
            "Unified layout only."));
        AddCollapsibleSection("Diff viewer", diffBody, () => _diffExpanded, v => _diffExpanded = v);

        // ── Git ──
        var gitBody = new StackPanel { Spacing = 8, Margin = new Thickness(0, 10, 0, 0) };
        gitBody.Children.Add(WithHint(Check("Prune deleted remote branches on fetch", c.PruneOnFetch, v => _ = _cfg.UpdateAsync(s => s with { PruneOnFetch = v })),
            "On fetch and pull, removes remote branches that no longer exist on the server. Your local branches aren't touched."));
        gitBody.Children.Add(WithHint(Check("Pull with rebase", c.PullWithRebase, v => _ = _cfg.UpdateAsync(s => s with { PullWithRebase = v })),
            "Puts your local commits on top of the remote ones instead of creating a merge commit."));
        gitBody.Children.Add(Check("Reopen tabs on launch", c.RestoreSessionTabs, v => _ = _cfg.UpdateAsync(s => s with { RestoreSessionTabs = v })));
        gitBody.Children.Add(WithHint(Check("Ask for a stash message", c.UseCustomStashNames,
            v => _ = _cfg.UpdateAsync(s => s with { UseCustomStashNames = v })),
            "When off, the Stash button names the stash \"Stash of <branch>\"."));
        AddCollapsibleSection("Git", gitBody, () => _gitExpanded, v => _gitExpanded = v);

        // ── Terminal ──
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
        shellRow.Children.Add(Text("Shell", "ColorText", 13));
        shellRow.Children.Add(shellPath);
        shellRow.Children.Add(browse);
        termBody.Children.Add(shellRow);

        var shellArgs = new TextBox { Text = string.Join(' ', c.TerminalShellArgs), Watermark = "Separated by spaces", Width = 360, HorizontalAlignment = HorizontalAlignment.Left };
        shellArgs.LostFocus += (_, _) => _ = _cfg.UpdateAsync(s => s with
        {
            TerminalShellArgs = shellArgs.Text?.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [],
        });
        termBody.Children.Add(WithHint(LabeledRow("Shell arguments", shellArgs),
            "Quotes aren't supported — each space starts a new argument."));

        var fontFamily = new TextBox { Text = c.TerminalFontFamily ?? "", Watermark = OperatingSystem.IsWindows() ? "Cascadia Mono, Consolas" : "platform monospace", Width = 280, HorizontalAlignment = HorizontalAlignment.Left };
        fontFamily.LostFocus += (_, _) => _ = _cfg.UpdateAsync(s => s with { TerminalFontFamily = string.IsNullOrWhiteSpace(fontFamily.Text) ? null : fontFamily.Text.Trim() });
        termBody.Children.Add(LabeledRow("Font family", fontFamily));

        var fontSize = new NumericUpDown { Minimum = 8, Maximum = 32, Value = c.TerminalFontSize, FormatString = "0", Width = 120, HorizontalAlignment = HorizontalAlignment.Left };
        WireNumericCommit(fontSize, c.TerminalFontSize, v => v,
            next => _ = _cfg.UpdateAsync(s => s with { TerminalFontSize = next }));
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
        WireNumericCommit(scrollback, c.TerminalScrollbackLines, AppSettings.ClampTerminalScrollback,
            next => _ = _cfg.UpdateAsync(s => s with { TerminalScrollbackLines = next }));
        termBody.Children.Add(WithHint(LabeledRow("Scrollback (lines)", scrollback),
            "Lines kept above the screen. 5,000 lines take about 5 MB per open terminal."));
        termBody.Children.Add(WithHint(Check("Enhanced keyboard (Kitty protocol)", c.TerminalKittyKeyboard,
            v => _ = _cfg.UpdateAsync(s => s with { TerminalKittyKeyboard = v })),
            "Lets editors such as Neovim and Helix tell apart keys like Ctrl+Enter and Shift+Enter. Applies to new terminals."));
        termBody.Children.Add(WithHint(Check("Allow programs to copy to the clipboard (OSC 52)", c.TerminalAllowClipboardWrite,
            v => _ = _cfg.UpdateAsync(s => s with { TerminalAllowClipboardWrite = v })),
            "Lets copy in Vim or tmux reach the system clipboard, including over SSH. Programs can never read the clipboard."));

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
        termBody.Children.Add(Check("Blinking cursor", c.TerminalCursorBlink, v => _ = _cfg.UpdateAsync(s => s with { TerminalCursorBlink = v })));

        AddCollapsibleSection("Terminal", termBody, () => _terminalExpanded, v => _terminalExpanded = v);

        // ── Advanced ──
        var advBody = new StackPanel { Spacing = 8 };

        advBody.Children.Add(WithHint(Check("Auto hide notices", c.AutoHideNotices,
            v => _ = _cfg.UpdateAsync(s => s with { AutoHideNotices = v })),
            "Closes notices in the bottom-right corner after 10 seconds. Turn off to keep them until you close them."));

        advBody.Children.Add(WithHint(Check("Keep commit details on disk", c.GraphMetadataFromDisk,
            v => _ = _cfg.UpdateAsync(s => s with { GraphMetadataFromDisk = v })),
            "Reads commit messages, authors and dates from a disk cache to save memory on large repositories. Takes effect after restart."));

        advBody.Children.Add(WithHint(Check("Fast graph updates (experimental)", c.EnableGraphSplice,
            v => _ = _cfg.UpdateAsync(s => s with { EnableGraphSplice = v })),
            "On large repositories, adds new commits to the saved graph instead of rebuilding it. Takes effect after restart."));

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
        WireNumericCommit(silentCrawlCap, c.SilentCrawlRowCap, AppSettings.ClampSilentCrawlRowCap,
            next => _ = _cfg.UpdateAsync(s => s with { SilentCrawlRowCap = next }));
        advBody.Children.Add(WithHint(LabeledRow("Background preload (rows)", silentCrawlCap),
            "Rows loaded in the background after the graph opens, and again each time you scroll past them. 0 = no limit. Takes effect after restart."));

        var residentRowCap = new NumericUpDown
        {
            Minimum = AppSettings.MinResidentRowCap,
            Maximum = AppSettings.MaxResidentRowCap,
            Increment = 100_000,
            Value = c.ResidentRowCap,
            FormatString = "0",
            Width = 140,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        WireResidentRowCapCommit(residentRowCap, c.ResidentRowCap, _dialogs,
            next => _cfg.UpdateAsync(s => s with { ResidentRowCap = next }));
        advBody.Children.Add(WithHint(LabeledRow("Max rows in memory", residentRowCap),
            "Most rows the graph keeps loaded per repository. Only used when commit details are on disk; otherwise 500,000. Takes effect after restart."));

        var autolinkHint = "Turns issue keys in commit messages into links. In the URL, $1, $2… insert the pattern's capture groups."
            + "\n\nExample: pattern #(\\d+) with URL https://GitHistoria.com/autolink?issue=$1 turns #42 into a link.";
        var autolinkHeading = WithHint(Section("Issue autolinks"), autolinkHint);
        autolinkHeading.Margin = new Thickness(0, 8, 0, 0);
        advBody.Children.Add(autolinkHeading);

        for (var i = 0; i < c.IssueAutolinks.Count; i++)
        {
            var idx = i;
            var rule = c.IssueAutolinks[idx];
            var pat = new TextBox { Text = rule.Pattern, Watermark = "Pattern (regex)", Width = 200 };
            var url = new TextBox { Text = rule.UrlTemplate, Watermark = "Link URL ($1 = first group)", Width = 300 };
            pat.LostFocus += (_, _) => _ = _cfg.UpdateAsync(s => s with { IssueAutolinks = [.. s.IssueAutolinks.Select((r, j) => j == idx ? r with { Pattern = pat.Text ?? "" } : r)] });
            url.LostFocus += (_, _) => _ = _cfg.UpdateAsync(s => s with { IssueAutolinks = [.. s.IssueAutolinks.Select((r, j) => j == idx ? r with { UrlTemplate = url.Text ?? "" } : r)] });
            var rm = TextButton("Remove", () => _ = _cfg.UpdateAsync(s => s with { IssueAutolinks = [.. s.IssueAutolinks.Where((_, j) => j != idx)] }));
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            row.Children.Add(pat); row.Children.Add(url); row.Children.Add(rm);
            advBody.Children.Add(row);
        }
        var addRule = TextButton("Add rule", () => _ = _cfg.UpdateAsync(s => s with { IssueAutolinks = [.. s.IssueAutolinks, new IssueAutolinkRule { Pattern = "", UrlTemplate = "" }] }));
        addRule.HorizontalAlignment = HorizontalAlignment.Left;
        advBody.Children.Add(addRule);

        advBody.Margin = new Thickness(0, 10, 0, 0);

        AddCollapsibleSection("Advanced", advBody, () => _advExpanded, v => _advExpanded = v);
    }

    /// <summary>
    /// A section header that toggles <paramref name="body"/>'s visibility on click: left title,
    /// right chevron, a rule underneath — the whole row is the hit target, no box chrome.
    /// <paramref name="hint"/> puts a <see cref="HelpHint"/> beside the title; pressing it
    /// doesn't toggle the section.
    /// </summary>
    private void AddCollapsibleSection(string title, Control body, Func<bool> isExpanded, Action<bool> setExpanded, string? hint = null)
    {
        body.IsVisible = isExpanded();

        var arrow = Text(isExpanded() ? "▼" : "▶", "ColorTextMuted", 13);
        arrow.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(arrow, 1);

        Control titleText = Text(title, "ColorText", 13, FontWeight.SemiBold);
        titleText.VerticalAlignment = VerticalAlignment.Center;
        if (hint is not null)
        {
            titleText = WithHint(titleText, hint);
            var badge = ((Panel)titleText).Children.First(ch => ch.Classes.Contains(HelpHintClass));
            badge.PointerPressed += (_, e) => e.Handled = true;
        }

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

    /// <summary>
    /// Commits a <see cref="NumericUpDown"/>'s value through <paramref name="clamp"/> to
    /// <paramref name="commit"/> on <c>LostFocus</c> and on Enter, only when the clamped value
    /// changed. Enter is caught with <c>AddHandler(KeyDownEvent, …, handledEventsToo: true)</c> so
    /// an inner <c>TextBox</c> can't swallow it first.
    ///
    /// Verified against Avalonia 11.3.20's <c>NumericUpDown.OnKeyDown</c> source: its own
    /// <c>Key.Enter</c> case calls <c>CommitInput()</c> → <c>SyncTextAndValueProperties</c>, which
    /// parses the typed text into <c>Value</c> synchronously before returning. That runs as
    /// <c>NumericUpDown</c>'s own KeyDown handling on the same routed event, ahead of any instance
    /// handler added here on the same control, so by the time this fires <c>nud.Value</c> already
    /// reflects what's on screen — no extra text-to-value step is needed.
    /// </summary>
    internal static void WireNumericCommit(NumericUpDown nud, int initial, Func<int, int> clamp, Action<int> commit)
    {
        var prev = initial;
        void TryCommit()
        {
            var next = clamp((int)(nud.Value ?? prev));
            if (next == prev)
                return;
            prev = next;
            commit(next);
        }
        nud.LostFocus += (_, _) => TryCommit();
        nud.AddHandler(InputElement.KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Enter)
                TryCommit();
        }, handledEventsToo: true);
    }

    /// <summary>
    /// Commits the Max rows in memory field behind a destructive confirm, plus a second one when
    /// the new value is above <see cref="AppSettings.ResidentRowCapWarnThreshold"/>. Cancelling
    /// either restores the saved value.
    /// </summary>
    internal static void WireResidentRowCapCommit(
        NumericUpDown residentRowCap,
        int initial,
        IDialogService dialogs,
        Func<int, Task> updateAsync)
    {
        var prev = initial;
        var dialogOpen = false;

        async void Commit()
        {
            // Re-entrancy guard: reverting the value on Cancel below can itself provoke a
            // second LostFocus/Enter while the first dialog is still closing — ignore it.
            if (dialogOpen)
                return;

            var next = AppSettings.ClampResidentRowCap((int)(residentRowCap.Value ?? prev));
            if (next == prev)
                return;

            dialogOpen = true;
            try
            {
                // Committing from the same Enter keystroke that's about to open a modal dialog
                // races the physical key: the dialog's own default button (Cancel, here — a
                // destructive confirm defaults to safe) can end up catching that same keystroke's
                // trailing key-up the instant the new window takes focus, auto-cancelling itself
                // before the user ever sees it. Flushing the Input-priority queue alone
                // isn't enough — that's Avalonia's queue, not the OS message queue the
                // native key-up is still sitting in. A short real-time delay lets that message
                // land on the field (harmless — NumericUpDown has no Enter-linked side effect
                // after commit) before the dialog exists to steal it. Also covers the same race
                // for the spin-button-click-driven LostFocus path (see the capture note below).
                await Task.Delay(150);

                // A click on the spin-button arrows moves focus in/out of the control's internal
                // TextBox, which fires this same LostFocus — while its RepeatButton's own pointer
                // capture is still active. Opening a modal dialog right away can strand that
                // capture (the button reads as permanently pressed, repeating forever) because the
                // dialog's nested loop takes over before the pending PointerReleased is processed.
                // Flushing the Input-priority queue first lets that release land normally.
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { }, Avalonia.Threading.DispatcherPriority.Input);

                // Only update next from a live Value — if a Rebuild() ran during the
                // await and detached this control, Value comes back null. Falling back
                // to prev would make next == prev and silently discard the committed value.
                var liveVal = residentRowCap.Value;
                if (liveVal.HasValue)
                    next = AppSettings.ClampResidentRowCap((int)liveVal.Value);
                if (next == prev)
                    return;

                var proceed = await dialogs.ConfirmDestructiveAsync(
                    "Change the resident row cap?",
                    "Changing this value affects how many rows the graph will keep resident in memory for large repositories. Applies after restart of Historia.",
                    "Continue");

                // Intentional double warning: above the threshold a second, heavier confirm
                // follows the first, so a large cap is never saved on one reflexive click.
                if (proceed && next > AppSettings.ResidentRowCapWarnThreshold)
                {
                    // Same key-up race as above if the first dialog was confirmed from the keyboard.
                    await Task.Delay(150);
                    proceed = await dialogs.ConfirmDestructiveAsync(
                        "Keep this many rows in memory?",
                        $"Keeping more than {AppSettings.ResidentRowCapWarnThreshold:N0} rows in memory could drain memory heavily on large repositories. Do you really want to save {next:N0} rows?",
                        "Save anyway");
                }

                if (!proceed)
                {
                    residentRowCap.Value = prev;
                    return;
                }

                prev = next;
                await updateAsync(next);
            }
            finally
            {
                dialogOpen = false;
            }
        }

        residentRowCap.LostFocus += (_, _) => Commit();
        residentRowCap.AddHandler(InputElement.KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Enter)
                Commit();
        }, handledEventsToo: true);
    }

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

/// <summary>Per-repository settings.</summary>
public sealed class RepoSettingsView : UserControl, IDisposable
{
    private readonly IServiceProvider _sp;
    private readonly IWorkspaceService _workspace;
    private readonly Historia.Services.Settings.IRepoLayoutService _repoLayout;
    private readonly IProfileService _profiles;
    private readonly INavigationBus? _nav;
    private readonly INotificationService? _notifications;
    private readonly StackPanel _root = new() { Spacing = 12, Margin = new Thickness(32), MaxWidth = 560 };
    private readonly Panel _overlay = new() { IsHitTestVisible = false };

    private readonly EventHandler<Historia.Services.Settings.RepoLayoutChangedEventArgs> _onLayout;
    private readonly EventHandler _onProfilesChanged;
    private bool _syncingSshOnly;

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
        _sp = sp;
        _workspace = sp.GetRequiredService<IWorkspaceService>();
        _repoLayout = sp.GetRequiredService<Historia.Services.Settings.IRepoLayoutService>();
        _profiles = sp.GetRequiredService<IProfileService>();
        _nav = sp.GetService<INavigationBus>();
        _notifications = sp.GetService<INotificationService>();
        _onLayout = (_, _) =>
        {
            if (!_syncingSshOnly)
                Avalonia.Threading.Dispatcher.UIThread.Post(Rebuild);
        };
        _onProfilesChanged = (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(Rebuild);
        Content = new Panel { Children = { new ScrollViewer { Content = _root }, _overlay } };
        this.Dyn(BackgroundProperty, "ColorChrome");
        _root.AddHandler(PointerMovedEvent, OnRowsPointerMoved, RoutingStrategies.Bubble);
        _root.AddHandler(PointerReleasedEvent, OnRowsPointerReleased, RoutingStrategies.Bubble);
        _root.PointerCaptureLost += OnRowsCaptureLost;
        _workspace.FocusedRepositoryChanged += OnFocusChanged;
        _repoLayout.Changed += _onLayout;
        _profiles.ProfilesChanged += _onProfilesChanged;
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

        if (ctx is not null)
        {
            // ── Identity ──
            _root.Children.Add(WithHint(Text("Identity", "ColorTextMuted", 13, FontWeight.SemiBold),
                "Name, email and signing key used for commits in this repository."));

            var key = ctx.IdentityKey;
            string label;
            if (key is { } k && _profiles.IsLocalConfigSelected(k))
            {
                label = "Local config";
            }
            else if (ctx.State.ActiveProfile is { } p)
            {
                // stale rendering: a row that names a missing profile shows "<name> (unavailable)".
                label = _profiles.GetProfileById(p.Id) is null
                    ? $"{p.DisplayName} (unavailable)"
                    : p.IsGlobal ? "Local config" : p.DisplayName;
            }
            else
            {
                label = "Local config";
            }

            var profileBtn = new Button
            {
                Content = label + "  ▾",
                Padding = new Thickness(10, 4),
                HorizontalAlignment = HorizontalAlignment.Left,
                Cursor = new Cursor(StandardCursorType.Hand),
            };
            profileBtn.Click += (_, _) => OpenProfileMenu(profileBtn, ctx);
            _root.Children.Add(profileBtn);

            // ── Network ──
            _root.Children.Add(Text("Network", "ColorTextMuted", 13, FontWeight.SemiBold));
            var sshOnly = new ToggleSwitch
            {
                Content = "SSH only",
                IsChecked = _repoLayout.Get(ctx.RepositoryPath).SshOnly,
                HorizontalAlignment = HorizontalAlignment.Left,
                Cursor = new Cursor(StandardCursorType.Hand),
            };
            sshOnly.IsCheckedChanged += (_, _) => _ = OnSshOnlyToggledAsync(sshOnly.IsChecked == true, ctx);
            _root.Children.Add(WithHint(sshOnly,
                "Fetch, pull and push always use SSH, even for HTTPS remotes. HTTPS is never tried."));
        }

        _root.Children.Add(WithHint(Text("Graph columns", "ColorTextMuted", 13, FontWeight.SemiBold),
            "Order and visibility apply to all repositories. Widths are per repository and reset on restart."));

        // the editor works on the application-wide column set and shows even with no repo.
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

    private async Task OnSshOnlyToggledAsync(bool on, RepositoryContext ctx)
    {
        _syncingSshOnly = true;
        try
        {
            await _repoLayout.UpdateAsync(ctx.RepositoryPath, l => l with { SshOnly = on });
        }
        finally
        {
            _syncingSshOnly = false;
        }
    }

    private void OpenProfileMenu(Button anchor, RepositoryContext ctx)
    {
        var key = ctx.IdentityKey;
        var localSelected = key is { } k && _profiles.IsLocalConfigSelected(k);
        var active = ctx.State.ActiveProfile;

        var menu = new MenuFlyout();
        var menuItems = new List<Control>();

        var local = new MenuItem
        {
            Header = "Local config",
            Icon = localSelected ? new TextBlock { Text = "✓" } : null,
        };
        local.Click += (_, _) => _ = SelectLocalConfigAsync(ctx);
        menuItems.Add(local);
        menuItems.Add(new Separator());

        foreach (var p in _profiles.GetAllProfiles())
        {
            var pp = p;
            var mi = new MenuItem
            {
                Header = p.IsGlobal ? "Global (~/.gitconfig)" : p.DisplayName,
                Icon = !localSelected && active is not null && active.Id == p.Id ? new TextBlock { Text = "✓" } : null,
            };
            mi.Click += (_, _) => _ = BindProfileAsync(ctx, pp);
            menuItems.Add(mi);
        }
        menuItems.Add(new Separator());
        var add = new MenuItem { Header = "Add profile…" };
        add.Click += (_, _) => _nav?.RequestNavigate("profiles?intent=add");
        menuItems.Add(add);
        var manage = new MenuItem { Header = "Manage profiles…" };
        manage.Click += (_, _) => _nav?.RequestNavigate("profiles");
        menuItems.Add(manage);

        menu.ItemsSource = menuItems;
        menu.ShowAt(anchor);
    }

    private async Task<RepoIdentityKey> KeyForAsync(RepositoryContext ctx)
        => ctx.IdentityKey ?? await (_sp.GetService<IRepoIdentityService>()?.ResolveAsync(ctx, ctx.Lifetime) ?? Task.FromResult(new RepoIdentityKey("")));

    private async Task BindProfileAsync(RepositoryContext ctx, GitProfile profile)
    {
        try
        {
            var key = await KeyForAsync(ctx);
            await _profiles.BindProfileToRepositoryAsync(key, ctx.RepositoryPath, profile.Id, ctx.Lifetime);
            ctx.State.SetActiveProfile(profile);
            Rebuild();
        }
        catch (Exception ex)
        {
            _notifications?.Publish(NotificationSeverity.Error,
                $"Could not set profile: {ex.Message}", ex.NotificationDetails());
        }
    }

    private async Task SelectLocalConfigAsync(RepositoryContext ctx)
    {
        try
        {
            var key = await KeyForAsync(ctx);
            await _profiles.SetLocalConfigForRepositoryAsync(key, ctx.RepositoryPath, ctx.Lifetime);
            ctx.State.SetActiveProfile(null);
            Rebuild();
        }
        catch (Exception ex)
        {
            _notifications?.Publish(NotificationSeverity.Error,
                $"Could not set profile: {ex.Message}", ex.NotificationDetails());
        }
    }

    public void Dispose()
    {
        _workspace.FocusedRepositoryChanged -= OnFocusChanged;
        _repoLayout.Changed -= _onLayout;
        _profiles.ProfilesChanged -= _onProfilesChanged;
    }
}
