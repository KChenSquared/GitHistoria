namespace Historia;

/// <summary>
/// The Avalonia shell window (§6.2.1, §7.4): app band on top, the three body regions
/// (sidebar | commit graph | detail) with two user-driven dividers, status bar on the bottom.
/// The service provider is built by <see cref="App"/> and passed in.
/// </summary>
public partial class MainWindow : Window
{
    private readonly IServiceProvider _services = default!;
    private readonly IThemeService? _theme;
    private readonly IKeyboardCommandRouter? _keyboard;
    private readonly IScrollDirectionProbe? _scrollProbe;
    private readonly AtomicFileWriter? _placementWriter;
    private readonly AppBandControl? _appBand;
    private readonly ShortcutsBarControl? _leftShortcuts;
    private readonly ShortcutsBarControl? _rightShortcuts;
    private readonly CommitGraphControl? _graph;
    private readonly TerminalDockControl? _terminalDock;
    private IRepoNetworkActionRunner? _networkRunner;
    private IWorkspaceService? _workspace;
    private IRepositoryStateService? _boundState;
    private bool _forceClose;
    private bool _closingConfirmOpen;

    // Parameterless ctor for the XAML previewer only.
    public MainWindow() : this(null!) { }

    public MainWindow(IServiceProvider services)
    {
        InitializeComponent();

        _services = services;
        if (services is not null)
        {
            _theme = services.GetService<IThemeService>();
            _keyboard = services.GetService<IKeyboardCommandRouter>();
            _scrollProbe = services.GetService<IScrollDirectionProbe>();
            if (_scrollProbe is not null)
                Activated += (_, _) => _scrollProbe.Refresh();
            _placementWriter = new AtomicFileWriter(AppPaths.WindowPlacementFile);
            if (_theme is not null)
                _theme.ThemeChanged += OnThemeChanged;

            _appBand = new AppBandControl();
            AppBandHost.Content = _appBand;
            _appBand.Initialize(services);

            _leftShortcuts = new ShortcutsBarControl(services, ShortcutsBarSide.Left);
            LeftShortcutsHost.Content = _leftShortcuts;
            _leftShortcuts.NaturalWidthChanged += (_, _) => OnShortcutsNaturalWidthChanged();

            _rightShortcuts = new ShortcutsBarControl(services, ShortcutsBarSide.Right);
            RightShortcutsHost.Content = _rightShortcuts;
            _rightShortcuts.NaturalWidthChanged += (_, _) => OnShortcutsNaturalWidthChanged();

            // Eagerly resolve so its constructor subscribes to IRepoActionBus.NetworkActionRequested
            // right away — Fetch/Pull/Push from the shortcuts bar must keep working regardless of
            // which route the detail pane shows (Home, Settings, …), not just while RepositoryView
            // happens to be the mounted view.
            _networkRunner = services.GetRequiredService<IRepoNetworkActionRunner>();

            _graph = new CommitGraphControl();
            CenterHost.Content = _graph;
            _graph.Initialize(services);

            SidebarHost.Content = new RefNavigatorView(services);
            DetailHost.Content = new DetailRouter(services);

            _terminalDock = new TerminalDockControl();
            TerminalDockHost.Content = _terminalDock;
            _terminalDock.Initialize(services);

            _workspace = services.GetService<IWorkspaceService>();
            if (_workspace is not null)
            {
                // The terminal dock follows repo focus on its own (TerminalDockControl
                // subscribes to FocusedRepositoryChanged): active tab tracks the repo, and the
                // dock auto-hides for a repo with no terminal.
                _workspace.FocusedRepositoryChanged += (_, e) =>
                    Dispatcher.UIThread.Post(() => BindCenterDiff(e.New?.State));
                BindCenterDiff(_workspace.Focused?.State);
            }
        }

        RestorePlacement();
        InitBodyLayout();
        OnShortcutsNaturalWidthChanged();

        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        PositionChanged += (_, _) => QueuePlacementSave();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (_theme is not null)
            WindowChromeFor(_theme.Effective);
        ApplyBodyLayout();
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_theme is not null)
            _theme.ThemeChanged -= OnThemeChanged;
        _appBand?.Dispose();
        _leftShortcuts?.Dispose();
        _rightShortcuts?.Dispose();
        _terminalDock?.Dispose();
        _ = _placementWriter?.DisposeAsync();
        base.OnClosed(e);
    }

    private void OnThemeChanged(object? sender, ThemeChangedEventArgs e)
        => Dispatcher.UIThread.Post(() => WindowChromeFor(e.NewTheme));

    // ── Centre panel: graph ↔ diff overlay (§V2-8.1) ──
    private void BindCenterDiff(IRepositoryStateService? state)
    {
        if (ReferenceEquals(state, _boundState))
            return;
        if (_boundState is not null)
            _boundState.OpenDiffChanged -= OnOpenDiffChanged;
        _boundState = state;
        if (_boundState is not null)
            _boundState.OpenDiffChanged += OnOpenDiffChanged;
        ApplyCenter(state?.OpenDiff);
    }

    private void OnOpenDiffChanged(object? sender, OpenDiffChangedEventArgs e)
        => Dispatcher.UIThread.Post(() => ApplyCenter(e.Request));

    private void ApplyCenter(DiffRequest? request)
    {
        if (request is not null && _workspace?.Focused is { } ctx)
            CenterHost.Content = new DiffView(ctx, request, null, _services);
        else
            CenterHost.Content = _graph;
    }

    private void WindowChromeFor(EffectiveTheme theme)
        => Historia.Platform.WindowChrome.Apply(TryGetPlatformHandle()?.Handle ?? IntPtr.Zero, theme);

    // ── Body panel layout (§V3-B/19.24, §3) ──
    //
    // Each divider resizes only the two panels it sits between: the left divider trades width
    // between Left and Centre, the right divider between Centre and Right. The panel on the far
    // side never moves during a drag, so the adjacent panel responds 1:1 with the pointer and
    // the drag simply stops when the shrinking panel reaches its minimum (CentreMin for the
    // centre). Pane minimums are derived dynamically from the shortcut bars' natural width via
    // BodyColumnLayout.EffectiveMin, and ResolveColumns delegates to BodyColumnLayout.Resolve.

    private const double DividerPx = 4;
    private const double BaseLeftMin = 250;
    // §V3.1: floor the centre panel at the default columns' combined minimum plus padding
    // (Refs 140 + Graph 150 + Message 150 + 40), so a sidebar-divider drag can't squeeze
    // Message past the right edge. Both dividers clamp against it (§4.2.9).
    private const double CentreMin = 480;
    private const double BaseRightMin = 250;

    private double _leftMin = BaseLeftMin;
    private double _rightMin = BaseRightMin;

    public double LeftMin => _leftMin;
    public double RightMin => _rightMin;

    private double _leftW = 264;
    private double _rightW = 430;

    // Avalonia's XAML name generator does not emit fields for ColumnDefinition; index instead.
    private ColumnDefinition LeftCol => BodyGrid.ColumnDefinitions[0];
    private ColumnDefinition CenterCol => BodyGrid.ColumnDefinitions[2];
    private ColumnDefinition RightCol => BodyGrid.ColumnDefinitions[4];

    private bool _dividerDragging;
    private double _dragPointerStartX;
    private double _dragStartLeft, _dragStartRight;

    private void OnShortcutsNaturalWidthChanged()
    {
        var leftBarWidth = _leftShortcuts?.NaturalWidth ?? 0;
        var rightBarWidth = _rightShortcuts?.NaturalWidth ?? 0;
        _leftMin = BodyColumnLayout.EffectiveMin(BaseLeftMin, leftBarWidth);
        _rightMin = BodyColumnLayout.EffectiveMin(BaseRightMin, rightBarWidth);

        _leftW = Math.Max(_leftW, _leftMin);
        _rightW = Math.Max(_rightW, _rightMin);

        MinWidth = _leftMin + _rightMin + 2 * DividerPx;
        ApplyBodyLayout();
    }

    private void InitBodyLayout()
    {
        BodyGrid.PropertyChanged += (_, ev) =>
        {
            if (ev.Property == BoundsProperty)
                ApplyBodyLayout();
        };
    }

    private double BodyContentWidth => Math.Max(0, BodyGrid.Bounds.Width - 2 * DividerPx);

    private (double Left, double Centre, double Right) ResolveColumns(double total)
        => BodyColumnLayout.Resolve(total, _leftW, _rightW, _leftMin, _rightMin);

    internal void SetIntents(double left, double right)
    {
        _leftW = left;
        _rightW = right;
        ApplyBodyLayout();
    }

    internal void ApplyBodyLayout()
    {
        var total = BodyContentWidth;
        if (total <= 0)
            return;

        var (left, centre, right) = ResolveColumns(total);
        LeftCol.Width = new GridLength(left);
        CenterCol.Width = new GridLength(centre);
        RightCol.Width = new GridLength(right);
    }

    // Divider drags are driven off the raw pointer position in BodyGrid's (stationary)
    // coordinate space — not accumulated DragDelta vectors, which drift once the divider
    // clamps and leave a "stuck, then jump to catch up" dead zone on the way back.
    private void OnDividerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(BodyGrid).Properties.IsLeftButtonPressed)
            return;
        if (!CenterCol.Width.IsAbsolute)
            ApplyBodyLayout(); // first drag before any Bounds change — pin the star columns
        _dividerDragging = true;
        _dragPointerStartX = e.GetPosition(BodyGrid).X;
        _dragStartLeft = LeftCol.Width.Value;
        _dragStartRight = RightCol.Width.Value;
        e.Pointer.Capture(sender as IInputElement);
        e.Handled = true;
    }

    // Left divider: trade width between Left and Centre only. Right panel is fixed for the drag.
    private void OnLeftDividerMoved(object? sender, PointerEventArgs e)
    {
        if (!_dividerDragging)
            return;
        var dx = e.GetPosition(BodyGrid).X - _dragPointerStartX;
        var maxLeft = Math.Max(_leftMin, BodyContentWidth - _dragStartRight - CentreMin);
        _leftW = Math.Clamp(_dragStartLeft + dx, _leftMin, maxLeft);
        _rightW = _dragStartRight;
        ApplyBodyLayout();
        e.Handled = true;
    }

    // Right divider: trade width between Centre and Right only. Left panel is fixed for the drag.
    private void OnRightDividerMoved(object? sender, PointerEventArgs e)
    {
        if (!_dividerDragging)
            return;
        var dx = e.GetPosition(BodyGrid).X - _dragPointerStartX;
        var maxRight = Math.Max(_rightMin, BodyContentWidth - _dragStartLeft - CentreMin);
        _rightW = Math.Clamp(_dragStartRight - dx, _rightMin, maxRight);
        _leftW = _dragStartLeft;
        ApplyBodyLayout();
        e.Handled = true;
    }

    private void OnDividerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _dividerDragging = false;
        e.Pointer.Capture(null);
    }

    private void OnDividerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
        => _dividerDragging = false;

    // ── Keyboard accelerators (§6.15.2) — same chord grammar as the WPF shell ──

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (_keyboard is null)
            return;
        if (e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
            or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin)
            return;

        var parts = new List<string>(4);
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control)) parts.Add("Ctrl");
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) parts.Add("Shift");
        if (e.KeyModifiers.HasFlag(KeyModifiers.Alt)) parts.Add("Alt");

        var key = e.Key == Key.Return ? "Enter" : e.Key.ToString();
        parts.Add(key.Length == 1 ? key.ToUpperInvariant() : key);

        var chord = string.Join('+', parts);

        // Decide synchronously and mark the key handled *before* dispatching — a handler that
        // yields (e.g. CommitAsync) would otherwise leave e.Handled unset until long after this
        // routed-event walk has finished, letting the keystroke fall through into a focused
        // TextBox (§findings: async chord swallow bug).
        if (!_keyboard.IsRegistered(chord))
            return;

        e.Handled = true;
        _ = DispatchAndLogAsync(chord);
    }

    private async Task DispatchAndLogAsync(string chord)
    {
        try
        {
            await _keyboard!.DispatchAsync(chord).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLog.Write(AppLogLevel.Warn, "keyboard", $"chord handler for '{chord}' threw", ex);
        }
    }

    // ── Window geometry (§V3-9.2) ──

    private void QueuePlacementSave()
    {
        if (_placementWriter is null || !IsLoaded)
            return;

        var maximized = WindowState == WindowState.Maximized;
        var placement = new WindowPlacement
        {
            Left = Position.X,
            Top = Position.Y,
            Width = maximized ? Width : Bounds.Width,
            Height = maximized ? Height : Bounds.Height,
            Maximized = maximized,
        };
        if (placement.Width <= 0 || placement.Height <= 0)
            return;

        try { _placementWriter.QueueWrite(JsonSerializer.Serialize(placement, HistoriaJson.Options)); }
        catch { /* best effort */ }
    }

    private void RestorePlacement()
    {
        WindowPlacement? p = null;
        try
        {
            var file = AppPaths.WindowPlacementFile;
            if (File.Exists(file))
                p = JsonSerializer.Deserialize<WindowPlacement>(File.ReadAllText(file), HistoriaJson.Options);
        }
        catch { p = null; }

        if (p is null)
            return;

        WindowStartupLocation = WindowStartupLocation.Manual;
        Position = new PixelPoint((int)p.Left, (int)p.Top);
        Width = Math.Clamp(p.Width, 900, 10000);
        Height = Math.Clamp(p.Height, 600, 10000);
        if (p.Maximized)
            WindowState = WindowState.Maximized;

        // Re-centre if the saved spot is fully off every connected screen.
        Opened += (_, _) =>
        {
            var onScreen = Screens?.All.Any(s => s.Bounds.Intersects(
                new PixelRect(Position, PixelSize.FromSize(new Size(Bounds.Width, 40), 1.0)))) ?? true;
            if (!onScreen)
            {
                var screen = Screens?.Primary ?? Screens?.All.FirstOrDefault();
                if (screen is not null)
                {
                    var workArea = screen.WorkingArea;
                    var x = workArea.X + Math.Max(0, (workArea.Width - (int)Bounds.Width) / 2);
                    var y = workArea.Y + Math.Max(0, (workArea.Height - (int)Bounds.Height) / 2);
                    Position = new PixelPoint(x, y);
                }
                else
                {
                    WindowStartupLocation = WindowStartupLocation.CenterScreen;
                }
            }
        };
    }

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        Historia.Diagnostics.PerfLog.Mark("MainWindow.OnClosing start");
        base.OnClosing(e);
        if (_forceClose)
        {
            using (Historia.Diagnostics.PerfLog.Scope("MainWindow.OnClosing terminalDock.Dispose"))
                _terminalDock?.Dispose();
            return;
        }

        if (_closingConfirmOpen)
        {
            e.Cancel = true;
            return;
        }

        var termSessions = _services?.GetService<Historia.Services.Terminal.ITerminalSessionService>();
        var dialogs = _services?.GetService<Historia.Services.UI.IDialogService>();
        if (termSessions is { Sessions.Count: > 0 } && dialogs is not null)
        {
            e.Cancel = true;
            _closingConfirmOpen = true;
            try
            {
                var count = termSessions.Sessions.Count;
                var confirmed = await dialogs.ConfirmDestructiveAsync(
                    "Quit Historia?",
                    $"There {(count == 1 ? "is 1 live terminal session" : $"are {count} live terminal sessions")}. Quitting will terminate {(count == 1 ? "it" : "them")} and anything running inside.",
                    "Quit");
                if (confirmed)
                {
                    _forceClose = true;
                    Close();
                }
            }
            catch
            {
                _forceClose = true;
                Close();
            }
            finally
            {
                _closingConfirmOpen = false;
            }
            return;
        }

        using (Historia.Diagnostics.PerfLog.Scope("MainWindow.OnClosing terminalDock.Dispose"))
            _terminalDock?.Dispose();
    }
}
