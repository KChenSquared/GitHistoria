namespace Historia.Views;

/// <summary>
/// The window's app band (row 0): the branded Historia menu button plus the repository tab strip.
/// The tabs and menu entries are markup bound to an <see cref="AppBandViewModel"/>; the code here
/// is view mechanics: the overflow arrows and multi-row height, the add button's placement, the
/// wheel panning, the menu flyout and the tab drag (pointer gesture, edge-scroll timer, ghost),
/// which previews and saves the new order through the view model.
/// </summary>
public sealed partial class AppBandView : UserControl, IDisposable
{
    private const double RowHeight = 31;

    // The pre-layout fallback for overflow maths and drag reorder.
    private const double TabWidth = 186;

    // The "+" button's width plus its margins.
    private const double AddButtonWidth = 28;
    private const double DragThreshold = 4;
    private const double EdgeScrollZone = 28;
    private const double EdgeScrollStep = 14;

    private RepoTabViewModel? _pressTab;
    private Border? _pressShell;
    private Point _pressPanelPos;
    private Point _grabOffset;
    private Point _lastPanelPos;
    private bool _dragging;
    private int _edgeDir;
    private DispatcherTimer? _edgeTimer;
    private AppBandDragGhost? _dragGhost;

    // The laid-out width of every tab, captured when the drag starts: a tab swap re-creates the
    // containers, whose bounds stay empty until the next layout pass.
    private readonly Dictionary<AppBandItemViewModel, double> _dragWidths = [];

    // Reused across the per-pointer-move reorders so the hot drag path stays allocation-free.
    private readonly List<double> _widths = [];

    private bool _inUpdateOverflow;
    private bool _overflowing;
    private bool _disposed;

    public AppBandView()
    {
        InitializeComponent();

        // The drag gesture is captured to the strip, whose children the view model reorders
        // freely during a live shuffle without dropping the pointer.
        TabStrip.AddHandler(PointerPressedEvent, OnStripPointerPressed, RoutingStrategies.Tunnel);
        TabStrip.AddHandler(PointerMovedEvent, OnStripPointerMoved, RoutingStrategies.Bubble);
        TabStrip.AddHandler(PointerReleasedEvent, OnStripPointerReleased, RoutingStrategies.Bubble);
        TabStrip.PointerCaptureLost += OnStripCaptureLost;

        Scroller.PropertyChanged += (_, e) =>
        {
            if (e.Property == BoundsProperty)
                UpdateOverflow();
        };
        Scroller.AddHandler(PointerWheelChangedEvent, OnScrollerWheel, RoutingStrategies.Tunnel);
    }

    internal AppBandViewModel? ViewModel { get; private set; }

    /// <summary>Creates the view model and starts following the workspace. Safe to call once.</summary>
    public void Initialize(IServiceProvider services)
    {
        if (ViewModel is not null)
            return;

        var theme = services.GetRequiredService<IThemeService>();
        ViewKit.BindThemedMark(BrandMark, theme);

        var viewModel = ActivatorUtilities.CreateInstance<AppBandViewModel>(
            services, new UiDispatcher(), new ClipboardService(), new HostPlatform(theme));
        viewModel.TabsRebuilt += OnTabsRebuilt;
        viewModel.TabSettingsChanged += OnTabSettingsChanged;
        ViewModel = viewModel;
        DataContext = viewModel;

        ApplyTabStripMode();
        QueueTabLayout();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        StopEdgeTimer();
        StopDragGhost();
        if (ViewModel is { } viewModel)
        {
            viewModel.TabsRebuilt -= OnTabsRebuilt;
            viewModel.TabSettingsChanged -= OnTabSettingsChanged;
            viewModel.Dispose();
        }
    }

    // ── menus and brand ─────────────────────────────────────────────────────────────────────

