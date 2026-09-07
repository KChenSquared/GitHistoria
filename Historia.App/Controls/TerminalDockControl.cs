using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Historia.Services.Settings;
using Historia.Services.Terminal;
using Historia.Services.Theming;
using Historia.Services.Workspace;

namespace Historia.Controls;

/// <summary>
/// The bottom terminal dock (V5 §7): a 6 px splitter, a live-session tab strip, a small
/// toolbar, and the <see cref="TerminalControl"/> host. Collapses to zero height. Native
/// control — no WebView airspace work (design doc §7.1).
/// </summary>
public sealed class TerminalDockControl : UserControl, IDisposable
{
    private const double SplitterHeight = 8;
    // Tall enough for the two vertically-stacked header buttons (✕ / ＋).
    private const double HeaderHeight = 40;
    private const double MinDock = 120;
    // Keep at least this much of the centre panel visible above the dock (review §1).
    private const double MinCentreReserve = 80;

    private ITerminalSessionService _sessions = default!;
    private IWorkspaceService _workspace = default!;
    private ISettingsService _settings = default!;
    private IKeyboardCommandRouter _keyboard = default!;
    private INotificationService _notifications = default!;
    private IThemeService _theme = default!;
    private IServiceProvider _services = default!;
    private IDisposable? _chord;
    private bool _wired;

    private readonly Grid _root;
    private readonly Thumb _splitter;
    private readonly StackPanel _tabStrip;
    private readonly ScrollViewer _tabScroller;
    private readonly Button _tabLeft;
    private readonly Button _tabRight;
    private readonly ContentControl _body;
    private readonly ScrollBar _vScroll;
    private readonly TerminalControl _terminal = new();
    private bool _syncingScroll;

    private bool _open;
    // The dock was hidden automatically because the focused repo has no terminal — as opposed
    // to the user hiding it with ✕. Only an auto-hidden dock re-shows itself when focus lands
    // on a repo that does have one.
    private bool _autoHidden;
    private bool _firstOpenSized;
    private double _dockHeight = 260;
    private DispatcherTimer? _persistDebounce;

    // On its first reveal this run the dock takes 35 % of the centre panel (review follow-up),
    // regardless of the persisted height; the user's drags then take over.
    private const double FirstOpenFraction = 0.35;

    public TerminalDockControl()
    {
        _tabStrip = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center };

        // Buttons stacked vertically on the right (review §3): close on top, new-terminal below.
        // The clear and maximize/restore buttons are gone; "+" opens another Home terminal,
        // exactly as the Historia-menu entry does.
        var toolbar = new StackPanel { Orientation = Orientation.Vertical, Spacing = 2, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top };
        toolbar.Children.Add(ToolButton("✕", "Close terminal dock", () => SetOpen(false)));
        toolbar.Children.Add(ToolButton("＋", "New terminal", NewHomeTerminal));
        // Below "+": shuts every open terminal tab down, one at a time — closing the dock
        // itself (✕ above) only hides it and leaves every session running.
        toolbar.Children.Add(ToolButton("－－", "Close all terminals", () => _ = CloseAllTerminalsAsync()));

