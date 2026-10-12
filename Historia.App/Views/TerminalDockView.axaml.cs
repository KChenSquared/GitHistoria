namespace Historia.Views;

/// <summary>
/// The bottom terminal dock. The tab strip, toolbar and open / height state are markup bound to a
/// <see cref="TerminalDockViewModel"/>; the code here is view mechanics: the splitter drag, the
/// scrollbar mirrored onto the terminal's scrollback, the tab strip's overflow scrolling, the
/// session shown by the terminal control and the OSC 52 clipboard sink.
/// </summary>
public sealed partial class TerminalDockView : UserControl, IDisposable
{
    private const double TabScrollStep = 120;
    private const double TabWheelStep = 60;

    private ITerminalSessionService? _sessions;
    private bool _syncingScroll;

    public TerminalDockView()
    {
        InitializeComponent();

        TabScroller.AddHandler(PointerWheelChangedEvent, OnTabStripWheel, RoutingStrategies.Tunnel);
        TabScroller.PropertyChanged += (_, e) =>
        {
            if (e.Property == BoundsProperty)
                UpdateTabOverflow();
        };
        Terminal.ScrollMetricsChanged += (_, _) => SyncScrollBar();
    }

    internal TerminalDockViewModel? ViewModel { get; private set; }

    /// <summary>Creates the view model and starts following the session service. Safe to call once.</summary>
    public void Initialize(IServiceProvider services)
    {
        if (ViewModel is not null)
            return;

        _sessions = services.GetRequiredService<ITerminalSessionService>();
        Terminal.Attach(services);
        Terminal.RestartRequested += OnTerminalRestartRequested;

        var viewModel = new TerminalDockViewModel(
            services.GetRequiredService<IUiDispatcher>(),
            _sessions,
            services.GetRequiredService<IWorkspaceService>(),
            services.GetRequiredService<ISettingsService>(),
            services.GetRequiredService<IKeyboardCommandRouter>())
        {
            HostHeightProvider = CentrePanelHeight,
        };
        viewModel.FocusTerminalRequested += OnFocusTerminalRequested;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        ViewModel = viewModel;
        DataContext = viewModel;

        Terminal.SetSession(viewModel.ActiveSession);
        if (this.IsAttachedToVisualTree())
            AttachClipboardSink(_sessions);
        QueueTabLayout();
    }

