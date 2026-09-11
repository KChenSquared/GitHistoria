namespace Historia.Controls;

/// <summary>
/// Native commit graph + history list (§7.4). Avalonia port of the WPF
/// <c>NativeControls.CommitGraphControl</c>. Fully virtualized: only the visible row range is
/// drawn each frame. Selection flows through <see cref="IRepositoryStateService"/> (§7.7).
/// </summary>
public sealed partial class CommitGraphControl : Control
{
    private const double MinRowHeight = 20;
    private const double MaxRowHeight = 44;
    private const double MinLaneWidth = 15; // §V3.1: raised from 13 with the +2px lane width
    private const double MaxLaneWidth = 60;
    /// <summary>§V3.2: commit nodes are round dots again (the §V3.1 diamond was reverted).</summary>
    private const double NodeRadius = 5;
    private const double RowTextSize = 14;
    private const double HeaderTextSize = 12;
    private const double BadgeLabelSize = 14;
    private const double HeaderHeight = 30;
    private const double CogSize = 15;
    private const double QueryBarHeight = 32;
    private const double BoundaryGrab = 4;
    private const double TickWidth = 4;
    /// <summary>§V6-G2: the lane-line stroke width, shared by <see cref="LanePen"/> and the
    /// HEAD badge→node connector.</summary>
    private const double LaneStrokeWidth = 2.0;

    private double _laneWidth = 18; // §V3.1: raised from 16 (+2px)
    private double LaneWidth => _laneWidth;

    private IServiceProvider _services = default!;
    private IWorkspaceService _workspace = default!;
    private IGraphLayoutService _layoutService = default!;
    private IThemeService _theme = default!;
    private IDialogService _dialogs = default!;
    private INotificationService _notifications = default!;
    private Historia.Services.PowerTools.ICherryPickService _cherryPick = default!;
    private Historia.Services.PowerTools.IStashService _stashes = default!;
    private INavigationBus _nav = default!;
    private Historia.Services.Settings.ISettingsService _settings = default!;
    private Historia.Services.UI.IScrollDirectionProbe _scrollProbe = default!;
    private Historia.Services.Settings.IRepoLayoutService _repoLayout = default!;

    // §7.6 takes effect on the next launch, and GraphLayoutService snapshots it at construction.
    // Snapshot it here too so the control's resident-row ceiling always matches the service's —
    // a mid-session toggle must not make the control cap at the 1M disk limit while metadata is
    // still fully resident (the 250k memory limit exists precisely to bound that allocation).
    private bool _graphMetaFromDisk;
    private int ResidentRowCap => GraphLimits.GetMaxResidentRows(_graphMetaFromDisk);

    private IReadOnlyList<GraphColumn> _columns = GraphColumnDefaults.All;
    private bool _graphWidthPinned;

    // ── Reveal-toward-an-unrendered-commit (§7.3) ──
    private ObjectId? _revealTarget;         // commit the pump is walking toward; null = idle
    private GraphDecoration? _revealDeco;    // stand-in badge drawn while walking
    private bool _revealUserTookOver;        // user scrolled/zoomed since the click → stop driving
    private bool _revealPumpRunning;

    /// <summary>While true the graph body is blanked and only the reveal placeholder is drawn.</summary>
    private bool RevealBlanking => _revealTarget is not null && !_revealUserTookOver;
    private readonly HashSet<GraphColumnKind> _manuallySized = [];
    private double _refsBaseWidth = GraphColumnDefaults.RefsStaticFloor;
    private ResolvedColumn[] _resolvedColumns = [];
    private double _bandWidth;
    private double _graphColLeft;
    private double _graphColWidth;

    private int _resizeBoundary = -1;
    private bool _resizing;
    private double _resizeStartX;
    private double _resizeStartWidth;
    private GraphColumnKind _resizeKind;
    private bool _resizeInverted;
    private bool _zoomOverridesPin;

    private GraphColumnKind? _reorderCandidate;
    private GraphColumnKind? _reorderKind;
    private double _reorderGhostX;
    private int _reorderCaret = -1;

    private readonly ScrollBar _scrollBar;
    private readonly ScrollBar _hScrollBar;
    private bool _scrollBarUserActive;

    private RepositoryContext? _context;
    private IRepositoryStateService? _state;
    private GraphLayoutResult? _layout;
    // The commit store behind _layout. A disk-backed store holds an open file handle that
    // GraphLayoutService will close as soon as a RefsChanged re-walk drops its own reference —
    // which used to happen mid-Render and kill the process (ObjectDisposedException off the
    // render thread). We hold an explicit lease for as long as _layout can be rendered from.
    private ICommitStore? _leasedStore;
    // Consecutive Render() failures caught by the transient-fault handler. Reset on any frame
    // that paints cleanly; capped so a *deterministic* fault can't turn "drop the frame and
    // repaint" into an unbounded Background-priority repaint loop.
    private int _renderFailStreak;
    private const int MaxRenderRetries = 8;
    private CancellationTokenSource? _layoutCts;
    private sealed record ExtendRequest(
        RepositoryContext Context,
        int TargetRow,
        int BatchSize,
        TaskCompletionSource? Completion,
        CancellationToken Token);

    private Channel<ExtendRequest>? _extendChannel;
    private int _cappedNotified;

    private double _rowHeight = 26;
    private double _scrollOffset;
    private double _hScrollOffset;

    // §V3.1 in-graph lane pan: scrolls the lanes INSIDE the Graph column without moving any
    // other column, so any lane is reachable while Message stays on screen. In DIP, lane space.
    // Per repo for the session only (reset to lane 0 at launch) — saved on repo switch, restored
    // on Attach, never persisted.
    private double _graphPanOffset;
    private readonly Dictionary<string, double> _panByRepo = new(StringComparer.Ordinal);

    /// <summary>Widest the lanes can be panned: the natural graph width minus what the column
    /// shows. Zero when every lane already fits.</summary>
    private double MaxPan =>
        Math.Max(0, NaturalGraphWidth(_layout?.Layout.TotalLaneCount ?? 0) - _graphColWidth);

    private void ClampPan() => _graphPanOffset = Math.Clamp(_graphPanOffset, 0, MaxPan);

    private int _hoverRow = -1;
    private int _selectedRow = -1;
    private readonly HashSet<int> _multiSelect = [];
    private readonly List<(Rect Rect, int Row, GraphDecoration Deco)> _pillRects = [];
    private readonly List<(Rect Rect, string Tip)> _iconRects = [];

    // v8 M16: LRU cache for commit message bodies, bounded with oldest-25% eviction.
    private readonly Dictionary<ObjectId, string> _msgBodyCache = new();
    private readonly LinkedList<ObjectId> _msgBodyLru = new();
    private const int MsgBodyCacheCapacity = 512;

    private bool TryGetCachedBody(ObjectId id, out string body)
    {
        if (_msgBodyCache.TryGetValue(id, out body!))
        {
            _msgBodyLru.Remove(id);
            _msgBodyLru.AddLast(id);
            return true;
        }
        return false;
    }

    private void CacheMessageBody(ObjectId id, string body)
    {
        if (_msgBodyCache.TryGetValue(id, out _))
        {
            _msgBodyLru.Remove(id);
            _msgBodyLru.AddLast(id);
            _msgBodyCache[id] = body;
            return;
        }

        if (_msgBodyCache.Count >= MsgBodyCacheCapacity)
        {
            var toEvict = MsgBodyCacheCapacity / 4;
            for (var i = 0; i < toEvict && _msgBodyLru.First is { } node; i++)
            {
                _msgBodyCache.Remove(node.Value);
                _msgBodyLru.RemoveFirst();
            }
        }

        _msgBodyCache[id] = body;
        _msgBodyLru.AddLast(id);
    }

    private ObjectId? _hoverMsgId;
    private ObjectId? _msgFetchInFlight;
    private string? _msgTipText;

    private readonly Popup _refList = new()
    {
        Placement = PlacementMode.Pointer,
        IsLightDismissEnabled = false,
    };
    private GraphDecoration? _refListFor;
    private DispatcherTimer? _refListCloseTimer;

    // v8.1 fix: the graph builds a fresh ContextMenu per right-click (content depends on hit
    // position — column header / overflow chip / pill / row), so there's no single control-owned
    // ContextMenu for Avalonia to auto-close when a new one opens. Track the live one and close it
    // explicitly, or a second right-click (or an overflow/pill menu opened from within another)
    // leaves the previous popup stuck open behind the new one.
    private ContextMenu? _openMenu;

    private void OpenMenu(ContextMenu menu)
    {
        if (_openMenu is { } prev)
        {
            _openMenu = null;
            try { prev.Close(); } catch { /* ignore */ }
        }

        _openMenu = menu;
        EventHandler<RoutedEventArgs>? onClosed = null;
        onClosed = (_, _) =>
        {
            menu.Closed -= onClosed;
            if (ReferenceEquals(_openMenu, menu)) _openMenu = null;
        };
        menu.Closed += onClosed;
        menu.Open(this);
    }

    private Geometry _icoLocal = new PathGeometry();
    private Geometry _icoRemote = new PathGeometry();
    private Geometry _icoTag = new PathGeometry();

    private Point _dragStart;
    private int _dragRow = -1;
    private bool _dragging;
    private Point _lastPointerPos;
    private IPointer? _capturedPointer;

    private EffectiveTheme _themeMode = EffectiveTheme.Dark;
    private readonly Dictionary<int, Pen> _lanePens = [];
    private readonly Dictionary<int, SolidColorBrush> _laneBrushes = [];
    private readonly Dictionary<int, Pen> _emphPens = [];
    private readonly Dictionary<int, Pen> _pillBorders = [];
    // §V6-G1: badge label/icon ink — one of two shared brushes per lane colour's luminance.
    private readonly Dictionary<int, SolidColorBrush> _badgeInk = [];
    // §V6-G2: faint 1 DIP pen per lane colour for the non-HEAD badge→node connector.
    private readonly Dictionary<int, Pen> _faintConnectorPens = [];
    // §V6-G2: per-frame row -> right edge (screen x) of the primary drawn badge.
    private readonly Dictionary<int, double> _badgeRightEdge = [];
    private readonly Dictionary<int, SolidColorBrush> _laneFills = [];
    private readonly Dictionary<int, SolidColorBrush> _laneTicks = [];
    private readonly Dictionary<(int Tag, int Size, int WidthKey, string Text), FormattedText> _textCache = [];

    private double CornerRadius => Math.Min(LaneWidth, _rowHeight) * 0.75;

    private SolidColorBrush _bg = new(Colors.Black);
    private SolidColorBrush _chrome = new(Colors.Black);
    private Pen _borderPen = new(new SolidColorBrush(Colors.Gray), 1);
    private SolidColorBrush _nodeFill = new(Colors.White);
    private SolidColorBrush _text = new(Colors.Gray);
    private SolidColorBrush _textMuted = new(Colors.DimGray);
    private SolidColorBrush _pillText = new(Colors.White);
    private readonly SolidColorBrush _badgeInkDark = new(AvColor.FromRgb(0x1F, 0x1F, 0x1F));
    private readonly SolidColorBrush _badgeInkLight = new(AvColor.FromRgb(0xFF, 0xFF, 0xFF));
    private SolidColorBrush _headerText = new(Colors.DimGray);
    private SolidColorBrush _selectionFill = new(Colors.SlateBlue);
    private SolidColorBrush _selectionBar = new(Colors.SlateBlue);
    private SolidColorBrush _hoverFill = new(Colors.DimGray);
    private Pen _hoverPen = new(new SolidColorBrush(Colors.White), 1);
    // §V3.1: accent stroke on a commit node pinned just inside the Graph column's left/right
    // edge because its lane is panned or clipped out of view. Rebuilt on theme change.
    private Pen _clampedNodePen = new(new SolidColorBrush(Colors.SlateBlue), 1.5);
    // v8.4: the repo-settings cog drawn at the right edge of the BRANCH / TAGS header.
    private Pen _cogPen = new(new SolidColorBrush(Colors.DimGray), 1.4) { LineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
    private static readonly Geometry _cogGeo = Geometry.Parse(Historia.Components.IconPaths.Get("settings"));
    private Rect _refsCogRect;
    private SolidColorBrush _matchFill = new(Colors.Transparent);
    private Typeface _typeface = new(FontFamily.Default);

    private GraphQuery _query = new() { RefScope = GraphRefScope.AllRefs };
    private readonly Border _queryBar;
    private readonly TextBox _searchBox;
    private readonly TextBox _pathBox;
    private readonly ComboBox _scopeBox;
    private readonly CheckBox _stashCheck;
    private readonly TextBlock _matchLabel;
    private readonly GraphMatchCursor _matches = new();

    private double RowsTop => HeaderHeight + (_queryBar.IsVisible ? QueryBarHeight : 0);

    public CommitGraphControl()
    {
        UseLayoutRounding = true;
        Focusable = true;
        ClipToBounds = true;

        _refListCloseTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(120) };
        _refListCloseTimer.Tick += (_, _) =>
        {
            if (_refList.IsOpen && !RefListHovered && !RefListBadgeHovered())
                CloseRefList();
        };

        _scrollBar = new ScrollBar { Orientation = Orientation.Vertical, Width = 12, IsVisible = false, [Grid.ColumnProperty] = 0 };
        // While the user is dragging the thumb, background row-loading keeps triggering
        // ArrangeOverride passes (more rows stream in). Those must not reassert Maximum/Value
        // from our own stale `_scrollOffset`, which fights the ScrollBar's internal Track drag
        // math and desyncs the thumb from the pointer. `_scrollBarUserActive` gates that — and
        // it is driven by the ScrollBar's OWN Scroll event type, not pointer capture: a
        // ThumbTrack means a drag is in progress, and EVERY drag ends with EndScroll regardless
        // of where the pointer is released. (An earlier version latched on PointerPressed and
        // cleared on PointerReleased/CaptureLost on the bar; releasing the mouse away from the
        // bar fired neither, so the latch stuck on forever and the bar froze until a wheel
        // scroll forced a resync — the "scrollbar stuck at the bottom" bug.)
        _scrollBar.Scroll += (_, e) =>
        {
            _scrollBarUserActive = e.ScrollEventType == ScrollEventType.ThumbTrack;
            _scrollOffset = _scrollBar.Value;
            OnScrolled();
        };
        // Belt-and-braces: any of these unlatch too, and can never leave it stuck.
        _scrollBar.AddHandler(PointerReleasedEvent, (_, _) => _scrollBarUserActive = false, RoutingStrategies.Tunnel);
        _scrollBar.PointerCaptureLost += (_, _) => _scrollBarUserActive = false;
        _hScrollBar = new ScrollBar { Orientation = Orientation.Horizontal, Height = 12, IsVisible = false };
        _hScrollBar.Scroll += (_, _) => { _hScrollOffset = _hScrollBar.Value; InvalidateVisual(); };

        _searchBox = new TextBox { Width = 200, VerticalContentAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(_searchBox, "Find in message / author / SHA (Ctrl+F). Enter / Shift+Enter to step.");
        _searchBox.TextChanged += (_, _) => RecomputeMatches();
        _searchBox.AddHandler(KeyDownEvent, OnSearchBoxKeyDown, RoutingStrategies.Tunnel);
        _matchLabel = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 6, 0) };
        Dyn(_matchLabel, TextBlock.ForegroundProperty, "ColorTextMuted");