        var header = new Grid { Height = HeaderHeight };
        _tabScroller = new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = _tabStrip,
            VerticalAlignment = VerticalAlignment.Top,
        };
        // Same single-row scroll model as the app-band strip (review §7): plain < > with no
        // borders, shown only on overflow, plus wheel-to-pan. No multi-line support. The pair
        // sits together on the right, next to the ✕ stack (review follow-up).
        _tabLeft = ToolButton("<", "Scroll tabs left", () => ScrollTabs(-1));
        _tabRight = ToolButton(">", "Scroll tabs right", () => ScrollTabs(+1));
        // Share the app-band arrow chrome: disabled fades the glyph but keeps the ground flush
        // with the enabled sibling, instead of Fluent's grey disabled box.
        _tabLeft.Classes.Add("flat");
        _tabRight.Classes.Add("flat");
        _tabLeft.IsVisible = _tabRight.IsVisible = false;

        var tabNav = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Top,
        };
        tabNav.Children.Add(_tabLeft);
        tabNav.Children.Add(_tabRight);

        var tabRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(6, 4, 34, 0),
            VerticalAlignment = VerticalAlignment.Top,
        };
        _tabScroller[Grid.ColumnProperty] = 0;
        tabNav[Grid.ColumnProperty] = 1;
        tabRow.Children.Add(_tabScroller);
        tabRow.Children.Add(tabNav);

        _tabScroller.AddHandler(PointerWheelChangedEvent, OnTabStripWheel, RoutingStrategies.Tunnel);
        _tabScroller.ScrollChanged += (_, _) => UpdateTabOverflow();
        _tabScroller.PropertyChanged += (_, e) =>
        {
            if (e.Property == BoundsProperty)
                UpdateTabOverflow();
        };

        header.Children.Add(tabRow);
        header.Children.Add(toolbar);
        header[Grid.RowProperty] = 1;

        _splitter = new Thumb
        {
            Height = SplitterHeight,
            Cursor = new Cursor(StandardCursorType.SizeNorthSouth),
            Background = Brushes.Transparent,
            Template = new Avalonia.Controls.Templates.FuncControlTemplate((_, _) =>
            {
                // Transparent full-height grab strip with a faint 1 px seam at the top edge.
                var seam = new Border { Height = 1, VerticalAlignment = VerticalAlignment.Top };
                seam.Bind(Border.BackgroundProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("ColorBorder"));
                return new Border { Background = Brushes.Transparent, Child = seam };
            }),
        };
        _splitter.DragDelta += OnSplitterDrag;
        _splitter.DragCompleted += (_, _) => QueuePersist();
        _splitter[Grid.RowProperty] = 0;

        // A real vertical scrollbar on the right edge of the terminal body, sitting directly
        // under the ＋ button. Shown only when the scrollback is taller than the viewport.
        _vScroll = new ScrollBar
        {
            Orientation = Orientation.Vertical,
            // We drive IsVisible from the render metrics ourselves — keep the control from
            // also self-hiding on its Minimum/Maximum.
            Visibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Visible,
            Minimum = 0,
            IsVisible = false,
        };
        _vScroll.Scroll += OnVScrollScroll;
        _vScroll[Grid.ColumnProperty] = 1;
        _terminal[Grid.ColumnProperty] = 0;
        _terminal.ScrollMetricsChanged += (_, _) => SyncScrollBar();

        var bodyGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        bodyGrid.Children.Add(_terminal);
        bodyGrid.Children.Add(_vScroll);

        _body = new ContentControl { Content = bodyGrid };
        _body[Grid.RowProperty] = 2;

        _root = new Grid
        {
            RowDefinitions = new RowDefinitions($"{SplitterHeight},{HeaderHeight},*"),
        };
        _root.Children.Add(_splitter);
        _root.Children.Add(header);
        _root.Children.Add(_body);

        Content = _root;
        IsVisible = false;
        Height = 0;
    }

    public void Initialize(IServiceProvider services)
    {
        if (_wired)
            return;
        _services = services;
        _sessions = services.GetRequiredService<ITerminalSessionService>();
        _workspace = services.GetRequiredService<IWorkspaceService>();
        _settings = services.GetRequiredService<ISettingsService>();
        _keyboard = services.GetRequiredService<IKeyboardCommandRouter>();
        _notifications = services.GetRequiredService<INotificationService>();
        _theme = services.GetRequiredService<IThemeService>();

        _terminal.Attach(services);
        _terminal.RestartRequested += OnTerminalRestartRequested;

        _dockHeight = Math.Max(MinDock, _settings.Current.TerminalDockHeight);

        _sessions.SessionsChanged += OnSessionsChanged;
        _sessions.FocusedSessionChanged += OnFocusedChanged;
        _sessions.SessionExited += OnSessionExited;
        _sessions.ActivationRequested += OnActivationRequested;
        _workspace.FocusedRepositoryChanged += OnFocusedRepositoryChanged;

        _chord = _keyboard.Register("Ctrl+OemTilde", () =>
        {
            Dispatcher.UIThread.Post(ToggleFromKeyboard);
            return Task.CompletedTask;
        });

        _theme.ThemeChanged += OnThemeChanged;

        RebuildTabs();
        _wired = true;
    }

    private void OnThemeChanged(object? sender, ThemeChangedEventArgs e) => Dispatcher.UIThread.Post(RebuildTabs);


    // ── open / close / maximize ─────────────────────────────────────────────────────────────

    public void ToggleFromKeyboard()
    {
        if (_open)
        {
            SetOpen(false);
        }
        else
        {
            SetOpen(true);
            _terminal.Focus();
        }
    }

    // persist:false is for the automatic show/hide as repo focus moves — that must not
    // overwrite the user's own last open/closed choice in settings.
    private void SetOpen(bool open, bool persist = true)
    {
        _open = open;
        IsVisible = open;
        if (persist || open)
            _autoHidden = false;

        if (open && !_firstOpenSized)
        {
            var centre = CentrePanelHeight();
            if (centre > 0)
                _dockHeight = centre * FirstOpenFraction;
            _firstOpenSized = true;
        }

        Height = open ? ClampedHeight(_dockHeight) : 0;
        if (persist)
            _ = _settings.UpdateAsync(s => s with { TerminalDockOpen = open });
        if (open)
            Dispatcher.UIThread.Post(() => _terminal.Focus());
    }

    // ── follow the focused repository (V8.4) ────────────────────────────────────────────────

    private void OnFocusedRepositoryChanged(object? sender, FocusedRepositoryChangedEventArgs e)
        => Dispatcher.UIThread.Post(() =>
        {
            try
            {
                var match = e.New is { } repo ? FindRepoSession(repo) : null;
                if (match is not null)
                {
                    // The new repo has a terminal — make it the active tab, and bring the dock
                    // back if it was auto-hidden for a previous terminal-less repo.
                    _sessions.Focus(match);
                    if (_autoHidden)
                        SetOpen(true, persist: false);
                }
                else if (_open && _sessions.Focused?.Origin.Kind == TerminalOriginKind.Repository)
                {
                    // The dock is showing some other repo's terminal and the newly focused repo
                    // has none — hide the dock (all sessions keep running) until focus returns
                    // to a repo that does. A Home terminal is left visible.
                    SetOpen(false, persist: false);
                    _autoHidden = true;
                }
            }
            catch { /* repo-follow is a convenience; never surface as a fatal error */ }
        });

    /// <summary>The live session whose origin matches <paramref name="repo"/> — by stable repo
    /// identity key, or, when identity resolution hasn't caught up yet, by start directory.</summary>
    private TerminalSession? FindRepoSession(Historia.Services.Workspace.RepositoryContext repo)
    {
        var origin = AppBandControl.RepoOrigin(repo);
        return _sessions.Sessions.FirstOrDefault(s =>
            (origin.RepoKey is { } k && s.Origin.RepoKey == k)
            || PathsEqual(s.Origin.WorkingDirectory, origin.WorkingDirectory));
    }

    private static bool PathsEqual(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
            return false;
        try
        {
            return string.Equals(
                System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(a)),
                System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(b)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private void NewHomeTerminal()
        => _ = _sessions.OpenNewAsync(TerminalOrigin.Home(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));

    // Shuts every session down one at a time (not in parallel) so each shell gets a clean kill
    // in turn rather than a burst of simultaneous pipe teardowns.
    private async Task CloseAllTerminalsAsync()
    {
        foreach (var session in _sessions.Sessions)
            await _sessions.CloseAsync(session.Id);
    }

    /// <summary>Height of the centre column (graph area + this dock). The nearest <see cref="Grid"/>
    /// ancestor is the centre wrapper grid from <c>MainWindow.axaml</c>; the immediate visual
    /// parent is just a content presenter sized to the dock itself, so clamping against it would
    /// pin the dock to its current height and block growing it (review follow-up: "resizable").</summary>
    private double CentrePanelHeight()
        => this.FindAncestorOfType<Grid>()?.Bounds.Height
           ?? TopLevel.GetTopLevel(this)?.Bounds.Height
           ?? 0;

    private double ClampedHeight(double h)
    {
        var host = CentrePanelHeight();
        if (host <= 0)
            return Math.Max(MinDock, h);
        return Math.Clamp(h, MinDock, Math.Max(MinDock, host - MinCentreReserve));
    }

    private void OnSplitterDrag(object? sender, VectorEventArgs e)
    {
        if (!_open)
            return;
        _dockHeight = ClampedHeight(_dockHeight - e.Vector.Y); // drag up = taller
        Height = _dockHeight;
    }

    private void QueuePersist()
    {
        _persistDebounce ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _persistDebounce.Stop();
        _persistDebounce.Tick -= OnPersistTick;
        _persistDebounce.Tick += OnPersistTick;
        _persistDebounce.Start();
    }

    private void OnPersistTick(object? sender, EventArgs e)
    {
        _persistDebounce!.Stop();
        var h = _dockHeight;
        _ = _settings.UpdateAsync(s => s with { TerminalDockHeight = h });
    }

    // ── session events ──────────────────────────────────────────────────────────────────────

    private void OnSessionsChanged(object? sender, EventArgs e)
        => Dispatcher.UIThread.Post(RebuildTabs);

    // Closing the dock (✕) only hides it; an entry point reopens it (review §2).
    private void OnActivationRequested(object? sender, EventArgs e)
        => Dispatcher.UIThread.Post(() =>
        {
            SetOpen(true);
            _terminal.Focus();
        });

    private void OnFocusedChanged(object? sender, EventArgs e)
        => Dispatcher.UIThread.Post(() =>
        {
            _terminal.SetSession(_sessions.Focused);
            RebuildTabs();
        });

    private void OnSessionExited(object? sender, TerminalSession session)
        => Dispatcher.UIThread.Post(RebuildTabs);

    private void OnTerminalRestartRequested(object? sender, EventArgs e)
    {
        if (_terminal.Session is not { State: TerminalSessionState.Exited } dead)
            return;
        var origin = dead.Origin;
        _ = RestartAsync(dead.Id, origin);
    }

    private async Task RestartAsync(Guid deadId, TerminalOrigin origin)
    {
        await _sessions.CloseAsync(deadId);
        await _sessions.OpenOrFocusAsync(origin);
    }

    // ── terminal scrollbar ─────────────────────────────────────────────────────────────────

    // TerminalControl owns the scroll offset; mirror its metrics onto the ScrollBar, and feed
    // user drags back. `_syncingScroll` breaks the metrics → bar → metrics echo.
    private void SyncScrollBar()
    {
        var m = _terminal.CurrentScrollMetrics;
        var scrollable = m.Max > 0 && m.Viewport > 0;
        _vScroll.IsVisible = scrollable;
        if (!scrollable)
            return;

        _syncingScroll = true;
        _vScroll.Maximum = m.Max;
        _vScroll.ViewportSize = m.Viewport;
        _vScroll.LargeChange = Math.Max(1, m.Viewport - 1);
        _vScroll.SmallChange = 1;
        _vScroll.Value = Math.Clamp(m.Value, 0, m.Max);
        _syncingScroll = false;
    }

    private void OnVScrollScroll(object? sender, ScrollEventArgs e)
    {
        if (_syncingScroll)
            return;
        _terminal.SetScrollValue((int)Math.Round(e.NewValue));
    }

    // ── tab strip ───────────────────────────────────────────────────────────────────────────

    private Button? _focusedTabButton;

    private void RebuildTabs()
    {
        _tabStrip.Children.Clear();
        _focusedTabButton = null;
        var focused = _sessions.Focused;
        _terminal.SetSession(focused);

        foreach (var session in _sessions.Sessions)
        {
            var isFocused = ReferenceEquals(session, focused);
            var label = session.ReportedTitle ?? session.Origin.Title;
            if (session.State is TerminalSessionState.Exited)
                label += "  [exited]";

            var text = new TextBlock
            {
                Text = label,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 4, 0),
                FontSize = 12,
            };
            var close = new Button
            {
                Content = "✕",
                FontSize = 10,
                Padding = new Thickness(4, 0),
                Background = Brushes.Transparent,
                BorderThickness = default,
            };
            var captured = session;
            close.Click += (_, _) => _ = _sessions.CloseAsync(captured.Id);

            var inner = new StackPanel { Orientation = Orientation.Horizontal };
            inner.Children.Add(text);
            inner.Children.Add(close);

            var tab = new Button
            {
                Content = inner,
                Padding = new Thickness(2, 2),
                Background = isFocused ? Dyn("ColorListActive") : Brushes.Transparent,
                BorderThickness = new Thickness(0, 0, 0, isFocused ? 2 : 0),
                BorderBrush = Dyn("ColorPrimary"),
            };
            tab.Click += (_, _) =>
            {
                _sessions.Focus(captured);
                _terminal.SetSession(captured);
                _terminal.Focus();
                RebuildTabs();
            };
            _tabStrip.Children.Add(tab);
            if (isFocused)
                _focusedTabButton = tab;
        }

        Dispatcher.UIThread.Post(() =>
        {
            UpdateTabOverflow();
            ScrollFocusedTabIntoView();
        }, DispatcherPriority.Loaded);
    }

    // The active terminal's tab should always be reachable without manual scrolling — when the
    // strip overflows and focus moves to a session whose tab is off-screen (session switched via
    // keyboard, a new session opened, one closed), bring its tab into the visible range.
    private void ScrollFocusedTabIntoView()
    {
        if (_focusedTabButton is not { Bounds.Width: > 0 } tab)
            return;

        var left = tab.Bounds.Left;
        var right = tab.Bounds.Right;
        var viewLeft = _tabScroller.Offset.X;
        var viewRight = viewLeft + _tabScroller.Viewport.Width;

        if (left < viewLeft)
            _tabScroller.Offset = new Vector(Math.Max(0, left), 0);
        else if (right > viewRight && _tabScroller.Viewport.Width > 0)
            _tabScroller.Offset = new Vector(Math.Min(TabScrollable, right - _tabScroller.Viewport.Width), 0);
    }

    // ── tab-strip scrolling (review §7) ─────────────────────────────────────────────────────

    private double TabScrollable => Math.Max(0, _tabScroller.Extent.Width - _tabScroller.Viewport.Width);

    private void ScrollTabs(int dir)
    {
        var x = Math.Clamp(_tabScroller.Offset.X + dir * 120, 0, TabScrollable);
        _tabScroller.Offset = new Vector(x, 0);
    }

    private void OnTabStripWheel(object? sender, PointerWheelEventArgs e)
    {
        var d = e.Delta.Y != 0 ? e.Delta.Y : e.Delta.X;
        if (d == 0 || TabScrollable <= 0.5)
            return;
        _tabScroller.Offset = new Vector(
            Math.Clamp(_tabScroller.Offset.X - Math.Sign(d) * 60, 0, TabScrollable), 0);
        e.Handled = true;
    }

    private void UpdateTabOverflow()
    {
        var overflow = TabScrollable > 0.5;
        _tabLeft.IsVisible = _tabRight.IsVisible = overflow;
        if (!overflow)
            return;
        _tabLeft.IsEnabled = _tabScroller.Offset.X > 0.5;
        _tabRight.IsEnabled = _tabScroller.Offset.X < TabScrollable - 0.5;
    }

    private IBrush Dyn(string key)
        => this.TryFindResource(key, out var v) && v is Color c ? new SolidColorBrush(c)
         : this.TryFindResource(key, out var b) && b is IBrush br ? br
         : Brushes.Transparent;

    private Button ToolButton(string glyph, string tip, Action onClick)
    {
        var b = new Button
        {
            Content = glyph,
            FontSize = 12,
            MinHeight = 0,
            Height = 16,
            Padding = new Thickness(6, 0),
            Background = Brushes.Transparent,
            BorderThickness = default,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(b, tip);
        b.Click += (_, _) => onClick();
        return b;
    }

    public void Dispose()
    {
        _chord?.Dispose();
        if (_wired)
        {
            _sessions.SessionsChanged -= OnSessionsChanged;
            _sessions.FocusedSessionChanged -= OnFocusedChanged;
            _sessions.SessionExited -= OnSessionExited;
            _sessions.ActivationRequested -= OnActivationRequested;
            _workspace.FocusedRepositoryChanged -= OnFocusedRepositoryChanged;
            _theme.ThemeChanged -= OnThemeChanged;
        }
        _terminal.RestartRequested -= OnTerminalRestartRequested;
        _terminal.Teardown();
        _persistDebounce?.Stop();
    }
}