    /// <summary>The terminal shortcut: closes an open dock, or opens it and focuses the terminal.</summary>
    public void ToggleFromKeyboard() => ViewModel?.ToggleFromKeyboard();

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(TerminalDockViewModel.ActiveSession):
                Terminal.SetSession(ViewModel?.ActiveSession);
                break;
            case nameof(TerminalDockViewModel.Tabs):
                QueueTabLayout();
                break;
        }
    }

    private void OnFocusTerminalRequested(object? sender, EventArgs e) => Terminal.Focus();

    private void OnTerminalRestartRequested(object? sender, EventArgs e) => ViewModel?.RestartCommand.Execute(null);

    // ── OSC 52 clipboard sink ───────────────────────────────────────────────────────────────

    // The dock only hides (IsVisible = false), it never leaves the tree, so a background
    // session's write still finds the TopLevel while the dock is closed.
    private ITerminalSessionService? _clipboardSource;

    internal void AttachClipboardSink(ITerminalSessionService sessions)
    {
        DetachClipboardSink();
        _clipboardSource = sessions;
        sessions.ClipboardWriteRequested += OnClipboardWriteRequested;
    }

    private void DetachClipboardSink()
    {
        if (_clipboardSource is null)
            return;
        _clipboardSource.ClipboardWriteRequested -= OnClipboardWriteRequested;
        _clipboardSource = null;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_sessions is not null && ViewModel is not null && _clipboardSource is null)
            AttachClipboardSink(_sessions);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        DetachClipboardSink();
    }

    // Write-only and silent: the service already applied the user's setting.
    private void OnClipboardWriteRequested(object? sender, TerminalClipboardWriteEventArgs e)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            _ = SetClipboardTextAsync(clipboard, e.Text);
    }

    private static async Task SetClipboardTextAsync(Avalonia.Input.Platform.IClipboard clipboard, string text)
    {
        try
        {
            await clipboard.SetTextAsync(text);
        }
        catch (Exception ex)
        {
            AppLog.Write(AppLogLevel.Warn, "terminal", "OSC 52 clipboard write failed", ex);
        }
    }

    // ── splitter ────────────────────────────────────────────────────────────────────────────

    /// <summary>Height of the centre column (graph area + this dock). The nearest <see cref="Grid"/>
    /// ancestor is the centre wrapper grid from <c>MainWindow.axaml</c>; the immediate visual
    /// parent is just a content presenter sized to the dock itself, so clamping against it would
    /// pin the dock to its current height and block growing it.</summary>
    private double CentrePanelHeight()
        => this.FindAncestorOfType<Grid>()?.Bounds.Height
           ?? TopLevel.GetTopLevel(this)?.Bounds.Height
           ?? 0;

    private void OnSplitterDrag(object? sender, VectorEventArgs e) => ViewModel?.DragBy(e.Vector.Y);

    private void OnSplitterDragCompleted(object? sender, VectorEventArgs e) => ViewModel?.CompleteDrag();

    // ── terminal scrollbar ─────────────────────────────────────────────────────────────────

    // TerminalControl owns the scroll offset; mirror its metrics onto the ScrollBar, and feed
    // user drags back. `_syncingScroll` breaks the metrics -> bar -> metrics echo.
    private void SyncScrollBar()
    {
        var m = Terminal.CurrentScrollMetrics;
        var scrollable = m.Max > 0 && m.Viewport > 0;
        VScroll.IsVisible = scrollable;
        if (!scrollable)
            return;

        _syncingScroll = true;
        VScroll.Maximum = m.Max;
        VScroll.ViewportSize = m.Viewport;
        VScroll.LargeChange = Math.Max(1, m.Viewport - 1);
        VScroll.SmallChange = 1;
        VScroll.Value = Math.Clamp(m.Value, 0, m.Max);
        _syncingScroll = false;
    }

    private void OnVScrollScroll(object? sender, ScrollEventArgs e)
    {
        if (_syncingScroll)
            return;
        Terminal.SetScrollValue((int)Math.Round(e.NewValue));
    }

    // ── tab strip layout ────────────────────────────────────────────────────────────────────

    // Once the new tabs have been laid out, the strip updates its overflow arrows and brings the
    // active terminal's tab into view.
    private void QueueTabLayout()
    {
        Dispatcher.UIThread.Post(() =>
        {
            UpdateTabOverflow();
            ScrollActiveTabIntoView();
        }, DispatcherPriority.Loaded);
    }

    // The active terminal's tab should always be reachable without manual scrolling: when the
    // strip overflows and focus moves to a session whose tab is off-screen (session switched via
    // keyboard, a new session opened, one closed), bring its tab into the visible range.
    private void ScrollActiveTabIntoView()
    {
        if (ViewModel is not { } viewModel)
            return;
        var index = -1;
        for (var i = 0; i < viewModel.Tabs.Count; i++)
        {
            if (viewModel.Tabs[i].IsActive)
            {
                index = i;
                break;
            }
        }
        if (index < 0 || TabStrip.ContainerFromIndex(index) is not { Bounds.Width: > 0 } tab)
            return;

        var left = tab.Bounds.Left;
        var right = tab.Bounds.Right;
        var viewLeft = TabScroller.Offset.X;
        var viewRight = viewLeft + TabScroller.Viewport.Width;

        if (left < viewLeft)
            TabScroller.Offset = new Vector(Math.Max(0, left), 0);
        else if (right > viewRight && TabScroller.Viewport.Width > 0)
            TabScroller.Offset = new Vector(Math.Min(TabScrollable, right - TabScroller.Viewport.Width), 0);
    }

    private double TabScrollable => Math.Max(0, TabScroller.Extent.Width - TabScroller.Viewport.Width);

    private void OnTabLeftClick(object? sender, RoutedEventArgs e) => ScrollTabs(-1);

    private void OnTabRightClick(object? sender, RoutedEventArgs e) => ScrollTabs(+1);

    private void ScrollTabs(int dir)
    {
        var x = Math.Clamp(TabScroller.Offset.X + dir * TabScrollStep, 0, TabScrollable);
        TabScroller.Offset = new Vector(x, 0);
    }

    private void OnTabStripWheel(object? sender, PointerWheelEventArgs e)
    {
        var d = e.Delta.Y != 0 ? e.Delta.Y : e.Delta.X;
        if (d == 0 || TabScrollable <= 0.5)
            return;
        TabScroller.Offset = new Vector(
            Math.Clamp(TabScroller.Offset.X - Math.Sign(d) * TabWheelStep, 0, TabScrollable), 0);
        e.Handled = true;
    }

    private void OnTabScrollChanged(object? sender, ScrollChangedEventArgs e) => UpdateTabOverflow();

    private void UpdateTabOverflow()
    {
        var overflow = TabScrollable > 0.5;
        TabLeft.IsVisible = TabRight.IsVisible = overflow;
        if (!overflow)
            return;
        TabLeft.IsEnabled = TabScroller.Offset.X > 0.5;
        TabRight.IsEnabled = TabScroller.Offset.X < TabScrollable - 0.5;
    }

    public void Dispose()
    {
        DetachClipboardSink();
        if (ViewModel is { } viewModel)
        {
            viewModel.FocusTerminalRequested -= OnFocusTerminalRequested;
            viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            viewModel.Dispose();
        }
        Terminal.RestartRequested -= OnTerminalRestartRequested;
        Terminal.Teardown();
    }
}