        _pathBox = new TextBox { Width = 160, VerticalContentAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(_pathBox, "Path filter — limits the walk to commits touching this path");
        _pathBox.LostFocus += (_, _) => ApplyQueryFromBar();
        _pathBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) ApplyQueryFromBar(); };

        _scopeBox = new ComboBox { Width = 130, VerticalContentAlignment = VerticalAlignment.Center };
        _scopeBox.Items.Add(new ComboBoxItem { Content = "All refs", Tag = GraphRefScope.AllRefs });
        _scopeBox.Items.Add(new ComboBoxItem { Content = "Current branch", Tag = GraphRefScope.CurrentBranch });
        _scopeBox.SelectedIndex = 0;
        _scopeBox.SelectionChanged += (_, _) => ApplyQueryFromBar();

        _stashCheck = new CheckBox { Content = "Stashes", VerticalAlignment = VerticalAlignment.Center };
        _stashCheck.IsCheckedChanged += (_, _) => ApplyQueryFromBar();

        var prev = new Button { Content = "◂", Width = 22 };
        ToolTip.SetTip(prev, "Previous match (Shift+Enter / Shift+F3)");
        prev.Click += (_, _) => StepMatch(-1);
        var next = new Button { Content = "▸", Width = 22 };
        ToolTip.SetTip(next, "Next match (Enter / F3)");
        next.Click += (_, _) => StepMatch(1);

        var bar = new StackPanel { Orientation = Orientation.Horizontal };
        TextElement.SetFontSize(bar, 13);
        foreach (var el in new Control[]
        {
            new TextBlock { Text = "Find", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0,0,6,0) },
            _searchBox, _matchLabel, prev, next,
            new Border { Width = 12 },
            new TextBlock { Text = "Path", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0,0,6,0) },
            _pathBox,
            new Border { Width = 12 }, _scopeBox,
            new Border { Width = 8 }, _stashCheck,
        })
        {
            bar.Children.Add(el);
        }
        _queryBar = new Border
        {
            Child = bar,
            Padding = new Thickness(8, 3, 8, 3),
            BorderThickness = new Thickness(0, 0, 0, 1),
            IsVisible = false,
        };
        Dyn(_queryBar, Border.BackgroundProperty, "ColorChrome");
        Dyn(_queryBar, Border.BorderBrushProperty, "ColorBorder");

        VisualChildren.Add(_scrollBar);
        VisualChildren.Add(_hScrollBar);
        VisualChildren.Add(_queryBar);
        LogicalChildren.Add(_scrollBar);
        LogicalChildren.Add(_hScrollBar);
        LogicalChildren.Add(_queryBar);
        LogicalChildren.Add(_refList);

        _icoLocal = ParseBadgeIcon(IconPaths.Get("save"));
        _icoRemote = ParseBadgeIcon(IconPaths.Get("remote"));
        _icoTag = ParseBadgeIcon(IconPaths.Get("tag"));
    }

    private static void Dyn(Control c, AvaloniaProperty p, string key)
        => c.Bind(p, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension(key));

    private static AvColor ToColor(Rgb c) => c.ToAvalonia();

    private static Geometry ParseBadgeIcon(string pathData)
    {
        var g = Geometry.Parse(pathData);
        g.Transform = new ScaleTransform(12.0 / 16.0, 12.0 / 16.0);
        return g;
    }

    public void Initialize(IServiceProvider services)
    {
        _services = services;
        _workspace = services.GetRequiredService<IWorkspaceService>();
        _layoutService = services.GetRequiredService<IGraphLayoutService>();
        _theme = services.GetRequiredService<IThemeService>();
        _dialogs = services.GetRequiredService<IDialogService>();
        _notifications = services.GetRequiredService<INotificationService>();
        _cherryPick = services.GetRequiredService<Historia.Services.PowerTools.ICherryPickService>();
        _stashes = services.GetRequiredService<Historia.Services.PowerTools.IStashService>();
        _nav = services.GetRequiredService<INavigationBus>();
        _settings = services.GetRequiredService<Historia.Services.Settings.ISettingsService>();
        _scrollProbe = services.GetRequiredService<Historia.Services.UI.IScrollDirectionProbe>();
        _graphMetaFromDisk = _settings.Current.GraphMetadataFromDisk;
        _repoLayout = services.GetRequiredService<Historia.Services.Settings.IRepoLayoutService>();

        _repoLayout.Changed += OnRepoLayoutChanged;

        _themeMode = _theme.Effective;
        RebuildThemeResources();

        _workspace.FocusedRepositoryChanged += OnFocusedRepositoryChanged;
        _theme.ThemeChanged += OnThemeChanged;
        AttachedToVisualTree += (_, _) => Attach();
        DetachedFromVisualTree += (_, _) => Detach();
    }

    private void Attach()
    {
        _workspace.FocusedRepositoryChanged -= OnFocusedRepositoryChanged;
        _workspace.FocusedRepositoryChanged += OnFocusedRepositoryChanged;

        _theme.ThemeChanged -= OnThemeChanged;
        _theme.ThemeChanged += OnThemeChanged;

        if (_repoLayout is not null)
        {
            _repoLayout.Changed -= OnRepoLayoutChanged;
            _repoLayout.Changed += OnRepoLayoutChanged;
        }

        // Re-take the commit-store lease Detach() dropped, if the previous layout survived the
        // round-trip off the visual tree. AddRef returns null when the store closed while we
        // were detached (a RefsChanged re-walk dropped the layout service's lease and ours was
        // the last) — that layout can't be rendered any more, so drop it and let the rebind /
        // recompute below repopulate rather than paint stub rows.
        if (_leasedStore is null && _layout?.Layout.Commits is { } resident)
        {
            _leasedStore = resident.AddRef();
            if (_leasedStore is null)
                _layout = null;
        }

        var focused = _workspace.Focused;
        if (!ReferenceEquals(focused, _context))
        {
            Bind(focused);
        }
        else if (_state is not null)
        {
            _state.RefsChanged -= OnRefsChanged;
            _state.RefsChanged += OnRefsChanged;
            _state.SelectedCommitChanged -= OnSelectedCommitChanged;
            _state.SelectedCommitChanged += OnSelectedCommitChanged;
            _state.RevealCommitRequested -= OnRevealCommitRequested;
            _state.RevealCommitRequested += OnRevealCommitRequested;

            // If the parked layout just died with its store handle (AddRef returned null above),
            // walk it again for the still-focused repo; otherwise just repaint what we kept.
            if (_layout is null && _context is { } stale)
                _ = ComputeLayoutAsync(stale);

            InvalidateMeasure();
            InvalidateArrange();
            InvalidateVisual();
        }
    }

    private void OnFocusedRepositoryChanged(object? sender, FocusedRepositoryChangedEventArgs e)
        => Dispatcher.UIThread.Post(() => Bind(e.New));

    private void Bind(RepositoryContext? context)
    {
        if (ReferenceEquals(context, _context))
            return;
        Historia.Diagnostics.PerfLog.Mark($"graph Bind -> {(context?.RepositoryPath ?? "<none>")}");

        if (_state is not null)
        {
            _state.RefsChanged -= OnRefsChanged;
            _state.SelectedCommitChanged -= OnSelectedCommitChanged;
            _state.RevealCommitRequested -= OnRevealCommitRequested;
        }

        // §V3.1: stash this repo's lane-pan offset for the session before switching away.
        if (_context is { } leaving)
            _panByRepo[Historia.Services.Settings.IRepoLayoutService.KeyFor(leaving.RepositoryPath)] = _graphPanOffset;

        CancelAndDisposeLayoutCts();
        SetLayout(null);               // release the commit-store lease before switching repos
        ClearReveal();                 // a running pump exits when it sees _revealTarget == null
        _revealUserTookOver = false;
        _selectedRow = -1;
        _hoverRow = -1;
        _scrollOffset = 0;
        _context = context;
        _state = context?.State;

        if (context is not null)
        {
            var layout = _repoLayout.Get(context.RepositoryPath);
            _columns = layout.Columns;
            _graphWidthPinned = layout.GraphWidthPinned;
            _manuallySized.Clear();
            _zoomOverridesPin = false;
            _graphPanOffset = _panByRepo.GetValueOrDefault(
                Historia.Services.Settings.IRepoLayoutService.KeyFor(context.RepositoryPath));
        }
        else
        {
            _columns = GraphColumnDefaults.All;
            _graphWidthPinned = false;
            _graphPanOffset = 0;
        }

        if (_state is not null)
        {
            _state.RefsChanged += OnRefsChanged;
            _state.SelectedCommitChanged += OnSelectedCommitChanged;
            _state.RevealCommitRequested += OnRevealCommitRequested;
        }

        InvalidateMeasure();
        InvalidateArrange();
        InvalidateVisual();
        if (context is not null)
            _ = ComputeLayoutAsync(context);
    }

    private void Detach()
    {
        if (_state is not null)
        {
            _state.RefsChanged -= OnRefsChanged;
            _state.SelectedCommitChanged -= OnSelectedCommitChanged;
            _state.RevealCommitRequested -= OnRevealCommitRequested;
        }

        _workspace.FocusedRepositoryChanged -= OnFocusedRepositoryChanged;
        _theme.ThemeChanged -= OnThemeChanged;
        if (_repoLayout is not null)
            _repoLayout.Changed -= OnRepoLayoutChanged;
        CloseRefList();
        CancelAndDisposeLayoutCts();

        // Off the visual tree no Render can run, so drop the commit-store lease now rather than
        // hold a disk-backed store's file handle open after the panel is torn down for good.
        // Attach() re-takes it if _layout is still around (a transient detach/reattach). _layout
        // itself is kept so that round-trip doesn't blank the graph.
        _leasedStore?.Dispose();
        _leasedStore = null;
    }

    // Detach can run more than once across a control's attach/detach cycles, and several
    // async continuations touch _layoutCts on the UI thread. Swap the field out first, then
    // cancel/dispose — and tolerate a source that a prior pass already disposed.
    private void CancelAndDisposeLayoutCts()
    {
        _extendChannel?.Writer.TryComplete();
        _extendChannel = null;
        var cts = _layoutCts;
        _layoutCts = null;
        if (cts is null)
            return;
        try { cts.Cancel(); }
        catch (ObjectDisposedException) { }
        cts.Dispose();
    }

    private void OnRepoLayoutChanged(object? sender, Historia.Services.Settings.RepoLayoutChangedEventArgs e)
        => Dispatcher.UIThread.Post(() =>
        {
            if (_context is null)
                return;
            // §V3.1: accept this repo's own change and any application-wide column change.
            var mine = string.Equals(e.RepoPath,
                Historia.Services.Settings.IRepoLayoutService.KeyFor(_context.RepositoryPath), StringComparison.Ordinal);
            var global = string.Equals(e.RepoPath,
                Historia.Services.Settings.IRepoLayoutService.GlobalKey, StringComparison.Ordinal);
            if (!mine && !global)
                return;

            // Re-read the merged view (global order + visibility with this repo's session
            // widths) rather than trusting e.Layout, whose widths are defaults for a global change.
            var layout = _repoLayout.Get(_context.RepositoryPath);
            if (ReferenceEquals(layout.Columns, _columns))
                return;
            _columns = layout.Columns;
            _graphWidthPinned = layout.GraphWidthPinned;
            InvalidateMeasure();
            InvalidateArrange();
            InvalidateVisual();
        });

    private void OnRefsChanged(object? sender, RefsChangedEventArgs e)
    {
        if (_context is { } ctx)
            _ = RecomputeAsync(ctx);
    }

    private void OnSelectedCommitChanged(object? sender, SelectionChangedEventArgs e)
    {
        _selectedRow = ResolveRow(e.CommitId);
        AutoPanToSelectedRow();
        InvalidateVisual();
    }

    /// <summary>§V3.1: pan the Graph column so the selected commit's lane is visible. Runs from
    /// the one place the selected row changes (row click, clamped-node click, keyboard nav,
    /// Ctrl+F step, reveal) — never from hover.</summary>
    private void AutoPanToSelectedRow()
    {
        if (_selectedRow >= 0 && _layout is { } l && _selectedRow < l.Layout.RowCount)
            EnsureLaneInView(l.Layout.Rows[_selectedRow].LaneIndex);
    }

    private void OnRevealCommitRequested(object? sender, RevealCommitEventArgs e)
    {
        var row = ResolveRow(e.CommitId);
        if (row >= 0)
        {
            // Already loaded — jump straight there and drop any pending reveal.
            ClearReveal();
            _selectedRow = row;
            AutoPanToSelectedRow();
            CenterRow(row);
            InvalidateVisual();
            return;
        }

        // Not loaded yet: blank the graph, show the target name centred, and (if one isn't
        // already running) start the background pump that walks toward it. A fresh explicit
        // click re-takes the viewport.
        _revealTarget = e.CommitId;
        _revealDeco = BuildRevealDecoration(e.CommitId);
        _revealUserTookOver = false;
        InvalidateArrange();
        InvalidateVisual();

        if (!_revealPumpRunning)
            _ = RunRevealPumpAsync();
    }

    private async Task RunRevealPumpAsync()
    {
        _revealPumpRunning = true;
        try
        {
            var ct = _layoutCts?.Token ?? CancellationToken.None;
            while (_revealTarget is { } target && !_revealUserTookOver && !ct.IsCancellationRequested)
            {
                var row = ResolveRow(target);
                if (row >= 0)
                {
                    _selectedRow = row;
                    AutoPanToSelectedRow();
                    if (!_revealUserTookOver)
                        CenterRow(row);
                    break;
                }

                InvalidateVisual(); // refresh the "… N rows" count on the placeholder

                var before = _layout?.Layout.RowCount ?? 0;
                await EnsureExtendedAsync(int.MaxValue).ConfigureAwait(true);
                var after = _layout?.Layout.RowCount ?? 0;

                if (after != before)
                {
                    await Task.Delay(16, ct).ConfigureAwait(true);
                    continue;
                }

                if (_layout is { Layout.IsComplete: true })
                {
                    _notifications.Publish(NotificationSeverity.Info,
                        "That commit isn't in this graph.",
                        "It may be on a branch outside the current graph scope. Widen the scope, or use Find (Ctrl+F).");
                    break;
                }

                // Extension stalled before reaching the target and the walk isn't complete —
                // the resident-row ceiling blocks loading more. A bare break here would leave
                // the user on the blanked placeholder with no explanation.
                _notifications.Publish(NotificationSeverity.Info,
                    "That commit is beyond the loaded graph.",
                    $"History is capped at {ResidentRowCap:N0} rows to preserve memory. Use Find (Ctrl+F) to jump straight to it.");
                break;
            }
        }
        catch { /* reveal is best-effort — never surface as a fatal error */ }
        finally
        {
            _revealPumpRunning = false;
            ClearReveal();
            InvalidateArrange(); // un-blank: bring the graph body (and scrollbars) back
            InvalidateVisual();
        }
    }

    private void ClearReveal()
    {
        _revealTarget = null;
        _revealDeco = null;
    }

    /// <summary>A stand-in badge for a commit we haven't walked to yet — a branch or tag that
    /// points at it (rendered exactly like a real one), else its short SHA. The colour is seeded
    /// from the name the same way the layout engine seeds lane colour, so it matches once the
    /// row loads.</summary>
    private GraphDecoration BuildRevealDecoration(ObjectId id)
    {
        var branch = _state?.Branches.FirstOrDefault(b => Equals(b.TipCommitId, id));
        var tag = branch is null ? _state?.Tags.FirstOrDefault(t => Equals(t.TargetCommitId, id)) : null;

        DecorationType type;
        string label;
        var isLocal = false;
        string[]? remotes = null;

        if (branch is not null && branch.IsRemote)
        {
            type = DecorationType.RemoteBranch;
            var rn = branch.RemoteName;
            remotes = [rn ?? "origin"];
            label = rn is not null && branch.Name.StartsWith(rn + "/", StringComparison.Ordinal)
                ? branch.Name[(rn.Length + 1)..]
                : branch.Name;
        }
        else if (branch is not null)
        {
            type = DecorationType.LocalBranch;
            isLocal = true;
            label = branch.Name;
        }
        else if (tag is not null)
        {
            type = DecorationType.Tag;
            label = tag.Name;
        }
        else
        {
            type = DecorationType.LocalBranch;
            label = id.Sha.Length >= 7 ? id.Sha[..7] : id.Sha;
        }

        return new GraphDecoration
        {
            RowIndex = 0,
            Type = type,
            Label = label,
            ColorIndex = (int)(Historia.Services.Graph.StableHash.Fnv1a(label) % (uint)LanePalette.Size),
            IsLocal = isLocal,
            RemotesOrNull = remotes,
        };
    }

    private void CenterRow(int row)
    {
        if (_layout is not { } l)
            return;
        var contentHeight = l.Layout.RowCount * _rowHeight;
        var viewH = Math.Max(0, Bounds.Height - RowsTop - (_hScrollBar.IsVisible ? 12 : 0));
        var max = Math.Max(0, contentHeight - viewH);
        if (max <= 0)
            return;
        _scrollOffset = Math.Clamp(row * _rowHeight + _rowHeight / 2 - viewH / 2, 0, max);
        _scrollBar.Value = _scrollOffset;
        _ = EnsureExtendedAsync((int)((_scrollOffset + viewH) / _rowHeight) + 1);
    }

    private int ResolveRow(ObjectId? id)
        => id is not null && _layout is { } l && l.Layout.RowByCommitId.TryGetValue(id, out var row) ? row : -1;

    /// <summary>§7.3: rows laid out in the first, fast phase before the configured
    /// "Initial rows loaded" batch, so a repo open / switch paints almost immediately.</summary>
    private const int QuickInitialRows = 200;

    private async Task ComputeLayoutAsync(RepositoryContext context)
    {
        CancelAndDisposeLayoutCts();
        _layoutCts = new CancellationTokenSource();
        var ct = _layoutCts.Token;
        InitializeExtendPump(context, ct);

        try
        {
            var query = _query;
            Historia.Diagnostics.PerfLog.Mark($"graph ComputeLayoutAsync start ({context.RepositoryPath})");
            const int full = 2_500;
            var quick = Math.Min(QuickInitialRows, full);

            // v8.2: paint an optimistic snapshot from the on-disk walk cache before the real
            // quick phase below even starts — that phase still has to pay for a full ref
            // enumeration (ComputeFingerprint) before it can so much as check the cache, and on
            // a large repo that alone can take a while. This shortcut skips straight to a plain
            // file read, so something is on screen sooner; it is unconditionally replaced by the
            // fingerprint-verified quick-phase result a few lines down; if the repo hasn't
            // changed since the file was written, that replacement paints identical rows anyway.
            if (_layout is null)
            {
                var optimistic = await Task.Run(
                    () => _layoutService.TryOptimisticSnapshot(context, query, quick), ct).ConfigureAwait(true);
                if (optimistic is not null && !ct.IsCancellationRequested)
                {
                    ApplyLayout(optimistic);
                    Historia.Diagnostics.PerfLog.Mark($"graph optimistic paint ({optimistic.Layout.RowCount} rows)");
                }
            }

            // Phase 1: a tiny batch for an immediate first paint (<15ms). Phase 2 extends smoothly.
            if (quick < full)
            {
                var fast = await Task.Run(
                    () => _layoutService.ComputeLayoutAsync(context, query, quick, ct), ct).ConfigureAwait(true);
                if (ct.IsCancellationRequested)
                    return;
                ApplyLayout(fast);
                Historia.Diagnostics.PerfLog.Mark($"graph quick paint ({fast.Layout.RowCount} rows)");

                var rest = await Task.Run(
                    () => _layoutService.ExtendLayoutAsync(context, fast, full - fast.Layout.RowCount, ct), ct).ConfigureAwait(true);
                if (ct.IsCancellationRequested)
                    return;
                ApplyLayout(rest);
                Historia.Diagnostics.PerfLog.Mark($"graph first paint queued ({rest.Layout.RowCount} rows)");
                _ = StartBackgroundSilentLoadAsync(context, ct);
                return;
            }

            var result = await Task.Run(
                () => _layoutService.ComputeLayoutAsync(context, query, full, ct), ct).ConfigureAwait(true);
            if (ct.IsCancellationRequested)
                return;
            ApplyLayout(result);
            Historia.Diagnostics.PerfLog.Mark($"graph first paint queued ({result.Layout.RowCount} rows)");
            _ = StartBackgroundSilentLoadAsync(context, ct);
        }
        catch (OperationCanceledException) { }
        catch
        {
            // Layout failure leaves the graph blank rather than crashing the shell.
        }
        finally
        {
            // Whatever happened above -- painted, cancelled, or failed -- this context's first
            // graph-paint attempt is done. WorkspaceService.InitializeContextAsync is waiting on
            // this (bounded by its own timeout) before it starts touching the repo itself.
            context.MarkFirstGraphPaintDone();
        }
    }

    private void ApplyLayout(GraphLayoutResult result)
    {
        if (!SetLayout(result))
        {
            // The freshly computed store was closed by a concurrent re-walk before we could
            // lease it. Keep the previous layout; the re-walk that closed it queues its own
            // RefsChanged -> ComputeLayoutAsync, which will hand us a live store.
            Historia.Diagnostics.PerfLog.Mark("graph ApplyLayout dropped: store closed pre-lease");
            return;
        }
        _selectedRow = ResolveRow(_state?.SelectedCommitId);
        RecomputeMatches();
        InvalidateMeasure();
        // §V3.1: TotalLaneCount drives the auto Graph width — re-arrange, not just re-paint.
        InvalidateArrange();
        InvalidateVisual();
    }

    /// <summary>The single choke point for replacing <see cref="_layout"/>. Transfers the commit-
    /// store lease: takes one on the new layout's store and releases the one held for the old,
    /// so a disk-backed store's file handle stays open exactly as long as this control can still
    /// render from it — even after <c>GraphLayoutService</c> drops its own reference on a
    /// RefsChanged re-walk (that race previously closed the handle mid-<see cref="Render"/> and
    /// terminated the process). Idempotent when the store instance is unchanged (an in-place
    /// Extend reuses it). Must run on the UI thread.
    /// <para>Returns false when the new layout's store was already closed by a concurrent final
    /// Dispose (<see cref="ICommitStore.AddRef"/> returned null) — the layout is dead on arrival
    /// and neither it nor the lease is adopted; the caller keeps whatever it had.</para></summary>
    private bool SetLayout(GraphLayoutResult? result)
    {
        var next = result?.Layout.Commits;
        if (ReferenceEquals(next, _leasedStore))
        {
            _layout = result;
            return true;
        }

        if (next is not null && next.AddRef() is null)
            return false;

        _leasedStore?.Dispose();
        _leasedStore = next;
        _layout = result;
        return true;
    }

    private async Task RecomputeAsync(RepositoryContext context)
    {
        if (_layout is not { } previous)
        {
            await ComputeLayoutAsync(context).ConfigureAwait(true);
            return;
        }

        CancelAndDisposeLayoutCts();
        _layoutCts = new CancellationTokenSource();
        var ct = _layoutCts.Token;
        InitializeExtendPump(context, ct);

        try
        {
            var query = _query;
            const int initial = 2_500;
            var result = await Task.Run(() =>
                _layoutService.RecomputeAsync(context, query, previous, InvalidationReason.RefsChanged, initial, ct), ct).ConfigureAwait(true);
            if (ct.IsCancellationRequested)
                return;
            ApplyLayout(result);
            _ = StartBackgroundSilentLoadAsync(context, ct);
        }
        catch (OperationCanceledException) { }
        catch { }
    }

    private void InitializeExtendPump(RepositoryContext context, CancellationToken ct)
    {
        Interlocked.Exchange(ref _cappedNotified, 0);
        _extendChannel = Channel.CreateUnbounded<ExtendRequest>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
        _ = RunExtendPumpAsync(_extendChannel.Reader, context, ct);
    }

    // Every path that stops extending because the resident-row ceiling was reached routes
    // through here: EnsureExtendedAsync and StartBackgroundSilentLoadAsync both bail before
    // ever writing to the channel, so the pump alone could not be relied on to surface it.
    // Fires the toast at most once per layout (reset by InitializeExtendPump).
    private void NotifyResidentCapReached(int rowCount)
    {
        if (Interlocked.Exchange(ref _cappedNotified, 1) != 0)
            return;
        _notifications.Publish(NotificationSeverity.Info,
            "Graph capped at resident limit.",
            $"Loaded {rowCount:N0} commits. Further history is capped to preserve system memory.");
    }

    private async Task RunExtendPumpAsync(ChannelReader<ExtendRequest> reader, RepositoryContext context, CancellationToken ct)
    {
        try
        {
            while (await reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                while (reader.TryRead(out var req))
                {
                    if (ct.IsCancellationRequested || !ReferenceEquals(req.Context, _context))
                    {
                        req.Completion?.TrySetCanceled(ct);
                        continue;
                    }

                    if (_layout is not { } current || current.Layout.IsComplete)
                    {
                        req.Completion?.TrySetResult();
                        continue;
                    }

                    // For scroll requests, skip if already satisfied
                    if (req.TargetRow >= 0 && req.TargetRow < current.Layout.RowCount - 500 && req.TargetRow != int.MaxValue)
                    {
                        req.Completion?.TrySetResult();
                        continue;
                    }

                    var cap = ResidentRowCap;
                    if (current.Layout.RowCount >= cap)
                    {
                        NotifyResidentCapReached(current.Layout.RowCount);
                        req.Completion?.TrySetResult();
                        break;
                    }

                    var batch = Math.Min(req.BatchSize, cap - current.Layout.RowCount);
                    if (batch <= 0)
                    {
                        req.Completion?.TrySetResult();
                        continue;
                    }

                    try
                    {
                        var extended = await _layoutService.ExtendLayoutAsync(req.Context, current, batch, ct).ConfigureAwait(false);
                        if (extended.Layout.RowCount > current.Layout.RowCount && ReferenceEquals(req.Context, _context))
                        {
                            await Dispatcher.UIThread.InvokeAsync(() =>
                            {
                                if (ct.IsCancellationRequested || !ReferenceEquals(req.Context, _context))
                                    return;

                                SetLayout(extended);
                                InvalidateMeasure();
                                InvalidateArrange(); // lane count can grow -> auto Graph width
                                InvalidateVisual();
                            });
                        }
                        req.Completion?.TrySetResult();
                    }
                    catch (OperationCanceledException)
                    {
                        req.Completion?.TrySetCanceled(ct);
                    }
                    catch (Exception ex)
                    {
                        req.Completion?.TrySetException(ex);
                    }

                    // Yield briefly after background batch to keep system and UI fully responsive
                    if (req.TargetRow < 0)
                    {
                        await Task.Delay(20, ct).ConfigureAwait(false);
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch { }
    }

    private async Task EnsureExtendedAsync(int neededRow)
    {
        if (_context is not { } ctx || _layout is not { } current || _extendChannel is not { } channel)
            return;
        if (current.Layout.IsComplete)
            return;
        if (neededRow < current.Layout.RowCount - 500 && neededRow != int.MaxValue)
            return;

        if (current.Layout.RowCount >= ResidentRowCap)
        {
            NotifyResidentCapReached(current.Layout.RowCount);
            return;
        }

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ct = _layoutCts?.Token ?? CancellationToken.None;
        const int batch = 5_000;
        if (channel.Writer.TryWrite(new ExtendRequest(ctx, neededRow, batch, tcs, ct)))
        {
            try
            {
                await tcs.Task.WaitAsync(ct).ConfigureAwait(true);
            }
            catch { }
        }
    }

    private async Task StartBackgroundSilentLoadAsync(RepositoryContext context, CancellationToken ct)
    {
        try
        {
            await Task.Yield();
            var cap = ResidentRowCap;

            while (!ct.IsCancellationRequested && ReferenceEquals(context, _context))
            {
                if (_layout is not { } current || current.Layout.IsComplete)
                    break;
                if (current.Layout.RowCount >= cap)
                {
                    NotifyResidentCapReached(current.Layout.RowCount);
                    break;
                }

                if (_extendChannel is not { } channel)
                    break;

                var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                const int batch = 5_000;
                if (!channel.Writer.TryWrite(new ExtendRequest(context, -1, batch, tcs, ct)))
                    break;

                await tcs.Task.WaitAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch { }
    }

    // ── Theme ──

    private void OnThemeChanged(object? sender, ThemeChangedEventArgs e)
        => Dispatcher.UIThread.Post(() =>
        {
            _themeMode = e.NewTheme;
            RebuildThemeResources(); // recomputes _refsBaseWidth, an input to Resolve
            InvalidateArrange();
            InvalidateVisual();
        });

    private void RebuildThemeResources()
    {
        _lanePens.Clear();
        _laneBrushes.Clear();
        _emphPens.Clear();
        _badgeInk.Clear();
        _faintConnectorPens.Clear();
        _pillBorders.Clear();
        _laneFills.Clear();
        _laneTicks.Clear();
        _textCache.Clear();
        _elbowGeometryCache.Clear();

        var palette = _theme.Palette;
        var bg = ToColor(palette["bg"]);
        var text = ToColor(palette["text"]);
        var muted = ToColor(palette["text-muted"]);
        var primary = ToColor(palette["primary"]);

        _bg = new SolidColorBrush(bg);
        _chrome = new SolidColorBrush(ToColor(palette["chrome"]));
        _borderPen = new Pen(new SolidColorBrush(ToColor(palette["border"])), 1);
        _nodeFill = new SolidColorBrush(bg);
        _text = new SolidColorBrush(text);
        _textMuted = new SolidColorBrush(muted);
        _pillText = new SolidColorBrush(ToColor(palette["ref-label"]));
        _headerText = new SolidColorBrush(muted);
        _selectionFill = new SolidColorBrush(ToColor(palette["list-active"]));
        _hoverFill = new SolidColorBrush(ToColor(palette["list-hover"]));
        _selectionBar = new SolidColorBrush(primary);
        _hoverPen = new Pen(new SolidColorBrush(primary), 2.0);
        _cogPen = new Pen(new SolidColorBrush(muted), 1.4) { LineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        _clampedNodePen = new Pen(new SolidColorBrush(primary), 1.5);
        var warn = palette["warning"];
        _matchFill = new SolidColorBrush(AvColor.FromArgb(0x30, warn.R, warn.G, warn.B));

        RecomputeRefsBaseWidth();
    }

    private void RecomputeRefsBaseWidth()
    {
        var probe = new FormattedText("abcdefghijkl…", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            _typeface, BadgeLabelSize, Brushes.Black);
        var w = 8 + 6 + probe.Width + 4 + 12 + 2 + 12 + 6 + 8;
        _refsBaseWidth = Math.Clamp(w, GraphColumnDefaults.RefsStaticFloor, 240);
    }

    /// <summary>§V6-G9: the resolved lane colour for an index, read from the theme service
    /// (built-in rainbow or the active custom overlay). The maths lives in
    /// <see cref="GraphLanePalette.LaneRgb"/> so it is unit-testable; this is the control-state
    /// binding of it.</summary>
    private Rgb LaneRgb(int colorIndex)
        => GraphLanePalette.LaneRgb(colorIndex, _theme.LaneColors, _themeMode);

    // §V6: every per-lane-colour Pen/Brush is memoised in its own dictionary (all cleared
    // together by RebuildThemeResources on a theme change). One shape: look it up, else build
    // it once and cache. The build lambda runs only on a miss.
    private static T Pooled<T>(Dictionary<int, T> pool, int colorIndex, Func<int, T> build)
        => pool.TryGetValue(colorIndex, out var v) ? v : pool[colorIndex] = build(colorIndex);

    private Pen LanePen(int colorIndex) => Pooled(_lanePens, colorIndex,
        i => new Pen(new SolidColorBrush(ToColor(LaneRgb(i))), LaneStrokeWidth));

    /// <summary>§V6-G2: a thin, faint version of the lane colour for a non-HEAD badge→node
    /// connector. 1 DIP at 45% alpha — the crisp, stable equivalent of "thin and faint" (a
    /// sub-pixel stroke rounds away under layout rounding).</summary>
    private Pen FaintConnectorPen(int colorIndex) => Pooled(_faintConnectorPens, colorIndex, i =>
    {
        var c = LaneRgb(i);
        return new Pen(new SolidColorBrush(AvColor.FromArgb(0x73, c.R, c.G, c.B)), 1.0);
    });

    private SolidColorBrush LaneBrush(int colorIndex) => Pooled(_laneBrushes, colorIndex,
        i => new SolidColorBrush(ToColor(LaneRgb(i))));

    private Pen EmphPen(int colorIndex) => Pooled(_emphPens, colorIndex,
        i => new Pen(new SolidColorBrush(ToColor(LaneRgb(i))), 3.0));

    /// <summary>§V6-G1: the badge is a solid opaque lane-colour fill, so its label and icons
    /// need ink that contrasts against that hue — near-black on a light badge, near-white on a
    /// dark one. Two shared brush instances keep <see cref="Text"/>'s cache keying correct.</summary>
    private SolidColorBrush BadgeInk(int colorIndex) => Pooled(_badgeInk, colorIndex,
        i => GraphLanePalette.PrefersDarkInk(LaneRgb(i)) ? _badgeInkDark : _badgeInkLight);

    private Pen PillBorder(int colorIndex) => Pooled(_pillBorders, colorIndex,
        i => new Pen(new SolidColorBrush(ToColor(LaneRgb(i))), 1.5));

    private SolidColorBrush LaneFill(int colorIndex) => Pooled(_laneFills, colorIndex, i =>
    {
        var c = LaneRgb(i);
        byte a = _themeMode == EffectiveTheme.Light ? (byte)0x2E : (byte)0x3A;
        return new SolidColorBrush(AvColor.FromArgb(a, c.R, c.G, c.B));
    });

    private SolidColorBrush LaneTick(int colorIndex) => Pooled(_laneTicks, colorIndex,
        i => new SolidColorBrush(ToColor(LaneRgb(i))));

    private int TextTag(IBrush b) =>
        ReferenceEquals(b, _text) ? 0 :
        ReferenceEquals(b, _nodeFill) ? 1 :
        ReferenceEquals(b, _textMuted) ? 2 :
        ReferenceEquals(b, _pillText) ? 3 :
        ReferenceEquals(b, _headerText) ? 4 :
        ReferenceEquals(b, _badgeInkDark) ? 5 :
        ReferenceEquals(b, _badgeInkLight) ? 6 : 7;

    // ── Layout / arrange ──

    protected override Size MeasureOverride(Size availableSize)
    {
        _scrollBar.Measure(availableSize);
        _hScrollBar.Measure(availableSize);
        _queryBar.Measure(new Size(double.IsInfinity(availableSize.Width) ? 900 : availableSize.Width, QueryBarHeight));
        return new Size(
            double.IsInfinity(availableSize.Width) ? 600 : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? 400 : availableSize.Height);
    }

    /// <summary>§V3.1: resolve the column band into <see cref="_resolvedColumns"/>,
    /// <see cref="_bandWidth"/>, <see cref="_graphColLeft"/>, <see cref="_graphColWidth"/> and
    /// clamp the lane pan. Runs in <see cref="ArrangeOverride"/> (so the scrollbar logic there
    /// sees this frame's band width, not last frame's) and again at the top of
    /// <see cref="Render"/>. Pure geometry — safe to call more than once per frame.</summary>
    private void ResolveColumnBand(double width)
    {
        var totalLanes = _layout?.Layout.TotalLaneCount ?? 0;
        var res = GraphColumnLayout.Resolve(_columns, width, LaneWidth, totalLanes,
            _graphWidthPinned && !_zoomOverridesPin, _refsBaseWidth, _manuallySized);
        _resolvedColumns = res.Columns as ResolvedColumn[] ?? [.. res.Columns];
        _bandWidth = res.BandWidth;
        _graphColLeft = 0;
        _graphColWidth = 0;
        foreach (var c in _resolvedColumns)
            if (c.Kind == GraphColumnKind.Graph) { _graphColLeft = c.Left; _graphColWidth = c.Width; }
        ClampPan();
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _queryBar.Arrange(new Rect(0, HeaderHeight, finalSize.Width, QueryBarHeight));

        // Resolve the band before the scrollbar logic below reads _bandWidth — during an
        // interactive sidebar drag the arrange pass would otherwise see last frame's width.
        ResolveColumnBand(finalSize.Width);

        var contentHeight = (_layout?.Layout.RowCount ?? 0) * _rowHeight;
        var rowArea = finalSize.Height - RowsTop;
        var showV = contentHeight > rowArea;
        _scrollBar.IsVisible = showV && !RevealBlanking;

        var showH = _bandWidth > finalSize.Width - (showV ? 12 : 0) + 0.5;
        _hScrollBar.IsVisible = showH && !RevealBlanking;

        var viewport = Math.Max(0, rowArea - (showH ? 12 : 0));
        if (showV)
        {
            _scrollBar.Arrange(new Rect(finalSize.Width - 12, RowsTop, 12, viewport));
            _scrollBar.Minimum = 0;
            _scrollBar.Maximum = Math.Max(0, contentHeight - viewport);
            _scrollBar.ViewportSize = viewport;
            _scrollBar.LargeChange = viewport;
            _scrollBar.SmallChange = _rowHeight;
            // Always keep our mirror inside the (possibly just-grown or just-shrunk) range —
            // an out-of-range `_scrollOffset` is what left the bar unusable when Maximum moved
            // under a drag. Only push the value back into the ScrollBar when the user is NOT
            // mid-drag, so we never fight its Track math.
            _scrollOffset = Math.Clamp(_scrollOffset, 0, _scrollBar.Maximum);
            if (!_scrollBarUserActive)
                _scrollBar.Value = _scrollOffset;
        }
        else
        {
            _scrollOffset = 0;
        }

        if (_hScrollBar.IsVisible)
        {
            _hScrollBar.Arrange(new Rect(0, finalSize.Height - 12, Math.Max(0, finalSize.Width - (showV ? 12 : 0)), 12));
            _hScrollBar.Minimum = 0;
            _hScrollBar.Maximum = Math.Max(0, _bandWidth - (finalSize.Width - (showV ? 12 : 0)));
            _hScrollBar.ViewportSize = finalSize.Width;
            _hScrollBar.LargeChange = finalSize.Width * 0.9;
            _hScrollBar.SmallChange = 40;
            _hScrollOffset = Math.Min(_hScrollOffset, _hScrollBar.Maximum);
            _hScrollBar.Value = _hScrollOffset;
        }
        else
        {
            _hScrollOffset = 0;
        }

        return finalSize;
    }

    // ── Input ──

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        // v8 M9: honour the OS natural/inverted-scroll preference (macOS & Linux) or the
        // explicit override. Windows probes false, so the sign is 1 and behaviour is unchanged.
        var delta = e.Delta.Y * Historia.Services.UI.WheelInput.Sign(
            _settings.Current.WheelScrollDirection, _scrollProbe.NaturalScrolling);
        if (_revealTarget is not null && !_revealUserTookOver)
        {
            _revealUserTookOver = true; // any manual scroll/zoom hands the viewport back
            InvalidateArrange();        // un-blank the graph body
        }

        if ((e.KeyModifiers & KeyModifiers.Control) != 0)
        {
            var factor = delta > 0 ? 1.1 : 1 / 1.1;

            // Anchor the zoom on the row under the cursor so it stays put (fall back to the
            // viewport top when the pointer is over the header).
            var anchorY = e.GetPosition(this).Y;
            var rowAtAnchor = anchorY >= RowsTop ? (_scrollOffset + anchorY - RowsTop) / _rowHeight : (double?)null;

            var laneBefore = _laneWidth;
            _rowHeight = Math.Clamp(_rowHeight * factor, MinRowHeight, MaxRowHeight);
            _laneWidth = Math.Clamp(_laneWidth * factor, MinLaneWidth, MaxLaneWidth);
            _zoomOverridesPin = true;
            _elbowGeometryCache.Clear();

            // §V3.1: scale the pan by the same ratio the lane width actually changed by, so the
            // same lanes stay in view; ClampPan again once the resolved column width settles.
            if (laneBefore > 0)
                _graphPanOffset *= _laneWidth / laneBefore;
            ClampPan();

            if (rowAtAnchor is { } r)
                _scrollOffset = Math.Max(0, r * _rowHeight - (anchorY - RowsTop));

            InvalidateArrange();
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        // §V3.1: Shift+wheel, or a trackpad horizontal swipe, over the Graph column's rows pans
        // the lanes. Some platforms deliver Shift+wheel as Delta.X already, so accept either.
        // With no hidden lanes (MaxPan == 0) this falls through to vertical scrolling, as today.
        if (MaxPan > 0)
        {
            var wpos = e.GetPosition(this);
            if (wpos.Y >= RowsTop && ColumnKindAt(wpos.X) == GraphColumnKind.Graph)
            {
                var pan = e.Delta.X != 0 ? e.Delta.X
                        : (e.KeyModifiers & KeyModifiers.Shift) != 0 ? delta
                        : 0;
                if (pan != 0)
                {
                    _graphPanOffset = Math.Clamp(_graphPanOffset - pan * 40, 0, MaxPan);
                    InvalidateVisual();
                    e.Handled = true;
                    return;
                }
            }
        }

        _scrollOffset = Math.Clamp(_scrollOffset - delta * 40, 0, Math.Max(0, _scrollBar.Maximum));
        OnScrolled();
        e.Handled = true;
    }

    private void OnScrolled()
    {
        if (_revealTarget is not null && !_revealUserTookOver)
        {
            _revealUserTookOver = true; // manual scroll while a reveal is pending → user is driving
            InvalidateArrange();        // un-blank the graph body
        }
        _scrollBar.Value = _scrollOffset;
        CloseRefList();
        var lastVisible = (int)((_scrollOffset + Bounds.Height - RowsTop) / _rowHeight) + 1;
        _ = EnsureExtendedAsync(lastVisible);
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        var pos = e.GetPosition(this);
        _lastPointerPos = pos;
        var leftDown = e.GetCurrentPoint(this).Properties.IsLeftButtonPressed;

        if (_resizing)
        {
            var delta = pos.X - _resizeStartX;
            var eff = _resizeInverted ? -delta : delta;
            var min = GraphColumnDefaults.MinWidthFor(_resizeKind);
            var max = GraphColumnDefaults.MaxWidthFor(_resizeKind);
            // §V3.1: Graph never drags wider than the lanes the layout uses (skip until a
            // layout has lanes). Applying the ceiling during the drag — not only on save —
            // also removes the snap-back when a non-Graph column is released past 2,000px.
            if (_resizeKind == GraphColumnKind.Graph && _layout is { } l && l.Layout.TotalLaneCount > 0)
                max = Math.Clamp(NaturalGraphWidth(l.Layout.TotalLaneCount), min, max);
            var w = Math.Clamp(_resizeStartWidth + eff, min, max);
            _columns = WithColumnWidth(_columns, _resizeKind, w);
            if (_resizeKind == GraphColumnKind.Graph)
            {
                _graphWidthPinned = true;
                _zoomOverridesPin = false;
            }
            InvalidateArrange();
            InvalidateVisual();
            return;
        }

        if (_reorderKind is not null)
        {
            _reorderGhostX = pos.X;
            _reorderCaret = CaretIndexAt(pos.X);
            InvalidateVisual();
            return;
        }
        if (_reorderCandidate is not null && leftDown && Math.Abs(pos.X - _dragStart.X) > 6)
        {
            _reorderKind = _reorderCandidate;
            _reorderCandidate = null;
            _capturedPointer = e.Pointer;
            e.Pointer.Capture(this);
            _reorderGhostX = pos.X;
            _reorderCaret = CaretIndexAt(pos.X);
            InvalidateVisual();
            return;
        }

        if (pos.Y < HeaderHeight)
        {
            Cursor = new Cursor(
                _refsCogRect.Contains(pos) ? StandardCursorType.Hand
                : BoundaryAt(pos.X) >= 0 ? StandardCursorType.SizeWestEast
                : StandardCursorType.Arrow);
            return;
        }
        var overOverflow = _overflowChips.Any(c => c.Rect.Contains(pos));
        Cursor = new Cursor(_dragging || overOverflow ? StandardCursorType.Hand : StandardCursorType.Arrow);

        if (_dragRow >= 0 && !_dragging && leftDown
            && (Math.Abs(pos.X - _dragStart.X) > 6 || Math.Abs(pos.Y - _dragStart.Y) > 6))
        {
            _dragging = true;
            Cursor = new Cursor(StandardCursorType.Hand);
        }

        // While the pointer sits over the (auto-expanding) vertical scrollbar, skip the
        // row-hover tracking below: it was invalidating this whole custom-drawn surface on
        // every pixel of vertical movement, which raced against the ScrollBar's own hover-expand
        // transition and showed up as flicker wherever the expanded bar overlapped a text column.
        if (_scrollBar.IsVisible && _scrollBar.Bounds.Contains(pos))
        {
            if (_hoverRow != -1)
            {
                _hoverRow = -1;
                InvalidateVisual();
            }
            return;
        }

        var row = RowAt(pos.Y);
        if (row != _hoverRow)
        {
            _hoverRow = row;
            InvalidateVisual();
        }
        UpdateHoverTooltip(pos);
    }

    private int BoundaryAt(double x)
        => GraphColumnInteraction.BoundaryAt(_resolvedColumns, x, _hScrollOffset, BoundaryGrab);

    private static IReadOnlyList<GraphColumn> WithColumnWidth(IReadOnlyList<GraphColumn> cols, GraphColumnKind kind, double width)
        => [.. cols.Select(c => c.Kind == kind ? c with { Width = width } : c)];

    private int CaretIndexAt(double x)
        => GraphColumnInteraction.CaretIndexAt(_resolvedColumns, x, _hScrollOffset);

    private GraphColumnKind? ColumnKindAt(double x)
        => GraphColumnInteraction.ColumnKindAt(_resolvedColumns, x, _hScrollOffset);

    private void ReorderColumn(GraphColumnKind moved, int caret)
    {
        var next = GraphColumnInteraction.Reorder(_columns, moved, caret);
        if (ReferenceEquals(next, _columns))
            return;

        _columns = next;
        PersistColumns();
        InvalidateArrange();
        InvalidateVisual();
    }

    private void ShowColumnMenu()
    {
        var menu = new ContextMenu();
        var items = new List<Control>();
        foreach (var col in _columns)
        {
            var kind = col.Kind;
            var mi = new MenuItem { Header = kind.ToString(), Icon = col.Visible ? new TextBlock { Text = "✓" } : null };
            mi.Click += (_, _) => ToggleColumn(kind);
            items.Add(mi);
        }
        items.Add(new Separator());
        items.Add(MenuLeaf("Reset columns", () =>
        {
            _columns = GraphColumnDefaults.All;
            _graphWidthPinned = false;
            _manuallySized.Clear();
            if (_context is { } ctx)
                _ = _repoLayout.UpdateAsync(ctx.RepositoryPath, l => l with { Columns = GraphColumnDefaults.All, GraphWidthPinned = false });
            InvalidateArrange();
            InvalidateVisual();
        }));
        menu.ItemsSource = items;
        OpenMenu(menu);
    }

    private void ToggleColumn(GraphColumnKind kind)
    {
        var next = _columns.Select(c => c.Kind == kind ? c with { Visible = !c.Visible } : c).ToList();
        if (next.Count(c => c.Visible) == 0 || next.Count(c => c.Visible && c.IsFlex) != 1)
            return;
        _columns = GraphColumnDefaults.Normalize(next);
        PersistColumns();
        InvalidateArrange();
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        if (_hoverRow != -1)
        {
            _hoverRow = -1;
            InvalidateVisual();
        }
        ToolTip.SetIsOpen(this, false);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && (_resizing || _reorderKind is not null))
        {
            _resizing = false;
            _resizeBoundary = -1;
            _resizeInverted = false;
            _reorderKind = null;
            _reorderCandidate = null;
            _reorderCaret = -1;
            _capturedPointer?.Capture(null);
            if (_preResizeColumns is not null)
                _columns = _preResizeColumns;
            InvalidateArrange();
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.F && (e.KeyModifiers & KeyModifiers.Control) != 0)
        {
            ToggleQueryBar(true);
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape && _queryBar.IsVisible)
        {
            ToggleQueryBar(false);
            e.Handled = true;
            return;
        }
        if (e.Key is Key.F3 && _matches.HasMatches)
        {
            StepMatch((e.KeyModifiers & KeyModifiers.Shift) != 0 ? -1 : 1);
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    // ── Graph query & find (§V2-17.3) ──

    private void ToggleQueryBar(bool show)
    {
        _queryBar.IsVisible = show;
        if (show)
        {
            _searchBox.Focus();
            _searchBox.SelectAll();
        }
        else
        {
            Focus();
        }
        InvalidateArrange();
        InvalidateVisual();
    }

    private void OnSearchBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            StepMatch((e.KeyModifiers & KeyModifiers.Shift) != 0 ? -1 : 1);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            ToggleQueryBar(false);
            e.Handled = true;
        }
    }

    private void RecomputeMatches()
    {
        var term = _searchBox.Text?.Trim() ?? "";

        // The store owns the scan: an in-memory array loops; a disk-backed store streams its
        // search sidecar rather than paging every record (§7.5, §7.6).
        _matches.Reset(term.Length > 0 && _layout is { } l ? l.Layout.Commits.Search(term) : []);
        if (_matches.HasMatches)
            ScrollRowIntoView(_matches.Rows[0]);

        UpdateMatchLabel();
        InvalidateVisual();
    }

    private void StepMatch(int direction)
    {
        var row = _matches.Step(direction);
        if (row < 0)
            return;
        ScrollRowIntoView(row);
        _state?.SetSelectedCommit(_layout!.Layout.Commits[row].Id);
        UpdateMatchLabel();
        InvalidateVisual();
    }

    private void UpdateMatchLabel() =>
        _matchLabel.Text = _matches.Label(!string.IsNullOrEmpty(_searchBox.Text));

    private void ScrollRowIntoView(int row)
    {
        var top = row * _rowHeight;
        var viewHeight = Math.Max(0, Bounds.Height - RowsTop);
        if (top < _scrollOffset)
            _scrollOffset = top;
        else if (top + _rowHeight > _scrollOffset + viewHeight)
            _scrollOffset = top + _rowHeight - viewHeight;
        _scrollOffset = Math.Clamp(_scrollOffset, 0, Math.Max(0, _scrollBar.Maximum));
        _scrollBar.Value = _scrollOffset;
    }

    private void ApplyQueryFromBar()
    {
        var scope = (_scopeBox.SelectedItem as ComboBoxItem)?.Tag is GraphRefScope s ? s : GraphRefScope.AllRefs;
        var path = string.IsNullOrWhiteSpace(_pathBox.Text) ? null : _pathBox.Text.Trim().Replace('\\', '/');
        var next = _query with
        {
            RefScope = scope,
            PathFilter = path,
            IncludeStashes = _stashCheck.IsChecked == true,
        };
        if (next == _query)
            return;
        _query = next;
        if (_context is { } ctx)
            _ = ComputeLayoutAsync(ctx);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        var props = e.GetCurrentPoint(this).Properties;
        if (props.IsRightButtonPressed)
        {
            OnRightButtonPressed(e);
            return;
        }
        if (!props.IsLeftButtonPressed)
            return;

        Focus();
        // When the Home panel is the mounted detail view, the first click on the graph should
        // just dismiss Home and show the working-copy ("changes") panel — it should not also
        // select whatever commit happens to sit under the pointer. The two exceptions
        // (branch/tag pill, commit node dot) are handled where the row selection is applied.
        var leavingHome = _context is not null
            && !string.Equals(
                (_nav.CurrentRoute ?? "").Split('?', 2)[0].TrimStart('/'), "repo", StringComparison.Ordinal);
        if (_context is not null)
            _nav.RequestNavigate("repo");

        var pos = e.GetPosition(this);

        if (pos.Y < HeaderHeight)
        {
            // v8.4: the cog at the right of the BRANCH / TAGS header opens this repo's settings.
            if (_context is { } cogCtx && _refsCogRect.Contains(pos))
            {
                _workspace.Focus(cogCtx);
                _nav.RequestNavigate("repo-settings");
                e.Handled = true;
                return;
            }

            var b = BoundaryAt(pos.X);
            if (b >= 0)
            {
                if (e.ClickCount == 2)
                {
                    AutoFitColumn(_resolvedColumns[b].Kind);
                }
                else
                {
                    var leftIsFlex = _columns.FirstOrDefault(c => c.Kind == _resolvedColumns[b].Kind)?.IsFlex == true;
                    var target = leftIsFlex && b + 1 < _resolvedColumns.Length ? b + 1 : b;
                    _resizeInverted = target != b;
                    _resizing = true;
                    _resizeBoundary = b;
                    _resizeKind = _resolvedColumns[target].Kind;
                    _resizeStartX = pos.X;
                    _resizeStartWidth = _resolvedColumns[target].Width;
                    _preResizeColumns = _columns;
                    _capturedPointer = e.Pointer;
                    e.Pointer.Capture(this);
                }
                e.Handled = true;
            }
            else
            {
                _reorderCandidate = ColumnKindAt(pos.X);
                _preResizeColumns = _columns;
                _dragStart = pos;
                e.Handled = true;
            }
            return;
        }

        _dragStart = pos;
        _dragRow = RowAt(pos.Y);
        _dragging = false;
        CloseRefList();

        foreach (var (rect, _, hidden) in _overflowChips)
        {
            if (rect.Contains(pos))
            {
                ShowOverflowMenu(hidden);
                e.Handled = true;
                return;
            }
        }

        if (_layout is not { } l || _dragRow < 0)
            return;

        if (e.ClickCount == 2 && PillAt(pos) is { } dpill
            && dpill.Deco.Type is DecorationType.LocalBranch or DecorationType.RemoteBranch)
        {
            CheckoutFromBadge(dpill.Deco);
            e.Handled = true;
            return;
        }

        var id = l.Layout.Commits[_dragRow].Id;
        var mods = e.KeyModifiers;

        if (leavingHome && (mods & (KeyModifiers.Control | KeyModifiers.Shift)) == 0)
        {
            // Dismiss-Home click: keep the selection only when the pointer is on a branch/tag
            // pill or the commit's node dot; otherwise clear it so RepositoryView shows the
            // working-copy staging ("changes") panel rather than a commit's details.
            var onPill = PillAt(pos) is not null;
            var onNode = HitTestNode(pos, _dragRow);
            if (!onPill && !onNode)
            {
                _multiSelect.Clear();
                _state?.SetSelectedCommit(null);
                e.Handled = true;
                return;
            }
        }

        if ((mods & KeyModifiers.Control) != 0)
        {
            if (!_multiSelect.Remove(_dragRow))
                _multiSelect.Add(_dragRow);
            InvalidateVisual();
        }
        else if ((mods & KeyModifiers.Shift) != 0 && _selectedRow >= 0)
        {
            _multiSelect.Clear();
            for (var r = Math.Min(_selectedRow, _dragRow); r <= Math.Max(_selectedRow, _dragRow); r++)
                _multiSelect.Add(r);
            InvalidateVisual();
        }
        else
        {
            _multiSelect.Clear();
            _state?.SetSelectedCommit(id);
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        Cursor = new Cursor(StandardCursorType.Arrow);

        if (_resizing)
        {
            _resizing = false;
            _resizeBoundary = -1;
            _resizeInverted = false;
            _manuallySized.Add(_resizeKind);
            _capturedPointer?.Capture(null);
            PersistColumns();
            return;
        }

        if (_reorderKind is { } moved)
        {
            var caret = _reorderCaret;
            _reorderKind = null;
            _reorderCandidate = null;
            _reorderCaret = -1;
            _capturedPointer?.Capture(null);
            ReorderColumn(moved, caret);
            return;
        }
        _reorderCandidate = null;

        if (_dragging && _dragRow >= 0 && _layout is { } l)
        {
            var target = PillAt(e.GetPosition(this));
            if (target is { } pill && pill.Deco.Type is DecorationType.LocalBranch or DecorationType.RemoteBranch)
                ShowDropIntentMenu(l.Layout.Commits[_dragRow].Id, l.Layout.Commits[_dragRow].Id.Sha[..7], pill.Deco.Label);
        }

        _dragging = false;
        _dragRow = -1;
    }

    private IReadOnlyList<GraphColumn>? _preResizeColumns;

    private void PersistColumns()
    {
        if (_context is not { } ctx)
            return;
        var cols = _columns;
        var pinned = _graphWidthPinned;
        _ = _repoLayout.UpdateAsync(ctx.RepositoryPath, l => l with { Columns = cols, GraphWidthPinned = pinned });
    }

    /// <summary>§V3.1: the width that shows every lane the layout uses, plus one lane of margin
    /// for the right-edge tick.</summary>
    private double NaturalGraphWidth(int lanes) => (lanes + 1) * LaneWidth;

    private void AutoFitColumn(GraphColumnKind kind)
    {
        if (_layout is not { } result)
            return;
        var layout = result.Layout;

        // §V3.1: Graph auto-fit stretches to show the whole graph at once — every lane plus one
        // lane of tick margin — bounded by the floor and the hand-edit sanity ceiling. (Without
        // this it measured an empty string and landed on its 150px floor.)
        if (kind == GraphColumnKind.Graph)
        {
            var floor = GraphColumnDefaults.MinWidthFor(GraphColumnKind.Graph);
            var fitWidth = Math.Clamp(NaturalGraphWidth(layout.TotalLaneCount), floor,
                GraphColumnDefaults.MaxWidthFor(GraphColumnKind.Graph));

            _columns = WithColumnWidth(_columns, kind, fitWidth);
            _graphWidthPinned = true;
            _zoomOverridesPin = false;
            PersistColumns();
            InvalidateArrange();
            InvalidateVisual();
            return;
        }

        var rowArea = Bounds.Height - RowsTop;
        var first = Math.Max(0, (int)(_scrollOffset / _rowHeight));
        var last = Math.Min(layout.RowCount - 1, (int)((_scrollOffset + rowArea) / _rowHeight));

        double widest = GraphColumnDefaults.MinWidthFor(kind);
        for (var row = first; row <= last; row++)
        {
            var m = layout.Commits[row];
            var s = kind switch
            {
                GraphColumnKind.Message => m.MessageShort,
                GraphColumnKind.Author => m.AuthorName,
                GraphColumnKind.Date => DateCell(m.AuthorWhen),
                GraphColumnKind.Sha => m.Id.Sha[..7],
                _ => "",
            };
            if (s.Length == 0)
                continue;
            widest = Math.Max(widest, new FormattedText(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                _typeface, RowTextSize, _text).Width + 20);
        }

        _columns = WithColumnWidth(_columns, kind, widest);
        if (kind == GraphColumnKind.Graph)
            _graphWidthPinned = true;
        PersistColumns();
        InvalidateArrange();
        InvalidateVisual();
    }

    private void ShowOverflowMenu(IReadOnlyList<GraphDecoration> hidden)
    {
        var menu = new ContextMenu();
        var items = new List<Control>();
        foreach (var d in hidden)
        {
            var deco = d;
            items.Add(MenuLeaf(d.Type == DecorationType.Head ? "HEAD" : d.Label, () => ShowPillContextMenu(deco)));
        }
        menu.ItemsSource = items;
        OpenMenu(menu);
    }

    private void OnRightButtonPressed(PointerPressedEventArgs e)
    {
        // The message-column hover tooltip is a separate popup layer from the context menu and
        // doesn't auto-dismiss just because a menu opened on top of it — right-clicking a row
        // under an already-showing tooltip left the two overlapping. Close it unconditionally
        // before any of the menus below can open.
        ToolTip.SetIsOpen(this, false);
        _msgTipText = null;

        var pos = e.GetPosition(this);

        if (pos.Y < HeaderHeight)
        {
            ShowColumnMenu();
            e.Handled = true;
            return;
        }

        foreach (var (rect, _, hidden) in _overflowChips)
        {
            if (rect.Contains(pos))
            {
                ShowOverflowMenu(hidden);
                e.Handled = true;
                return;
            }
        }

        var pill = PillAt(pos);
        if (pill is { } p)
        {
            ShowPillContextMenu(p.Deco);
            e.Handled = true;
            return;
        }

        var row = RowAt(pos.Y);
        if (row >= 0 && _layout is { } l)
        {
            _state?.SetSelectedCommit(l.Layout.Commits[row].Id);
            ShowCommitContextMenu(l.Layout.Commits[row].Id);
            e.Handled = true;
        }
    }

    private int RowAt(double y)
    {
        if (_layout is not { } l)
            return -1;
        if (y < RowsTop)
            return -1;
        var row = (int)((y - RowsTop + _scrollOffset) / _rowHeight);
        return row >= 0 && row < l.Layout.RowCount ? row : -1;
    }

    private (Rect Rect, int Row, GraphDecoration Deco)? PillAt(Point p)
    {
        foreach (var pill in _pillRects)
            if (pill.Rect.Contains(p))
                return pill;
        return null;
    }

    /// <summary>True when <paramref name="p"/> lands on (or within a small grab margin of) the
    /// commit node dot drawn for <paramref name="row"/>.</summary>
    private bool HitTestNode(Point p, int row)
    {
        if (_layout is not { } l || row < 0 || row >= l.Layout.RowCount)
            return false;
        var cx = NodeX(l.Layout.Rows[row].LaneIndex, out _);
        var cy = RowY(row) + _rowHeight / 2;
        var dx = p.X - cx;
        var dy = p.Y - cy;
        const double grab = NodeRadius + 3;
        return dx * dx + dy * dy <= grab * grab;
    }

    private string? _lastTip;

    private void UpdateHoverTooltip(Point pos)
    {
        // A context menu is a separate popup layer that doesn't suppress pointer-moved on the
        // control underneath, and the pointer is typically still sitting right over the row that
        // was just right-clicked — so without this guard, the very next hover tick reopens the
        // message tooltip on top of the menu that was just opened for that same row.
        if (_openMenu is not null)
        {
            ToolTip.SetIsOpen(this, false);
            _lastTip = null;
            _msgTipText = null;
            return;
        }

        string? tip = null;

        foreach (var p in _pillRects)
            if (p.Rect.Contains(pos) && p.Deco.SiblingsOrNull is { Length: > 0 })
            {
                OpenRefListFor(p.Deco, p.Rect);
                ToolTip.SetIsOpen(this, false);
                _lastTip = null;
                return;
            }

        foreach (var ic in _iconRects)
            if (ic.Rect.Contains(pos)) { tip = ic.Tip; break; }

        if (tip is null)
            foreach (var p in _pillRects)
                if (p.Rect.Contains(pos))
                {
                    var d = p.Deco;
                    tip = d.Type == DecorationType.Head ? "HEAD" : d.Label;
                    break;
                }

        if (tip is null)
            foreach (var (rect, _, hidden) in _overflowChips)
                if (rect.Contains(pos))
                {
                    tip = $"{hidden.Count} more:\n" + string.Join("\n", hidden.Select(d => d.Type == DecorationType.Head ? "HEAD" : d.Label));
                    break;
                }

        // v8 M16: hovering the Message column shows the whole commit message (subject + body),
        // wrapped, sized to content. Tooltip-only — OnPointerPressed / the context menu are
        // untouched, so click and right-click on the message cell still work.
        if (tip is null && _resolvedColumns is { Length: > 0 } cols)
        {
            for (var i = 0; i < cols.Length; i++)
            {
                var col = cols[i];
                if (col.Kind == GraphColumnKind.Message)
                {
                    var colX = col.Left - _hScrollOffset;
                    if (pos.X >= colX && pos.X < colX + col.Width && pos.Y >= RowsTop && pos.Y < Bounds.Height)
                    {
                        var row = (int)((pos.Y - RowsTop + _scrollOffset) / _rowHeight);
                        if (_layout is { } lay && row >= 0 && row < lay.Layout.Commits.Count)
                        {
                            var meta = lay.Layout.Commits[row];
                            ShowMessageTooltip(meta.Id, meta.MessageShort);
                            return;
                        }
                    }
                    break;
                }
            }
            _hoverMsgId = null;
        }

        if (tip is null)
        {
            ToolTip.SetIsOpen(this, false);
            _lastTip = null;
            _msgTipText = null;
            return;
        }

        if (!string.Equals(_lastTip, tip, StringComparison.Ordinal))
        {
            _lastTip = tip;
            _msgTipText = null;
            _hoverMsgId = null;
            ToolTip.SetTip(this, tip);
            ToolTip.SetIsOpen(this, false);
            ToolTip.SetIsOpen(this, true);
        }
        else
        {
            ToolTip.SetIsOpen(this, true);
        }
    }

    /// <summary>v8 M16: show the full commit message for a Message-column cell. The body is
    /// fetched lazily (debounced) via <see cref="IGitGateway.GetCommitDetailAsync"/> and cached;
    /// until it resolves the subject alone is shown.</summary>
    private void ShowMessageTooltip(ObjectId id, string subject)
    {
        var text = subject;
        if (TryGetCachedBody(id, out var body))
            text = string.IsNullOrEmpty(body) ? subject : subject + "\n\n" + body;
        else
            _ = FetchMessageBodyAsync(id, subject);

        _hoverMsgId = id;
        if (string.Equals(_msgTipText, text, StringComparison.Ordinal))
        {
            ToolTip.SetIsOpen(this, true);
            return;
        }

        _msgTipText = text;
        _lastTip = null;
        ToolTip.SetTip(this, new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 520,
        });
        ToolTip.SetIsOpen(this, false);
        ToolTip.SetIsOpen(this, true);
    }

    private async Task FetchMessageBodyAsync(ObjectId id, string subject)
    {
        if (_context is not { } ctx || _msgBodyCache.ContainsKey(id) || id.Equals(_msgFetchInFlight))
            return;
        _msgFetchInFlight = id;
        try
        {
            await Task.Delay(180, ctx.Lifetime).ConfigureAwait(true); // debounce a scroll-past
            if (!id.Equals(_hoverMsgId))
                return;
            var detail = await ctx.Gateway.GetCommitDetailAsync(id, ctx.Lifetime).ConfigureAwait(true);
            CacheMessageBody(id, detail.MessageBody ?? "");
            if (id.Equals(_hoverMsgId) && IsPointerOver)
                ShowMessageTooltip(id, subject); // re-render now that the body is in
        }
        catch { /* best-effort tooltip */ }
        finally
        {
            if (id.Equals(_msgFetchInFlight))
                _msgFetchInFlight = null;
        }
    }

    // ── §V3-B: folded-badge ref list ──

    private bool RefListBadgeHovered()
    {
        if (_refListFor is not { } d || !IsPointerOver)
            return false;
        foreach (var p in _pillRects)
            if (p.Rect.Contains(_lastPointerPos) && p.Deco.Equals(d))
                return true;
        return false;
    }

    private bool RefListHovered =>
        _refList.IsPointerOver || _refList.Child?.IsPointerOver == true;

    private void CloseRefList()
    {
        _refListCloseTimer?.Stop();
        _refList.IsOpen = false;
        _refListFor = null;
    }

    private void OpenRefListFor(GraphDecoration head, Rect badgeRect)
    {
        if (!(_refList.IsOpen && _refListFor is { } cur && cur.Equals(head)))
        {
            _refListFor = head;
            _refList.Child = BuildRefListContent(head);
            _refList.PlacementTarget = this;
            _refList.PlacementRect = badgeRect;
            _refList.Placement = PlacementMode.BottomEdgeAlignedLeft;
            _refList.IsOpen = true;
        }
        if (_refListCloseTimer is { IsEnabled: false })
            _refListCloseTimer.Start();
    }

    private Control BuildRefListContent(GraphDecoration head)
    {
        var stack = new StackPanel { Margin = new Thickness(4) };
        stack.Children.Add(RefListRow(head.Type, head.Type == DecorationType.Head ? "HEAD" : head.Label,
            head.IsLocal, head.RemotesOrNull, head.ColorIndex, head.RowIndex));
        foreach (var s in head.Siblings)
            stack.Children.Add(RefListRow(s.Type, s.Label, s.IsLocal, s.Remotes, head.ColorIndex, head.RowIndex));

        var shell = new Border
        {
            Child = stack,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(2),
            BoxShadow = BoxShadows.Parse("0 2 10 0 #40000000"),
        };
        Dyn(shell, Border.BackgroundProperty, "ColorSurface");
        Dyn(shell, Border.BorderBrushProperty, "ColorBorder");
        return shell;
    }

    private Border RefListRow(DecorationType type, string label, bool isLocal, string[]? remotes,
        int colorIndex, int row)
    {
        var checkoutable = type is DecorationType.LocalBranch or DecorationType.RemoteBranch or DecorationType.Tag;

        var swatch = new Avalonia.Controls.Shapes.Rectangle
        {
            Width = 4, Height = 14, RadiusX = 2, RadiusY = 2,
            Fill = new SolidColorBrush(ToColor(LaneRgb(colorIndex))),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var kind = new TextBlock
        {
            Text = type == DecorationType.Tag ? "tag" : type == DecorationType.Head ? "head" : isLocal ? "local" : "remote",
            FontSize = 10, Width = 44, VerticalAlignment = VerticalAlignment.Center,
        };
        Dyn(kind, TextBlock.ForegroundProperty, "ColorTextMuted");
        var name = new TextBlock { Text = label, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        Dyn(name, TextBlock.ForegroundProperty, "ColorText");
        if (remotes is { Length: > 0 })
            name.Text += $"  ({string.Join(", ", remotes)})";

        var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(swatch);
        left.Children.Add(new Border { Width = 6 });
        left.Children.Add(kind);
        left.Children.Add(name);

        // Same local/remote status glyphs as the badge itself (§V6-G2), right-aligned here so
        // the row reads consistently with the badge that this popup expanded from.
        var icons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        if (type is DecorationType.LocalBranch or DecorationType.RemoteBranch)
        {
            if (isLocal)
                icons.Children.Add(Historia.Views.ViewKit.Icon("save", 12, "ColorTextMuted"));
            if (remotes is { Length: > 0 })
                icons.Children.Add(Historia.Views.ViewKit.Icon("remote", 12, "ColorTextMuted"));
        }

        var content = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(left, 0);
        Grid.SetColumn(icons, 1);
        content.Children.Add(left);
        content.Children.Add(icons);

        var rowBorder = new Border
        {
            Child = content,
            Padding = new Thickness(6, 4, 10, 4),
            CornerRadius = new CornerRadius(4),
            Background = Brushes.Transparent,
            Cursor = new Cursor(checkoutable ? StandardCursorType.Hand : StandardCursorType.Arrow),
        };
        rowBorder.PointerEntered += (_, _) => Dyn(rowBorder, Border.BackgroundProperty, "ColorSurface2");
        rowBorder.PointerExited += (_, _) => rowBorder.Background = Brushes.Transparent;
        rowBorder.AddHandler(PointerPressedEvent, (_, e) =>
        {
            var props = e.GetCurrentPoint(rowBorder).Properties;
            if (props.IsRightButtonPressed)
            {
                // Same context menu as right-clicking this ref's badge directly on the graph —
                // the stacked-tag popup previously had no right-click menu at all.
                e.Handled = true;
                ShowPillContextMenu(new GraphDecoration
                {
                    RowIndex = row, Type = type, Label = label,
                    ColorIndex = colorIndex, IsLocal = isLocal, RemotesOrNull = remotes,
                });
                return;
            }

            e.Handled = true;
            if (e.ClickCount == 2 && checkoutable)
            {
                CheckoutRefEntry(type, label, isLocal, remotes);
                CloseRefList();
            }
            else if (_layout is { } l && row >= 0 && row < l.Layout.RowCount)
            {
                // Single click selects/reveals the tip commit but leaves the popup open, so a
                // double click on the same row can still land and check the ref out. The close
                // timer takes it down once the pointer leaves the popup and its badge.
                _state?.SetSelectedCommit(l.Layout.Commits[row].Id);
            }
        }, RoutingStrategies.Tunnel);
        return rowBorder;
    }

    // ── Render (§V2-6.3) ──

    private readonly Dictionary<int, List<GraphDecoration>> _decoIndex = [];
    private object? _decoIndexKey;
    private readonly List<(Rect Rect, int Row, List<GraphDecoration> Hidden)> _overflowChips = [];

    /// <summary>
    /// Row → decorations, built once per layout rather than re-scanned every frame. Keyed on
    /// the <see cref="GraphLayout.Decorations"/> array identity, which Compute / Recompute /
    /// Extend all replace. Frequent repaints (hover, selection, scroll) reuse it untouched.
    /// </summary>
    private Dictionary<int, List<GraphDecoration>> DecorationsByRow(GraphLayout layout)
    {
        if (ReferenceEquals(_decoIndexKey, layout.Decorations))
            return _decoIndex;

        _decoIndex.Clear();
        foreach (var d in layout.Decorations)
        {
            if (!_decoIndex.TryGetValue(d.RowIndex, out var list))
                _decoIndex[d.RowIndex] = list = [];
            list.Add(d);
        }
        _decoIndexKey = layout.Decorations;
        return _decoIndex;
    }

    private double RowY(int row) => RowsTop + row * _rowHeight - _scrollOffset;

    public override void Render(DrawingContext dc)
    {
        var size = Bounds.Size;
        dc.DrawRectangle(_bg, null, new Rect(size));

        // Resolved in ArrangeOverride each frame; re-resolve here defensively (idempotent) so a
        // Render that somehow precedes an arrange still has a band.
        ResolveColumnBand(size.Width);

        DrawHeaderBand(dc, size.Width);

        if (RevealBlanking)
        {
            // Graph body is blanked while walking toward an unrendered commit — show only the
            // target, centred. The pump InvalidateVisual()s each batch to refresh the count.
            DrawRevealPlaceholder(dc, size);
            return;
        }

        if (_layout is not { } result)
            return;

        try
        {
            RenderRows(dc, size, result);
            _renderFailStreak = 0;
        }
        catch (Exception ex) when (ex is ObjectDisposedException or IOException or InvalidDataException
                                   or ArgumentOutOfRangeException or IndexOutOfRangeException)
        {
            // The disk-backed commit store was mid-rebuild (a spill restart rewrites the cache
            // files) while this frame was indexing it. Drop the frame and repaint — the store
            // settles within a batch or two, and the alternative is an exception thrown off the
            // render thread, which Avalonia propagates straight past Dispatcher.UnhandledException
            // and terminates the process. Layer 1 (the store lease) keeps the handle from being
            // closed under us; this is the belt-and-braces for a concurrent rewrite.
            //
            // A transient cause clears within a few frames. Cap the auto-repaints so a
            // *deterministic* fault in here (a stale scroll window, a lane-index bug) surfaces
            // once and leaves a blank frame instead of spinning a core forever on Background.
            if (++_renderFailStreak <= MaxRenderRetries)
            {
                Dispatcher.UIThread.Post(InvalidateVisual, DispatcherPriority.Background);
            }
            else if (_renderFailStreak == MaxRenderRetries + 1)
            {
                Historia.Diagnostics.PerfLog.Mark(
                    $"graph Render giving up after {MaxRenderRetries} retries: {ex.GetType().Name}");
                _notifications.Publish(NotificationSeverity.Error,
                    "The commit graph hit a rendering error and stopped repainting. "
                    + "Refresh the repository to retry.",
                    ex.ToString());
            }
        }
    }

    private void RenderRows(DrawingContext dc, Size size, GraphLayoutResult result)
    {
        var layout = result.Layout;
        var rowArea = size.Height - RowsTop;
        var first = Math.Max(0, (int)(_scrollOffset / _rowHeight) - 2);
        var last = Math.Min(layout.RowCount - 1, (int)((_scrollOffset + rowArea) / _rowHeight) + 2);
        if (last < first)
            return;

        // Fault the visible block of a disk-backed store in with one read before the column
        // passes start indexing it row by row (§7.6). A no-op for the in-memory store.
        layout.Commits.Prefetch(first, last);

        _pillRects.Clear();
        _iconRects.Clear();
        _overflowChips.Clear();
        _badgeRightEdge.Clear();

        var currentMatchRow = _matches.CurrentRow;
        var matchSet = _matches.RowSet;
        var gx = _graphColLeft - _hScrollOffset;
        var washRight = gx + Math.Max(0, _graphColWidth - TickWidth);
        // Clip the row-background band to the row area: the first drawn row starts two rows
        // above the viewport top, so its fill would otherwise bleed up into the header band.
        using (dc.PushClip(new Rect(0, RowsTop, size.Width, Math.Max(0, rowArea))))
        for (var row = first; row <= last; row++)
        {
            var y = RowY(row);
            if (matchSet is not null && matchSet.Contains(row))
                dc.DrawRectangle(_matchFill, null, new Rect(0, y, size.Width, _rowHeight));

            var selected = row == _selectedRow || _multiSelect.Contains(row);
            if (selected)
                dc.DrawRectangle(_selectionFill, null, new Rect(0, y, size.Width, _rowHeight));
            else if (row == _hoverRow)
                dc.DrawRectangle(_hoverFill, null, new Rect(0, y, size.Width, _rowHeight));

            if (_graphColWidth > 0)
            {
                var gr = layout.Rows[row];
                var colorIndex = gr.ColorIndex;
                // §V3.2: the wash starts at the commit's own node — the graph line it sits on —
                // and runs right to the column edge, rather than beginning half a lane to its
                // left and covering the whole lane band.
                var fillLeft = Math.Max(gx, NodeX(gr.LaneIndex, out _));
                if (washRight > fillLeft)
                    dc.DrawRectangle(LaneFill(colorIndex), null, new Rect(fillLeft, y, washRight - fillLeft, _rowHeight));
                // The solid right-edge tick is drawn in DrawGraphColumn, AFTER the edges, so a
                // lane line routed near the column's right edge can't paint over it.
            }

            if (selected)
                dc.DrawRectangle(_selectionBar, null, new Rect(0, y, 2, _rowHeight));

            if (row == currentMatchRow)
                dc.DrawRectangle(_selectionBar, null, new Rect(0, y, 3, _rowHeight));
        }

        var decosByRow = DecorationsByRow(layout);

        // Badge→node connectors: after the lane wash (drawn in the row loop above) but BEFORE
        // the column loop, so the graph's lane lines paint over them and the dots (a later
        // pass) stay on top.
        DrawBadgeConnectors(dc, layout, decosByRow, first, last, rowArea);

        foreach (var col in _resolvedColumns)
        {
            var x = col.Left - _hScrollOffset;
            if (x + col.Width < 0 || x > size.Width)
                continue;

            using (dc.PushClip(new Rect(x, RowsTop, Math.Max(0, col.Width), Math.Max(0, rowArea))))
            {
                switch (col.Kind)
                {
                    case GraphColumnKind.Graph:
                        DrawGraphColumn(dc, layout, first, last, x, size.Width);
                        break;
                    case GraphColumnKind.Refs:
                        DrawRefsColumn(dc, decosByRow, first, last, x, col.Width);
                        break;
                    case GraphColumnKind.Message:
                        DrawTextColumn(dc, layout, first, last, x, col.Width, m => m.MessageShort, _text);
                        break;
                    case GraphColumnKind.Author:
                        DrawTextColumn(dc, layout, first, last, x, col.Width, m => m.AuthorName, _textMuted);
                        break;
                    case GraphColumnKind.Date:
                        DrawTextColumn(dc, layout, first, last, x, col.Width, m => DateCell(m.AuthorWhen), _textMuted);
                        break;
                    case GraphColumnKind.Sha:
                        DrawTextColumn(dc, layout, first, last, x, col.Width, m => m.Id.Sha[..7], _textMuted);
                        break;
                }
            }
        }

        // Commit dots last, clipped to the Graph column, so the badge→node connectors sit
        // behind them.
        foreach (var col in _resolvedColumns)
        {
            if (col.Kind != GraphColumnKind.Graph)
                continue;
            var gcx = col.Left - _hScrollOffset;
            if (gcx + col.Width >= 0 && gcx <= size.Width)
                using (dc.PushClip(new Rect(gcx, RowsTop, Math.Max(0, col.Width), Math.Max(0, rowArea))))
                    DrawGraphNodes(dc, layout, first, last);
            break;
        }
    }

    /// <summary>§V6-G2: when the Branch/Tags column sits immediately left of the Graph column,
    /// draw a short horizontal line from each row's primary badge to its commit node. The
    /// checked-out branch's row gets the full lane line (colour + <see cref="LaneStrokeWidth"/>);
    /// every other decorated row gets a faint 1 DIP hairline in the same lane colour. Runs
    /// BEFORE the column loop (after the lane wash) so the graph's lane lines paint over the
    /// connectors and the dots stay on top; its own clip spans only the Refs↔Graph gap. Cheap
    /// enough not to cache; reads <c>_graphColLeft</c> fresh.</summary>
    private void DrawBadgeConnectors(DrawingContext dc, GraphLayout layout,
        Dictionary<int, List<GraphDecoration>> decosByRow, int first, int last, double rowArea)
    {
        if (RevealBlanking || _graphColWidth <= 0 || _resolvedColumns.Length == 0)
            return;

        var refsIdx = Array.FindIndex(_resolvedColumns, c => c.Kind == GraphColumnKind.Refs);
        var graphIdx = Array.FindIndex(_resolvedColumns, c => c.Kind == GraphColumnKind.Graph);
        if (refsIdx < 0 || graphIdx != refsIdx + 1)
            return;

        var refsCol = _resolvedColumns[refsIdx];
        var refsLeft = refsCol.Left - _hScrollOffset;
        var refsRightFallback = refsCol.Left + refsCol.Width - 8 - _hScrollOffset;
        var clipRight = _graphColLeft + _graphColWidth - _hScrollOffset;
        if (clipRight <= refsLeft)
            return;

        // The Refs column has not painted yet this frame, so measure where each row's primary
        // badge will end (the connector start) now.
        MeasureBadgeRightEdges(decosByRow, first, last, refsLeft, refsCol.Width);

        using (dc.PushClip(new Rect(refsLeft, RowsTop, clipRight - refsLeft, Math.Max(0, rowArea))))
        {
            for (var row = first; row <= last; row++)
            {
                if (!decosByRow.TryGetValue(row, out var decos) || decos.Count == 0)
                    continue;

                var gr = layout.Rows[row];
                var startX = _badgeRightEdge.GetValueOrDefault(row, refsRightFallback);
                var endX = NodeX(gr.LaneIndex, out _);
                if (endX <= startX + 0.5)
                    continue;

                var y = RowY(row) + _rowHeight / 2;
                var isHead = (gr.Flags & RowFlags.IsHead) != 0 || decos.Any(d => d.IsHead);
                var pen = isHead ? LanePen(gr.ColorIndex) : FaintConnectorPen(gr.ColorIndex);
                dc.DrawLine(pen, new Point(startX, y), new Point(endX, y));
            }
        }
    }

    private void DrawHeaderBand(DrawingContext dc, double width)
    {
        dc.DrawRectangle(_chrome, null, new Rect(0, 0, width, HeaderHeight));
        dc.DrawLine(_borderPen, new Point(0, HeaderHeight - 0.5), new Point(width, HeaderHeight - 0.5));

        _refsCogRect = default;

        foreach (var col in _resolvedColumns)
        {
            var x = col.Left - _hScrollOffset;
            if (x + col.Width < 0 || x > width)
                continue;

            var label = col.Kind switch
            {
                GraphColumnKind.Refs => "BRANCH / TAGS",
                GraphColumnKind.Graph => "GRAPH",
                GraphColumnKind.Message => "MESSAGE",
                GraphColumnKind.Author => "AUTHOR",
                GraphColumnKind.Date => "DATE",
                GraphColumnKind.Sha => "SHA",
                _ => "",
            };
            // v8.4: the repo-settings cog lives at the right edge of the BRANCH / TAGS header
            // (it used to be a per-tab button). Reserve room so the label ellipsises before it.
            var cogRoom = col.Kind == GraphColumnKind.Refs && _context is not null ? CogSize + 12 : 0;
            var ft = Text(label, _headerText, Math.Max(10, col.Width - 12 - cogRoom), HeaderTextSize);
            dc.DrawText(ft, new Point(x + 8, (HeaderHeight - ft.Height) / 2));

            if (cogRoom > 0)
            {
                var cx = x + col.Width - CogSize - 8;
                var cy = (HeaderHeight - CogSize) / 2;
                _refsCogRect = new Rect(cx - 3, cy - 3, CogSize + 6, CogSize + 6);
                var s = CogSize / 24.0;
                using (dc.PushTransform(Matrix.CreateScale(s, s) * Matrix.CreateTranslation(cx, cy)))
                    dc.DrawGeometry(null, _cogPen, _cogGeo);
            }

            if (col.Left > 0.5)
                dc.DrawLine(_borderPen, new Point(x, 4), new Point(x, HeaderHeight - 4));
        }

        if (_reorderKind is { } rk)
        {
            var ghost = Text(rk.ToString().ToUpperInvariant(), _text, size: HeaderTextSize);
            dc.DrawRectangle(_hoverFill, _borderPen, new Rect(_reorderGhostX - 40, 2, 80, HeaderHeight - 4));
            dc.DrawText(ghost, new Point(_reorderGhostX - 36, (HeaderHeight - ghost.Height) / 2));

            if (_reorderCaret >= 0)
            {
                double caretX = _reorderCaret < _resolvedColumns.Length
                    ? _resolvedColumns[_reorderCaret].Left - _hScrollOffset
                    : _bandWidth - _hScrollOffset;
                dc.DrawRectangle(_selectionBar, null, new Rect(caretX - 1, 0, 2, HeaderHeight));
            }
        }
    }

    /// <summary>The blanked-graph placeholder: a stand-in ref badge centred in the Branch/Tags
    /// column, and a borderless "Loading history… N rows" line just below it. Shown while the
    /// reveal pump walks toward an as-yet-unrendered commit (§7.3).</summary>
    private void DrawRevealPlaceholder(DrawingContext dc, Size size)
    {
        const double h = 20;
        var rowAreaH = Math.Max(0, size.Height - RowsTop);
        var midY = RowsTop + rowAreaH / 2;
        var top = midY - h / 2;

        // Keep the column boundaries visible so the placeholder badge reads as sitting in the
        // Branch/Tags column.
        foreach (var c in _resolvedColumns)
        {
            var lx = c.Left - _hScrollOffset;
            if (c.Left > 0.5 && lx >= 0 && lx <= size.Width)
                dc.DrawLine(_borderPen, new Point(lx, RowsTop), new Point(lx, size.Height));
        }

        if (_revealDeco is { } deco)
        {
            // Centre it in the Refs column when that column is shown, else on the whole width.
            double refLeft = 0, refWidth = size.Width;
            foreach (var c in _resolvedColumns)
                if (c.Kind == GraphColumnKind.Refs) { refLeft = c.Left - _hScrollOffset; refWidth = c.Width; }

            var (_, natural, min) = BadgeMetrics(deco);
            var w = Math.Clamp(natural, min, Math.Max(min, refWidth - 16));
            var x = refLeft + (refWidth - w) / 2;

            DrawBadge(dc, deco, x, top, w, h, midY, row: -1);
            _pillRects.Clear();   // the placeholder badge is decorative — no hit-testing
            _iconRects.Clear();
        }

        // ~20% below the vertical centre, so it clears the badge even when the Refs column is
        // in the middle of the window.
        var sub = Text($"Loading history…  {(_layout?.Layout.RowCount ?? 0):N0} rows",
            _textMuted, size.Width - 80, HeaderTextSize);
        dc.DrawText(sub, new Point((size.Width - sub.Width) / 2, RowsTop + rowAreaH * 0.70));
    }

    private void DrawGraphColumn(DrawingContext dc, GraphLayout layout, int first, int last, double colX, double viewportWidth)
    {
        var hoverLane = _hoverRow >= 0 && _hoverRow < layout.RowCount ? layout.Rows[_hoverRow].LaneIndex : -1;

        // §V3.1: cull lane geometry against the visible span — the Graph column intersected with
        // the viewport — with a small margin for the pen width. A stretched or panned column
        // "contains" every lane, so culling against the column edge alone would skip nothing;
        // this stops issuing draw calls for lines nobody can see.
        var minVisibleX = Math.Max(colX, 0) - 4;
        var maxVisibleX = Math.Min(colX + _graphColWidth, viewportWidth) + 4;

        // Draw segments and elbows directly for visible rows in O(visible rows) time
        for (var row = first; row <= last; row++)
        {
            var y = RowY(row);
            var yMid = y + _rowHeight / 2;

            var segments = layout.GetRowSegments(row);
            if (!segments.IsEmpty)
            {
                foreach (var seg in segments)
                {
                    var x = LaneX(seg.Lane);
                    if (x < minVisibleX || x > maxVisibleX)
                        continue;

                    var isEmph = hoverLane >= 0 && seg.Lane == hoverLane;
                    var pen = isEmph ? EmphPen(seg.ColorIndex) : LanePen(seg.ColorIndex);

                    if (seg.Type == LaneSegmentType.Full)
                        dc.DrawLine(pen, new Point(x, y), new Point(x, y + _rowHeight));
                    else if (seg.Type == LaneSegmentType.TopToNode)
                        dc.DrawLine(pen, new Point(x, y), new Point(x, yMid));
                    else if (seg.Type == LaneSegmentType.NodeToBottom)
                        dc.DrawLine(pen, new Point(x, yMid), new Point(x, y + _rowHeight));
                }
            }

            var elbows = layout.GetRowElbows(row);
            if (!elbows.IsEmpty)
            {
                foreach (var elbow in elbows)
                {
                    var fromX = LaneX(elbow.FromLane);
                    var toX = LaneX(elbow.ToLane);
                    // An elbow spans [min(fromX, toX), max(fromX, toX)]; skip it only when that
                    // whole span is outside the visible span.
                    if (Math.Max(fromX, toX) < minVisibleX || Math.Min(fromX, toX) > maxVisibleX)
                        continue;

                    var isEmph = hoverLane >= 0 && (elbow.FromLane == hoverLane || elbow.ToLane == hoverLane);
                    var pen = isEmph ? EmphPen(elbow.ColorIndex) : LanePen(elbow.ColorIndex);

                    DrawElbow(dc, pen, elbow.FromLane, elbow.ToLane, fromX, y, elbow.Kind);
                }
            }
        }

        // The right-edge lane tick sits ON TOP of the lines
        if (_graphColWidth > 0)
        {
            var tickX = colX + _graphColWidth - TickWidth;
            for (var row = first; row <= last; row++)
                dc.DrawRectangle(LaneTick(layout.Rows[row].ColorIndex), null,
                    new Rect(tickX, RowY(row), TickWidth, _rowHeight));
        }
    }

    /// <summary>The commit dots — a pass after <see cref="DrawBadgeConnectors"/> so the
    /// connector lines render behind them. Every dot is the same size and solid-filled in its
    /// lane colour (§V6).</summary>
    private void DrawGraphNodes(DrawingContext dc, GraphLayout layout, int first, int last)
    {
        for (var row = first; row <= last; row++)
        {
            var gr = layout.Rows[row];
            var cx = NodeX(gr.LaneIndex, out var clamped);
            var cy = RowY(row) + _rowHeight / 2;

            dc.DrawEllipse(LaneBrush(gr.ColorIndex), clamped ? _clampedNodePen : null,
                new Point(cx, cy), NodeRadius, NodeRadius);
            if (row == _hoverRow)
                dc.DrawEllipse(null, _hoverPen, new Point(cx, cy), NodeRadius + 2, NodeRadius + 2);
        }
    }

    // §V3.1: _hScrollOffset scrolls the whole column band; _graphPanOffset scrolls only the
    // lanes inside the Graph column. Culling, NodeX edge-clamping, the lane wash, badge
    // connectors and hover emphasis all derive from LaneX, so they follow the pan automatically.
    private double LaneX(int lane) => _graphColLeft + (lane + 0.5) * LaneWidth - _hScrollOffset - _graphPanOffset;

    /// <summary>§V3.1: pan just enough to bring <paramref name="lane"/> inside the visible part
    /// of the Graph column, leaving one lane of margin. A no-op when the lane already shows or
    /// nothing is hidden. Never called from hover.</summary>
    private void EnsureLaneInView(int lane)
    {
        if (MaxPan <= 0)
        {
            if (_graphPanOffset != 0) { _graphPanOffset = 0; InvalidateVisual(); }
            return;
        }
        var left = lane * LaneWidth;                    // lane's left edge in pan space
        var right = left + LaneWidth;
        var view = _graphColWidth - TickWidth;
        if (left < _graphPanOffset)
            _graphPanOffset = left - LaneWidth;         // one lane of margin
        else if (right > _graphPanOffset + view)
            _graphPanOffset = right - view + LaneWidth;
        ClampPan();
        InvalidateVisual();
    }

    /// <summary>§V3.1: screen x of a commit node — its lane centre, or pinned just inside the
    /// Graph column's left/right edge when that lane has been <b>panned</b> out of view. The
    /// single source of a node's x: the drawn dot, its hit area, its lane wash and its badge
    /// connector all read from here so they always agree.
    /// <para>§V3.2: the edge-clamp only fires while the lanes are panned (<c>_graphPanOffset
    /// != 0</c>). Merely narrowing the Graph column with the divider lets the rightmost dots
    /// clip with the column instead of piling up against its edge.</para></summary>
    private double NodeX(int laneIndex, out bool clamped)
    {
        var naturalX = LaneX(laneIndex);
        clamped = false;
        if (_graphColWidth <= 0 || _graphPanOffset == 0)
            return naturalX;

        var gcx = _graphColLeft - _hScrollOffset;
        var minX = gcx + NodeRadius + 2;
        var maxX = gcx + _graphColWidth - TickWidth - NodeRadius - 2;
        if (naturalX > maxX) { clamped = true; return maxX; }
        if (naturalX < minX) { clamped = true; return minX; }
        return naturalX;
    }

    private const double IconBox = 12;
    private const double IconGap = 4;
    private const double BadgePadX = 2;
    private const double BadgeClusterGap = 4;
    /// <summary>Ref-badge / overflow-chip corner radius — near-square (was 7).</summary>
    private const double BadgeCornerRadius = 2;
    private const double BadgeMinTextWidth = 10;
    private const double BadgeTextSlack = 1;
    private const double BadgeMinBranchWidth = 130;

    private int BadgeIconCount(GraphDecoration d) => d.Type switch
    {
        DecorationType.Tag => 1,
        DecorationType.LocalBranch or DecorationType.RemoteBranch =>
            (d.IsLocal ? 1 : 0) + (d.Remotes.Length > 0 ? 1 : 0),
        _ => 0,
    };

    private static bool IsBranchBadge(GraphDecoration d) =>
        d.Type is DecorationType.LocalBranch or DecorationType.RemoteBranch or DecorationType.Head;

    private static double IconClusterWidth(int icons) =>
        icons <= 0 ? 0 : icons * IconBox + (icons - 1) * IconGap;

    private static string? BadgePlusText(GraphDecoration d) =>
        d.SiblingsOrNull is { Length: > 0 } sibs ? $"+{sibs.Length + 1}" : null;

    private (double RightExtras, double Natural, double Min) BadgeMetrics(GraphDecoration d)
    {
        var label = d.Type == DecorationType.Head ? "HEAD" : d.Label;
        var labelW = Text(label, _pillText, size: BadgeLabelSize).Width;

        var icons = BadgeIconCount(d);
        var plus = BadgePlusText(d);
        var plusW = plus is null ? 0 : Text(plus, _pillText, size: BadgeLabelSize).Width;

        var rightExtras =
            (icons > 0 ? BadgeClusterGap + IconClusterWidth(icons) : 0) +
            (plus is not null ? BadgeClusterGap + plusW : 0);

        var content = BadgePadX + labelW + BadgeTextSlack + rightExtras + BadgePadX;
        var natural = IsBranchBadge(d) ? Math.Max(BadgeMinBranchWidth, content) : content;
        var min = BadgePadX + BadgeMinTextWidth + rightExtras + BadgePadX;
        return (rightExtras, natural, min);
    }

    /// <summary>§V6-G2: record, per row, the screen-X right edge of the primary badge — the
    /// start point for <see cref="DrawBadgeConnectors"/>, which now runs before the Refs column
    /// paints. Mirrors the primary-badge width calc in <see cref="DrawRefsColumn"/> exactly.</summary>
    private void MeasureBadgeRightEdges(Dictionary<int, List<GraphDecoration>> decosByRow,
        int first, int last, double colX, double colWidth)
    {
        var startX = colX + 8;
        var rightLimit = colX + colWidth - 8;
        for (var row = first; row <= last; row++)
        {
            if (!decosByRow.TryGetValue(row, out var decos) || decos.Count == 0)
                continue;
            var (_, natural, min) = BadgeMetrics(decos[0]);
            var reserve = decos.Count > 1 ? 32.0 : 0.0;
            var room = rightLimit - startX - reserve;
            _badgeRightEdge[row] = startX + Math.Min(natural, Math.Max(min, room));
        }
    }

    private void DrawRefsColumn(DrawingContext dc, Dictionary<int, List<GraphDecoration>> decosByRow,
        int first, int last, double colX, double colWidth)
    {
        var h = Math.Min(20, _rowHeight - 6);
        var startX = colX + 8;
        var rightLimit = colX + colWidth - 8;

        for (var row = first; row <= last; row++)
        {
            if (!decosByRow.TryGetValue(row, out var decos) || decos.Count == 0)
                continue;

            var top = RowY(row) + (_rowHeight - h) / 2;
            var midY = top + h / 2;

            var drawn = new List<(GraphDecoration D, double W)>();
            var x = startX;
            for (var i = 0; i < decos.Count; i++)
            {
                var (_, natural, min) = BadgeMetrics(decos[i]);
                var reserve = i < decos.Count - 1 ? 32 : 0;
                var room = rightLimit - x - reserve;
                if (i == 0)
                {
                    var w0 = Math.Min(natural, Math.Max(min, room));
                    drawn.Add((decos[i], w0));
                    x += w0 + 4;
                    continue;
                }
                if (natural > room)
                    break;
                drawn.Add((decos[i], natural));
                x += natural + 4;
            }

            var bx = startX;
            foreach (var (d, w) in drawn)
            {
                DrawBadge(dc, d, bx, top, w, h, midY, row);
                bx += w + 4;
            }

            // §V6-G2: the primary badge's right edge (the connector start) is measured up front
            // in MeasureBadgeRightEdges — DrawBadgeConnectors runs before this column now.

            if (drawn.Count < decos.Count)
            {
                var hidden = decos.GetRange(drawn.Count, decos.Count - drawn.Count);
                var ft = Text($"+{hidden.Count}", _textMuted, size: BadgeLabelSize);
                var chipW = ft.Width + 10;
                var chipX = colX + colWidth - 4 - chipW;
                var chipRect = new Rect(chipX, top, chipW, h);
                dc.DrawRectangle(_hoverFill, _borderPen, chipRect, BadgeCornerRadius, BadgeCornerRadius);
                dc.DrawText(ft, new Point(chipX + 5, CenteredTextY(ft, top, h, BadgeLabelSize)));
                _overflowChips.Add((chipRect, row, hidden));
            }
        }
    }

    /// <summary>Top-left Y for <c>dc.DrawText</c> so the text's cap band is optically centred
    /// in a <paramref name="bandH"/>-tall band at <paramref name="bandTop"/> — independent of
    /// the font's line-box padding, which <c>(bandH - ft.Height) / 2</c> ignores.</summary>
    private static double CenteredTextY(FormattedText ft, double bandTop, double bandH, double size)
        => bandTop + bandH / 2 + size * 0.35 - ft.Baseline;

    private void DrawBadge(DrawingContext dc, GraphDecoration d, double x, double top, double w,
        double h, double midY, int row)
    {
        var rect = new Rect(x, top, w, h);
        var headRing = d.IsHead || d.Type == DecorationType.Head;
        // §V6-G1: solid opaque lane-colour fill + same-hue border.
        dc.DrawRectangle(
            LaneBrush(d.ColorIndex),
            headRing ? _hoverPen : PillBorder(d.ColorIndex),
            rect, BadgeCornerRadius, BadgeCornerRadius);

        var ink = BadgeInk(d.ColorIndex);
        var (rightExtras, _, _) = BadgeMetrics(d);
        var plus = BadgePlusText(d);
        var plusW = plus is null ? 0 : Text(plus, ink, size: BadgeLabelSize).Width;
        var icons = BadgeIconCount(d);

        var label = d.Type == DecorationType.Head ? "HEAD" : d.Label;
        var textBudget = Math.Max(4, w - BadgePadX * 2 - rightExtras);
        var ft = Text(label, ink, textBudget, BadgeLabelSize);
        dc.DrawText(ft, new Point(x + BadgePadX, CenteredTextY(ft, top, h, BadgeLabelSize)));
        _pillRects.Add((rect, row, d));

        if (plus is not null)
        {
            var pf = Text(plus, ink, size: BadgeLabelSize);
            dc.DrawText(pf, new Point(x + w - BadgePadX - plusW, CenteredTextY(pf, top, h, BadgeLabelSize)));
        }

        // §V6-G2 / v8 M2: the icon cluster and the +N counter are always laid out from the badge's
        // right edge with a fixed order and a guaranteed gap, so the cloud glyph can never be
        // clipped or hidden behind +N. If the badge is pathologically narrow, the label is what
        // yields (its budget already floors low), never the right-edge cluster.
        var ix = x + w - BadgePadX - (plus is not null ? plusW + BadgeClusterGap : 0) - IconClusterWidth(icons);
        ix = Math.Max(ix, x + BadgePadX + Math.Min(ft.Width, BadgeMinTextWidth));
        var iy = midY - 6;
        if (d.Type == DecorationType.Tag)
        {
            DrawBadgeIcon(dc, _icoTag, ix, iy, ink);
        }
        else if (d.Type is DecorationType.LocalBranch or DecorationType.RemoteBranch)
        {
            if (d.IsLocal)
            {
                DrawBadgeIcon(dc, _icoLocal, ix, iy, ink);
                _iconRects.Add((new Rect(ix, iy, IconBox, IconBox), "local"));
                ix += IconBox + IconGap;
            }
            if (d.Remotes.Length > 0)
            {
                DrawBadgeIcon(dc, _icoRemote, ix, iy, ink);
                _iconRects.Add((new Rect(ix, iy, IconBox, IconBox), string.Join(", ", d.RemotesOrNull!)));
            }
        }
    }

    private void DrawBadgeIcon(DrawingContext dc, Geometry g, double x, double y, IBrush ink)
    {
        using (dc.PushTransform(Matrix.CreateTranslation(x, y)))
            dc.DrawGeometry(ink, null, g);
    }

    private void DrawTextColumn(DrawingContext dc, GraphLayout layout, int first, int last,
        double colX, double colWidth, Func<CommitMetadata, string> select, IBrush brush)
    {
        var maxW = Math.Max(20, colWidth - 12);
        for (var row = first; row <= last; row++)
        {
            var meta = layout.Commits[row];
            var text = select(meta);
            var ft = Text(text, brush, maxW);
            var midY = RowY(row) + (_rowHeight - ft.Height) / 2;
            dc.DrawText(ft, new Point(colX + 8, midY));
        }
    }

    private readonly Dictionary<(int LaneDelta, ElbowKind Kind), StreamGeometry> _elbowGeometryCache = new();

    private StreamGeometry GetElbowGeometry(int laneDelta, ElbowKind kind)
    {
        if (_elbowGeometryCache.TryGetValue((laneDelta, kind), out var cached))
            return cached;

        var geo = new StreamGeometry();
        var dx = laneDelta * _laneWidth;
        var midY = _rowHeight / 2.0;
        var sx = dx >= 0 ? 1.0 : -1.0;
        // One rounded corner joining a vertical run in one lane to a horizontal run at node
        // height. The vertical run on this row is midY long, the horizontal at most |dx|.
        var r = Math.Min(CornerRadius, Math.Min(Math.Abs(dx) / 2.0, midY));

        using (var ctx = geo.Open())
        {
            if (kind == ElbowKind.LeaveBottom)
            {
                // Merge edge, drawn on the merge commit's own row. The merged-in branch's line
                // rises up its own lane (ToLane, at x = dx) from the row below, turns at node
                // height, and runs horizontally into the merge commit's node in FromLane (x = 0).
                // Up from the source, then toward the target.
                ctx.BeginFigure(new Point(dx, _rowHeight), isFilled: false);
                ctx.LineTo(new Point(dx, midY + r));
                ctx.QuadraticBezierTo(new Point(dx, midY), new Point(dx - sx * r, midY));
                ctx.LineTo(new Point(0, midY));
            }
            else
            {
                // Branch-out, drawn on the parent commit's row. The new commit's line falls down
                // its own lane (FromLane, at x = 0) from the row above, turns at node height, and
                // runs horizontally into the parent commit's node in ToLane (x = dx). Down from
                // the new commit, then toward the parent.
                ctx.BeginFigure(new Point(0, 0), isFilled: false);
                ctx.LineTo(new Point(0, midY - r));
                ctx.QuadraticBezierTo(new Point(0, midY), new Point(sx * r, midY));
                ctx.LineTo(new Point(dx, midY));
            }
            ctx.EndFigure(false);
        }

        _elbowGeometryCache[(laneDelta, kind)] = geo;
        return geo;
    }

    private void DrawElbow(DrawingContext dc, Pen pen, int fromLane, int toLane, double fromX, double topY, ElbowKind kind)
    {
        var geo = GetElbowGeometry(toLane - fromLane, kind);
        using (dc.PushTransform(Matrix.CreateTranslation(fromX, topY)))
            dc.DrawGeometry(null, pen, geo);
    }

    private FormattedText Text(string s, IBrush brush, double maxWidth = double.PositiveInfinity, double size = RowTextSize)
    {
        var tag = TextTag(brush);
        var wKey = double.IsPositiveInfinity(maxWidth) ? -1 : (int)maxWidth;
        var key = (tag, (int)size, wKey, s);
        if (_textCache.TryGetValue(key, out var ft))
            return ft;
        if (_textCache.Count > 10000)
            _textCache.Clear();
        ft = new FormattedText(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, _typeface, size, brush)
        {
            Trimming = TextTrimming.CharacterEllipsis,
            MaxLineCount = 1,
        };
        if (!double.IsPositiveInfinity(maxWidth))
            ft.MaxTextWidth = Math.Max(4, maxWidth);
        _textCache[key] = ft;
        return ft;
    }

    private static string Relative(DateTimeOffset when)
    {
        var d = DateTimeOffset.Now - when;
        if (d.TotalMinutes < 60) return $"{Math.Max(1, (int)d.TotalMinutes)}m";
        if (d.TotalHours < 24) return $"{(int)d.TotalHours}h";
        if (d.TotalDays < 30) return $"{(int)d.TotalDays}d";
        return when.ToString("yyyy-MM-dd");
    }

    // v8.4: the Date column shows the full local wall-clock timestamp plus the "how long ago"
    // relative form in parentheses — e.g. "09/05/2026 - 02:30 PM (17h)".
    private static string DateCell(DateTimeOffset when)
        => when.ToLocalTime().ToString("MM/dd/yyyy - hh:mm tt", CultureInfo.InvariantCulture)
           + $" ({Relative(when)})";
}