    private void OnHamburgerClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } viewModel)
            return;

        viewModel.PrepareAppMenu();
        new MenuFlyout
        {
            Placement = PlacementMode.BottomEdgeAlignedLeft,
            ItemsSource = BuildMenuItems(viewModel.AppMenu),
        }.ShowAt(Hamburger);
    }

    private static List<Control> BuildMenuItems(IReadOnlyList<AppMenuEntry> entries)
    {
        var items = new List<Control>(entries.Count);
        foreach (var entry in entries)
        {
            if (entry.IsSeparator)
            {
                items.Add(new Separator());
                continue;
            }

            var item = new MenuItem
            {
                Header = entry.Header,
                Icon = entry.IsChecked ? new TextBlock { Text = "✓" } : null,
                Command = entry.Command,
            };
            if (entry.HasChildren)
                item.ItemsSource = BuildMenuItems(entry.Children);
            items.Add(item);
        }
        return items;
    }

    private void OnBrandPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Visual visual && e.GetCurrentPoint(visual).Properties.IsLeftButtonPressed)
            ViewModel?.NavigateCommand.Execute("");
    }

    // ── tab click ───────────────────────────────────────────────────────────────────────────

    private void OnTabReleased(object? sender, PointerReleasedEventArgs e)
    {
        if ((sender as StyledElement)?.DataContext is not RepoTabViewModel tab)
            return;

        if (e.InitialPressMouseButton == MouseButton.Left && !_dragging)
        {
            tab.ActivateCommand.Execute(null);
        }
        else if (e.InitialPressMouseButton == MouseButton.Middle)
        {
            e.Handled = true;
            tab.CloseCommand.Execute(null);
        }
    }

    // ── tab strip layout ────────────────────────────────────────────────────────────────────

    private Visual StripSpace => (Visual?)TabStrip.ItemsPanelRoot ?? TabStrip;

    private bool MultiLine => ViewModel?.IsMultiLine ?? false;

    private double ScrollableWidth => Math.Max(0, Scroller.Extent.Width - Scroller.Viewport.Width);

    private double ScrollableHeight => Math.Max(0, Scroller.Extent.Height - Scroller.Viewport.Height);

    private void SetHorizontalOffset(double x)
        => Scroller.Offset = new Vector(Math.Clamp(x, 0, ScrollableWidth), Scroller.Offset.Y);

    private void SetVerticalOffset(double y)
        => Scroller.Offset = new Vector(Scroller.Offset.X, Math.Clamp(y, 0, ScrollableHeight));

    private void OnTabSettingsChanged(object? sender, EventArgs e) => ApplyTabStripMode();

    // Tabs are variable-width, so the overflow estimate made on a rebuild ran before any new tab
    // was measured. Re-check once the strip has laid out; this also keeps the focused tab on
    // screen, for example a repository opened by a forwarded second launch lands at the far right.
    private void OnTabsRebuilt(object? sender, EventArgs e) => QueueTabLayout();

    private void QueueTabLayout()
    {
        UpdateOverflow();
        Dispatcher.UIThread.Post(() =>
        {
            UpdateOverflow();
            ActiveTabContainer()?.BringIntoView();
        }, DispatcherPriority.Loaded);
    }

    private Control? ActiveTabContainer()
    {
        if (ViewModel is not { } viewModel)
            return null;
        var tabs = viewModel.Tabs;
        for (var i = 0; i < tabs.Count; i++)
        {
            if (tabs[i] is RepoTabViewModel { IsActive: true })
                return TabStrip.ContainerFromIndex(i);
        }
        return null;
    }

    private void ApplyTabStripMode()
    {
        var multi = MultiLine;
        Scroller.HorizontalScrollBarVisibility = multi ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Hidden;
        Scroller.VerticalScrollBarVisibility = multi ? ScrollBarVisibility.Hidden : ScrollBarVisibility.Disabled;
        LeftArrow.VerticalAlignment = RightArrow.VerticalAlignment =
            multi ? VerticalAlignment.Bottom : VerticalAlignment.Center;
        Scroller.Offset = default;
        UpdateOverflow();
    }

    private void OnScrollerScrollChanged(object? sender, ScrollChangedEventArgs e) => UpdateOverflow();

    private void OnLeftArrowClick(object? sender, RoutedEventArgs e) => PageStrip(-1);

    private void OnRightArrowClick(object? sender, RoutedEventArgs e) => PageStrip(+1);

    private void PageStrip(int dir)
    {
        if (MultiLine)
            SetVerticalOffset(Scroller.Offset.Y + dir * RowHeight);
        else
            SetHorizontalOffset(Scroller.Offset.X + dir * TabWidth);
    }

    // Wheel over the strip pans it: down the rows in multi-line mode, along the row (left / right)
    // in single-row mode. Tilt-wheel / trackpad X is a fallback.
    private void OnScrollerWheel(object? sender, PointerWheelEventArgs e)
    {
        var d = e.Delta.Y != 0 ? e.Delta.Y : e.Delta.X;
        if (d == 0)
            return;
        d *= ViewModel?.WheelSign() ?? 1;

        if (MultiLine)
        {
            if (ScrollableHeight <= 0.5)
                return;
            SetVerticalOffset(Scroller.Offset.Y - Math.Sign(d) * RowHeight);
        }
        else
        {
            if (ScrollableWidth <= 0.5)
                return;
            SetHorizontalOffset(Scroller.Offset.X - Math.Sign(d) * TabWidth);
        }
        e.Handled = true;
    }

    private void SetBandRows(int rows)
    {
        var h = Math.Max(1, rows) * RowHeight + 1;
        if (Math.Abs(Height - h) > 0.5)
            Height = h;
    }

    private void UpdateOverflow()
    {
        if (_inUpdateOverflow)
            return;
        _inUpdateOverflow = true;
        try
        {
            var viewport = Scroller.Viewport.Width;
            if (viewport <= 0)
            {
                PlaceAddButton(overflow: false);
                SetBandRows(1);
                return;
            }

            // Tabs may be variable-width (dynamic mode), so sum their laid-out widths (each
            // shell's bounds already include its 1 px left separator). Fall back to the best
            // per-mode estimate for any tab that has not been measured yet.
            var fixedWidth = ViewModel?.IsFixedTabWidth ?? false;
            var fallback = fixedWidth ? ViewModel!.FixedTabWidth : TabWidth;
            var tabCount = ViewModel?.Tabs.Count ?? 0;
            double tabsWidth = 0;
            for (var i = 0; i < tabCount; i++)
            {
                var width = TabStrip.ContainerFromIndex(i)?.Bounds.Width ?? 0;
                tabsWidth += width > 0.5 ? width : fallback;
            }
            var addWidth = AddTileContainer() is { Bounds.Width: > 0 } tile ? tile.Bounds.Width : AddButtonWidth;

            bool overflow;
            if (!MultiLine)
            {
                overflow = tabsWidth > viewport + 0.5;
                SetBandRows(1);
            }
            else
            {
                var maxRows = ViewModel?.MaxRows ?? 3;
                var rowsNeeded = Math.Max(1, (int)Math.Ceiling((tabsWidth + addWidth) / viewport - 1e-6));
                overflow = rowsNeeded > maxRows;
                SetBandRows(Math.Min(rowsNeeded, maxRows));
            }

            SetArrowVisible(LeftArrow, overflow);
            SetArrowVisible(RightArrow, overflow);

            if (MultiLine)
            {
                LeftArrow.IsEnabled = Scroller.Offset.Y > 0.5;
                RightArrow.IsEnabled = Scroller.Offset.Y < ScrollableHeight - 0.5;
            }
            else
            {
                LeftArrow.IsEnabled = Scroller.Offset.X > 0.5;
                RightArrow.IsEnabled = Scroller.Offset.X < ScrollableWidth - 0.5;
            }

            PlaceAddButton(overflow);
        }
        finally
        {
            _inUpdateOverflow = false;
        }
    }

    // Avalonia has no "Hidden": keep the layout slot but stay inert when not overflowing.
    // When shown, clear the local Opacity so the `.flat` / `.flat:disabled` styles decide it
    // (a disabled arrow reads as a faint ghost).
    private static void SetArrowVisible(RepeatButton b, bool shown)
    {
        b.IsHitTestVisible = shown;
        if (shown)
            b.ClearValue(Visual.OpacityProperty);
        else
            b.Opacity = 0;
    }

    // While the strip overflows the "+" leaves the end of the strip (where it follows the last
    // tab and wraps with the tabs) for a button beside the arrows.
    private void PlaceAddButton(bool overflow)
    {
        _overflowing = overflow;
        AddButton.IsVisible = overflow;
        AddButton.VerticalAlignment = MultiLine ? VerticalAlignment.Bottom : VerticalAlignment.Center;
        if (AddTileContainer() is { } tile)
            tile.IsVisible = !overflow;
    }

    private Control? AddTileContainer()
        => ViewModel is { } viewModel ? TabStrip.ContainerFromIndex(viewModel.Tabs.Count) : null;

    private void OnContainerPrepared(object? sender, ContainerPreparedEventArgs e)
    {
        if (ViewModel is { } viewModel && e.Index == viewModel.StripItems.Count - 1)
            e.Container.IsVisible = !_overflowing;
    }

    // ── tab drag-and-drop ───────────────────────────────────────────────────────────────────

    // The tab's container in the strip: the presenter that is a direct child of the tab panel.
    private Avalonia.Controls.Presenters.ContentPresenter? ContainerOf(Visual? start)
    {
        var panel = TabStrip.ItemsPanelRoot;
        for (var visual = start; visual is not null; visual = visual.GetVisualParent())
        {
            if (visual is Avalonia.Controls.Presenters.ContentPresenter presenter && ReferenceEquals(presenter.GetVisualParent(), panel))
                return presenter;
        }
        return null;
    }

    private void OnStripPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if ((e.Source as Visual)?.FindAncestorOfType<Button>(includeSelf: true) is not null)
            return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        if (ContainerOf(e.Source as Visual) is not { DataContext: RepoTabViewModel tab, Child: Border shell })
            return;

        _pressTab = tab;
        _pressShell = shell;
        _pressPanelPos = e.GetPosition(StripSpace);
        _grabOffset = e.GetPosition(shell);
    }

    private void OnStripPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_pressTab is null || ViewModel is not { } viewModel || !e.GetCurrentPoint(StripSpace).Properties.IsLeftButtonPressed)
            return;

        var panelPos = e.GetPosition(StripSpace);
        _lastPanelPos = panelPos;
        if (!_dragging)
        {
            if (!AppBandDragReorder.PassesThreshold(_pressPanelPos.X, panelPos.X, DragThreshold))
                return;

            // The ghost is drawn from the tab as it looks before the view model dims it.
            StartDragGhost(_pressShell);
            if (!viewModel.BeginDrag(_pressTab))
            {
                ClearPress();
                StopDragGhost();
                return;
            }
            _dragging = true;
            CaptureTabWidths(viewModel);
            e.Pointer.Capture(TabStrip);
            StartEdgeTimer();
        }

        ReorderTo(panelPos.X);
        var here = e.GetPosition(this);
        _dragGhost?.MoveTo(new Point(here.X - _grabOffset.X, here.Y - _grabOffset.Y));

        _edgeDir = AppBandDragReorder.EdgeDirection(
            e.GetPosition(Scroller).X, Scroller.Viewport.Width, EdgeScrollZone);
    }

    // Capture lost without a normal drop (a dialog, Alt+Tab, …): abandon the drag and resync.
    private void OnStripCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (!_dragging && _pressTab is null)
            return;
        ClearPress();
        StopEdgeTimer();
        StopDragGhost();
        ViewModel?.CancelDrag();
    }

    private void OnStripPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_pressTab is null)
            return;

        var wasDragging = _dragging;
        ClearPress();
        StopEdgeTimer();
        StopDragGhost();
        e.Pointer.Capture(null);

        if (!wasDragging)
            return;
        e.Handled = true;
        ViewModel?.CompleteDrag();
    }

    private void ClearPress()
    {
        _pressTab = null;
        _pressShell = null;
        _dragging = false;
    }

    private void CaptureTabWidths(AppBandViewModel viewModel)
    {
        _dragWidths.Clear();
        var tabs = viewModel.Tabs;
        for (var i = 0; i < tabs.Count; i++)
            _dragWidths[tabs[i]] = TabStrip.ContainerFromIndex(i)?.Bounds.Width ?? 0;
    }

    // Live shuffle: the view model moves the dragged tab (or its whole group) to the slot under
    // the cursor. It is only the preview; the drop saves the order through the workspace.
    private void ReorderTo(double panelX)
    {
        if (ViewModel is not { } viewModel)
            return;

        var tabs = viewModel.Tabs;
        _widths.Clear();
        for (var i = 0; i < tabs.Count; i++)
            _widths.Add(_dragWidths.GetValueOrDefault(tabs[i]));
        viewModel.ReorderTo(panelX, _widths, TabWidth);
    }

    private void StartEdgeTimer()
    {
        _edgeTimer ??= new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(16) };
        _edgeTimer.Tick -= OnEdgeTick;
        _edgeTimer.Tick += OnEdgeTick;
        _edgeTimer.Start();
    }

    private void StopEdgeTimer()
    {
        _edgeTimer?.Stop();
        _edgeDir = 0;
    }

    private void OnEdgeTick(object? sender, EventArgs e)
    {
        if (_edgeDir == 0 || _pressTab is null)
            return;
        var next = Math.Clamp(Scroller.Offset.X + EdgeScrollStep * _edgeDir, 0, ScrollableWidth);
        if (Math.Abs(next - Scroller.Offset.X) < 0.5)
            return;
        SetHorizontalOffset(next);
        ReorderTo(_lastPanelPos.X);
    }

    private void StartDragGhost(Border? shell)
    {
        StopDragGhost();
        var layer = AdornerLayer.GetAdornerLayer(this);
        if (shell is null || layer is null || shell.Bounds.Width <= 0 || shell.Bounds.Height <= 0)
            return;

        var size = new PixelSize(
            Math.Max(1, (int)Math.Ceiling(shell.Bounds.Width)),
            Math.Max(1, (int)Math.Ceiling(shell.Bounds.Height)));
        var rtb = new RenderTargetBitmap(size);
        rtb.Render(shell);

        _dragGhost = new AppBandDragGhost(rtb, shell.Bounds.Size);
        layer.Children.Add(_dragGhost);
    }

    private void StopDragGhost()
    {
        if (_dragGhost is not null)
        {
            (_dragGhost.Parent as Panel)?.Children.Remove(_dragGhost);
            AdornerLayer.GetAdornerLayer(this)?.Children.Remove(_dragGhost);
            _dragGhost.Dispose();
        }
        _dragGhost = null;
    }

    // ── host seams ──────────────────────────────────────────────────────────────────────────

    // What the app band needs from the application that Core cannot reach.
    private sealed class HostPlatform(IThemeService theme) : IAppBandPlatform
    {
        public bool DeveloperMenuEnabled => DevMode.Enabled;

        public bool AutoFetchBarsVisible => AutoFetchDebug.Enabled;

        public event Action? AutoFetchBarsVisibleChanged
        {
            add => AutoFetchDebug.Changed += value;
            remove => AutoFetchDebug.Changed -= value;
        }

        public void ShowAbout() => _ = ShowAboutAsync();

        public void Exit()
            => (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.TryShutdown();

        private async Task ShowAboutAsync()
        {
            try
            {
                await AboutDialog.ShowAsync(theme);
            }
            catch (Exception ex)
            {
                AppLog.Write(AppLogLevel.Warn, "AppBand", "Showing the About window failed.", ex);
            }
        }
    }
}
