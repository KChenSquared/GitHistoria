namespace Historia.Controls;

/// <summary>
/// The window's app band (Row 0): the branded app-menu button plus the repo tab
/// strip. Native control, not a WebView — the app menu drops over the sidebar and the graph.
/// Bound to <see cref="IWorkspaceService"/>.
/// </summary>
public sealed class AppBandControl : UserControl, IDisposable
{
    private const double RowHeight = 31;
    // Tab width is a user setting (AppSettings.TabWidthMode). Dynamic (default) sizes each
    // tab to its label, capping the label at TabNameMaxChars so a long name only stretches the
    // tab so far. Fixed uses AppSettings.FixedTabWidth. TabWidth is the pre-layout fallback for
    // overflow maths / drag reorder and the Dynamic-mode fallback estimate.
    private const double TabWidth = 186;
    private const int TabNameMaxChars = 25;
    private const double ApproxCharPx = 7.2;
    private const double DragThreshold = 4;

    private IWorkspaceService _workspace = default!;
    private IAutoFetchService _autoFetch = default!;
    private ITerminalSessionService _terminals = default!;
    private INavigationBus _nav = default!;
    private IProfileService _profiles = default!;
    private IServiceProvider _services = default!;
    private ISettingsService _settings = default!;
    private Historia.Services.UI.IExternalLauncher _launcher = default!;
    private Historia.Services.UI.IScrollDirectionProbe _scrollProbe = default!;
    private ICloneService _cloneService = default!;
    private IUnsavedWorkRegistry? _unsavedWork;
    private IDialogService? _dialogs;

    private EventHandler<OpenRepositoriesChangedEventArgs>? _onOpenChanged;
    private EventHandler<FocusedRepositoryChangedEventArgs>? _onFocusChanged;
    private EventHandler? _onProfilesChanged;
    private EventHandler<SettingsChangedEventArgs>? _onSettingsChanged;
    private EventHandler? _onAutoFetchStates;
    private Action? _onAutoFetchDebug;
    private EventHandler? _onCloneChanged;
    private bool _cloneTabWasPresent;
    private TextBlock? _pendingCloneStatusText;
    private Button? _pendingCloneCloseButton;
    private bool _disposed;

    // dev visuals: per-open-repo left-edge colour bar + its last-applied token (skip re-binds).
    private readonly Dictionary<RepositoryContext, (Border Bar, string Token)> _autoFetchBars = [];

    private readonly DockPanel _dock;
    private readonly WrapPanel _tabPanel;
    private readonly ScrollViewer _scroller;
    private readonly RepeatButton _left;
    private readonly RepeatButton _right;
    private readonly Button _add;
    private readonly Control _brand;
    private readonly Button _hamburger;
    private readonly Image _brandMark;
    private readonly ContextMenu _appMenu;
    private readonly ContextMenu _addMenu;

    private readonly List<Border> _tabViews = [];
    internal IReadOnlyList<Border> TabViewsForTesting => _tabViews;
    private const double EdgeScrollZone = 28;
    private const double EdgeScrollStep = 14;
    private Border? _dragView;
    private Point _pressPanelPos;
    private bool _dragging;
    private int _dragFromIndex = -1;
    private int _edgeDir;
    private DispatcherTimer? _edgeTimer;
    private Point _grabOffset;
    private Point _lastPanelPos;
    private AppBandDragGhost? _dragGhost;
    // Reused across the per-pointer-move ReorderTo calls so the hot drag path stays allocation-free.
    private readonly List<double> _reorderWidths = new();
    // the dragged tab's group span in _tabViews index terms, captured on press. For an
    // owner/standalone drag this is its own group (itself plus its linked children, if any); for
    // a linked-child drag this is its OWNER's group, since ReorderTo clamps the child's live
    // target into that span. -1/0 when the pressed tab isn't a group member (no clamping needed).
    private int _dragGroupStart = -1;
    private int _dragGroupCount;
    // Reused per-group width/start/size buffers for an owner/standalone drag's block-move preview.
    private readonly List<double> _reorderGroupWidths = new();
    private readonly List<int> _reorderGroupStarts = new();
    private readonly List<int> _reorderGroupSizes = new();

    public AppBandControl()
    {
        Height = RowHeight + 1;
        this.Dyn(BackgroundProperty, "ColorChrome");

        var border = new Border { BorderThickness = new Thickness(0, 0, 0, 1) };
        border.Dyn(Border.BorderBrushProperty, "ColorBorder");

        _dock = new DockPanel { LastChildFill = true };
        border.Child = _dock;
        Content = border;

        // ── Static app branding + hamburger menu button ──
        var brandRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 6, 0),
        };
        _hamburger = MakeGlyphButton("☰", "Menu");
        brandRow.Children.Add(_hamburger);

        var menuDivider = new Border { Width = 1, Margin = new Thickness(6, 4, 0, 4) };
        menuDivider.Dyn(Border.BackgroundProperty, "ColorBorder");
        brandRow.Children.Add(menuDivider);

        // The ship emblem — a full-colour raster that follows the theme (wired in Initialize,
        // once the theme service is available).
        _brandMark = new Image { Width = 24, Height = 24, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var word = new TextBlock { Text = "Historia", FontWeight = FontWeight.SemiBold, Margin = new Thickness(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        word.Dyn(TextBlock.ForegroundProperty, "ColorText");

        // The mark + wordmark are their own click target, separate from the hamburger —
        // clicking them opens the Home right panel directly, without also popping the app menu.
        var brandClick = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Background = Brushes.Transparent,
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
        };
        brandClick.Children.Add(_brandMark);
        brandClick.Children.Add(word);
        ToolTip.SetTip(brandClick, "Home");
        brandClick.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(brandClick).Properties.IsLeftButtonPressed)
                _nav?.RequestNavigate("");
        };
        brandRow.Children.Add(brandClick);

        _brand = new Border { Child = brandRow, Height = RowHeight, VerticalAlignment = VerticalAlignment.Top };

        _appMenu = new ContextMenu { Placement = PlacementMode.BottomEdgeAlignedLeft };
        _hamburger.Click += (_, _) =>
        {
            // Opening the menu from a repo view switches the detail pane to Home, but the
            // Settings / Developer / Profiles panels are left open so the menu can act on them.
            var head = (_nav?.CurrentRoute ?? "").Split('?', 2)[0].TrimStart('/');
            if (_nav is not null && head is not ("" or "settings" or "dev" or "profiles"))
                _nav.RequestNavigate("");
            _appMenu.Open(_hamburger);
        };

        DockPanel.SetDock(_brand, Dock.Left);
        _dock.Children.Add(_brand);

        var brandDivider = new Border { Width = 1 };
        brandDivider.Dyn(Border.BackgroundProperty, "ColorBorder");
        DockPanel.SetDock(brandDivider, Dock.Left);
        _dock.Children.Add(brandDivider);

        _tabPanel = new WrapPanel { Orientation = Orientation.Horizontal };
        // The drag gesture is captured to _tabPanel (not the dragged shell) so ReorderTo can
        // freely re-parent shells during a live shuffle without dropping the pointer.
        _tabPanel.AddHandler(PointerMovedEvent, OnStripPointerMoved, RoutingStrategies.Bubble);
        _tabPanel.AddHandler(PointerReleasedEvent, OnStripPointerReleased, RoutingStrategies.Bubble);
        _tabPanel.PointerCaptureLost += OnStripCaptureLost;
        _scroller = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _tabPanel,
        };
        _scroller.ScrollChanged += (_, _) => UpdateOverflow();
        _scroller.PropertyChanged += (_, e) =>
        {
            if (e.Property == BoundsProperty)
                UpdateOverflow();
        };
        _scroller.AddHandler(PointerWheelChangedEvent, (_, e) =>
        {
            // Wheel over the strip pans it: down the rows in multi-line mode, along the row
            // (left / right) in single-row mode. Tilt-wheel / trackpad X is a fallback.
            var d = e.Delta.Y != 0 ? e.Delta.Y : e.Delta.X;
            if (d == 0)
                return;
            // Honour the OS natural/inverted-scroll preference (Windows probes false).
            d *= Historia.Services.UI.WheelInput.Sign(
                _settings.Current.WheelScrollDirection, _scrollProbe.NaturalScrolling);

            if (MultiLine)
            {
                if (ScrollableHeight <= 0.5)
                    return;
                SetVerticalOffset(_scroller.Offset.Y - Math.Sign(d) * RowHeight);
            }
            else
            {
                if (ScrollableWidth <= 0.5)
                    return;
                SetHorizontalOffset(_scroller.Offset.X - Math.Sign(d) * TabWidth);
            }
            e.Handled = true;
        }, RoutingStrategies.Tunnel);

        _right = MakeArrow("▶");
        _right.Click += (_, _) => PageStrip(+1);
        DockPanel.SetDock(_right, Dock.Right);
        _dock.Children.Add(_right);

        _left = MakeArrow("◀");
        _left.Click += (_, _) => PageStrip(-1);
        DockPanel.SetDock(_left, Dock.Right);
        _dock.Children.Add(_left);

        _add = MakeGlyphButton("＋", "Open or clone a repository");
        // The + button opens a small Open / Clone / Initialize menu rather than just
        // landing on Home. Each item routes to Home with an intent the panel acts on.
        _addMenu = new ContextMenu { Placement = PlacementMode.BottomEdgeAlignedLeft };
        _addMenu.ItemsSource = new Control[]
        {
            MenuLeaf("Open…", () => _nav.RequestNavigate("?intent=open")),
            MenuLeaf("Clone…", () => _nav.RequestNavigate("?intent=clone")),
            MenuLeaf("Initialize…", () => _nav.RequestNavigate("?intent=init")),
        };
        _add.Click += (_, _) => _addMenu.Open(_add);

        _dock.Children.Add(_scroller);
    }

    private double ScrollableWidth => Math.Max(0, _scroller.Extent.Width - _scroller.Viewport.Width);
    private double ScrollableHeight => Math.Max(0, _scroller.Extent.Height - _scroller.Viewport.Height);

    private void SetHorizontalOffset(double x)
        => _scroller.Offset = new Vector(Math.Clamp(x, 0, ScrollableWidth), _scroller.Offset.Y);

    private void SetVerticalOffset(double y)
        => _scroller.Offset = new Vector(_scroller.Offset.X, Math.Clamp(y, 0, ScrollableHeight));

    private void PlaceAddButton(bool overflow)
    {
        if (overflow)
        {
            _add.VerticalAlignment = MultiLine ? VerticalAlignment.Bottom : VerticalAlignment.Center;
            if (ReferenceEquals(_add.Parent, _tabPanel))
                _tabPanel.Children.Remove(_add);
            if (!_dock.Children.Contains(_add))
            {
                DockPanel.SetDock(_add, Dock.Right);
                // Insert immediately before the scroller (always the last, fill child) so the
                // add button docks *after* the arrows in processing order — i.e. it lands next
                // to the tab strip, and the arrows are the ones pushed further out to the edge.
                _dock.Children.Insert(_dock.Children.Count - 1, _add);
            }
        }
        else
        {
            _add.VerticalAlignment = VerticalAlignment.Center;
            if (_dock.Children.Contains(_add))
                _dock.Children.Remove(_add);
            if (!ReferenceEquals(_add.Parent, _tabPanel))
                _tabPanel.Children.Add(_add);
            else if (_tabPanel.Children.Count > 0 && !ReferenceEquals(_tabPanel.Children[^1], _add))
            {
                _tabPanel.Children.Remove(_add);
                _tabPanel.Children.Add(_add);
            }
        }
    }

    public void Initialize(IServiceProvider services)
    {
        _services = services;
        _workspace = services.GetRequiredService<IWorkspaceService>();
        _autoFetch = services.GetRequiredService<IAutoFetchService>();
        _terminals = services.GetRequiredService<ITerminalSessionService>();
        _nav = services.GetRequiredService<INavigationBus>();
        _profiles = services.GetRequiredService<IProfileService>();
        _settings = services.GetRequiredService<ISettingsService>();
        _launcher = services.GetRequiredService<Historia.Services.UI.IExternalLauncher>();
        _scrollProbe = services.GetRequiredService<Historia.Services.UI.IScrollDirectionProbe>();
        _cloneService = services.GetRequiredService<ICloneService>();
        _unsavedWork = services.GetService<IUnsavedWorkRegistry>();
        _dialogs = services.GetService<IDialogService>();

        Historia.Views.ViewKit.BindThemedMark(_brandMark, services.GetRequiredService<Historia.Services.Theming.IThemeService>());

        _onOpenChanged = (_, _) => Dispatcher.UIThread.Post(RebuildTabs);
        _onFocusChanged = (_, _) => Dispatcher.UIThread.Post(RebuildTabs);
        _onProfilesChanged = (_, _) => Dispatcher.UIThread.Post(BuildAppMenu);
        _onSettingsChanged = (_, _) => Dispatcher.UIThread.Post(() =>
        {
            ApplyTabStripMode();
            RebuildTabs(); // Tab-width mode / fixed width changes need the tabs rebuilt
        });

        _onAutoFetchStates = (_, _) => Dispatcher.UIThread.Post(RecolorAutoFetchBars);
        _onAutoFetchDebug = () => Dispatcher.UIThread.Post(ApplyAutoFetchBarVisibility);
        _onCloneChanged = (_, _) => Dispatcher.UIThread.Post(OnCloneStatusChanged);

        _workspace.OpenRepositoriesChanged += _onOpenChanged;
        _workspace.FocusedRepositoryChanged += _onFocusChanged;
        _profiles.ProfilesChanged += _onProfilesChanged;
        _settings.Changed += _onSettingsChanged;
        _autoFetch.TabStatesChanged += _onAutoFetchStates;
        AutoFetchDebug.Changed += _onAutoFetchDebug;
        _cloneService.Changed += _onCloneChanged;

        BuildAppMenu();
        ApplyTabStripMode();
        _cloneTabWasPresent = _cloneService.Current is not null;
        RebuildTabs();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_workspace is not null)
        {
            _workspace.OpenRepositoriesChanged -= _onOpenChanged;
            _workspace.FocusedRepositoryChanged -= _onFocusChanged;
        }
        if (_profiles is not null)
        {
            _profiles.ProfilesChanged -= _onProfilesChanged;
        }
        if (_settings is not null)
            _settings.Changed -= _onSettingsChanged;
        if (_autoFetch is not null && _onAutoFetchStates is not null)
            _autoFetch.TabStatesChanged -= _onAutoFetchStates;
        if (_onAutoFetchDebug is not null)
            AutoFetchDebug.Changed -= _onAutoFetchDebug;
        if (_cloneService is not null && _onCloneChanged is not null)
            _cloneService.Changed -= _onCloneChanged;
    }

    /// <summary><see cref="ICloneService.Changed"/> fires on every progress tick — a full
    /// <see cref="RebuildTabs"/> per tick would rebuild every real tab dozens of times a second.
    /// Only rebuild the strip when a clone starts or ends (<c>Current</c> transitioning to or
    /// from <see langword="null"/>); otherwise update the pending tab's status text and its
    /// close button's enabled state in place.</summary>
    private void OnCloneStatusChanged()
    {
        var status = _cloneService.Current;
        var isRunningNow = status is not null;
        if (isRunningNow != _cloneTabWasPresent)
        {
            _cloneTabWasPresent = isRunningNow;
            RebuildTabs();
            return;
        }
        if (status is null)
            return;
        if (_pendingCloneStatusText is not null)
            _pendingCloneStatusText.Text = status.Describe();
        if (_pendingCloneCloseButton is not null)
            _pendingCloneCloseButton.IsEnabled = !status.CancelRequested;
    }

    // ── dev visuals: per-tab auto-fetch colour bar ──

    private static string AutoFetchBarToken(AutoFetchTabState state) => state switch
    {
        AutoFetchTabState.FetchedThisCycle => "ColorSuccess",
        AutoFetchTabState.PendingThisCycle => "ColorWarning",
        _ => "ColorDanger",
    };

    private void ApplyAutoFetchBarVisibility()
    {
        foreach (var (bar, _) in _autoFetchBars.Values)
            bar.IsVisible = AutoFetchDebug.Enabled;
        RecolorAutoFetchBars();
    }

    private void RecolorAutoFetchBars()
    {
        if (!AutoFetchDebug.Enabled)
            return;
        foreach (var ctx in _autoFetchBars.Keys.ToList())
        {
            var (bar, token) = _autoFetchBars[ctx];
            var want = AutoFetchBarToken(_autoFetch.GetTabState(ctx));
            if (want == token)
                continue;
            bar.Dyn(Border.BackgroundProperty, want);
            _autoFetchBars[ctx] = (bar, want);
        }
    }

    // ── Terminal entry points ──

    // The Historia menu's sole terminal item, labelled "Open Terminal" — always adds another Home
    // shell and focuses it (the label is cosmetic: it never reuses an existing shell).
    private Task NewHomeTerminalAsync()
        => _terminals.OpenNewAsync(TerminalOrigin.Home(HomeDirectory));

    private Task OpenRepoTerminalAsync(RepositoryContext ctx)
    {
        _workspace.Focus(ctx);
        return _terminals.OpenOrFocusAsync(RepoTerminalOrigin.For(ctx));
    }

    // ── App menu ──

    private void BuildAppMenu()
    {
        var items = new List<Control>
        {
            MenuLeaf("Open Repository…", () => _nav.RequestNavigate("")),
            MenuLeaf("Clone Repository…", () => _nav.RequestNavigate("")),
            MenuLeaf("Initialize Repository…", () => _nav.RequestNavigate("")),
            new Separator(),
        };

        var profileRoot = new MenuItem { Header = "Default profile" };
        var profileItems = new List<Control>();
        var defaultProfile = _profiles.GetDefaultProfile();
        foreach (var p in _profiles.GetAllProfiles())
        {
            var item = new MenuItem
            {
                Header = p.IsGlobal ? "Global (~/.gitconfig)" : p.DisplayName,
                Icon = defaultProfile is not null && defaultProfile.Id == p.Id ? new TextBlock { Text = "✓" } : null,
            };
            var profile = p;
            item.Click += async (_, _) => await DefaultProfileFlow.SetAsync(_services, profile);
            profileItems.Add(item);
        }
        if (profileItems.Count > 0)
            profileItems.Add(new Separator());
        profileItems.Add(MenuLeaf("Manage profiles…", () => _nav.RequestNavigate("profiles")));
        profileRoot.ItemsSource = profileItems;
        items.Add(profileRoot);

        items.Add(MenuLeaf("Open Terminal", () => _ = NewHomeTerminalAsync()));
        items.Add(MenuLeaf("Settings", () => _nav.RequestNavigate("settings")));
        if (DevMode.Enabled)
            items.Add(MenuLeaf("Developer", () => _nav.RequestNavigate("dev")));
        items.Add(new Separator());
        var theme = _services.GetRequiredService<Historia.Services.Theming.IThemeService>();
        items.Add(MenuLeaf("About Historia", () => _ = AboutDialog.ShowAsync(theme)));
        items.Add(MenuLeaf("Exit", () =>
            (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.TryShutdown()));

        _appMenu.ItemsSource = items;
    }

    // ── Tabs ──

    private void RebuildTabs()
    {
        if (_dragging)
            return;

        _tabPanel.Children.Clear();
        _tabViews.Clear();
        _autoFetchBars.Clear();
        _pendingCloneStatusText = null;
        _pendingCloneCloseButton = null;
        var open = _workspace.OpenRepositories;
        Control? focusedView = null;
        for (var i = 0; i < open.Count; i++)
        {
            var isFocused = ReferenceEquals(open[i], _workspace.Focused);
            var view = MakeTab(open[i], isFocused, first: i == 0);
            _tabViews.Add(view);
            _tabPanel.Children.Add(view);
            if (isFocused)
                focusedView = view;
        }

        // A pending tab for a clone in progress — not backed by a RepositoryContext, so it
        // gets its own builder. Added to _tabViews (not just _tabPanel) so UpdateOverflow's width
        // sum, the overflow arrows and the "+" button placement all still account for it; its
        // untagged Border is excluded from drag-reorder automatically (OnTabPressed / ReorderTo
        // key off Tag is RepositoryContext).
        //
        // workspace.CloneAsync adds the real repo (raising OpenRepositoriesChanged -> this same
        // RebuildTabs) before CloneService's own finally block nulls Current — so for one
        // in-between rebuild both the pending status and the real, already-open repo exist at
        // once. Suppress the pending tab once its target directory is already open, so that
        // window never shows a flickering duplicate.
        if (_cloneService.Current is { } cloneStatus
            && !open.Any(ctx => PathsEqual(ctx.RepositoryPath, cloneStatus.TargetDirectory)))
        {
            var pending = MakePendingCloneTab(cloneStatus, first: _tabViews.Count == 0);
            _tabViews.Add(pending);
            _tabPanel.Children.Add(pending);
        }
        UpdateOverflow();

        // Tabs are variable-width, so the estimate above ran before any tab was measured.
        // Re-check once the strip has laid out (this also keeps the focused tab on screen —
        // e.g. a repo opened by a forwarded second launch lands at the far right).
        Dispatcher.UIThread.Post(() =>
        {
            UpdateOverflow();
            focusedView?.BringIntoView();
        }, DispatcherPriority.Loaded);
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(a.TrimEnd('/', '\\'), b.TrimEnd('/', '\\'),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private Border MakeTab(RepositoryContext ctx, bool active, bool first)
    {
        // a submodule-linked tab renders 3px shorter, flush with the strip's bottom edge,
        // and suppresses its own leading separator so the group reads as one continuous unit
        // with only the owner's separator showing. Avalonia's Layoutable.Arrange applies
        // Height/VerticalAlignment generically for any element within whatever rect its parent
        // panel hands it — this holds for WrapPanel exactly as for any other panel, so a
        // shorter linked shell still sits bottom-aligned within its row.
        var linked = ctx.LinkOwner is not null;
        var shell = new Border
        {
            Tag = ctx,
            BorderThickness = new Thickness((first || linked) ? 0 : 1, 0, 0, 0),
            Height = linked ? RowHeight - 3 : RowHeight,
            VerticalAlignment = linked ? VerticalAlignment.Bottom : VerticalAlignment.Stretch,
        };
        shell.Dyn(Border.BorderBrushProperty, "ColorBorder");

        // Dynamic (default) → no Width, the tab sizes to its label. Fixed → every tab is
        // the configured pixel width. 5 px of breathing room after the last
        // icon; the cluster's own left inset is the StackPanel margin below.
        var fixedMode = TabMode == TabWidthMode.Fixed;
        var body = new Border
        {
            Padding = new Thickness(12, 0, 5, 0),
            BorderThickness = new Thickness(0, 2, 0, 0),
            BorderBrush = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        if (fixedMode)
            body.Width = FixedTabWidth;
        if (active)
        {
            body.Dyn(Border.BackgroundProperty, "ColorBg");
            body.Dyn(Border.BorderBrushProperty, "ColorPrimary");
        }
        else
        {
            body.Dyn(Border.BackgroundProperty, "ColorChrome");
        }

        var grid = new Grid
        {
            // Fixed: name takes the leftover (star) space and ellipsises at the tab edge.
            // Dynamic: name is Auto and capped by its own MaxWidth, so the tab shrinks to fit.
            ColumnDefinitions = new ColumnDefinitions(fixedMode ? "*,Auto" : "Auto,Auto"),
        };

        var name = new TextBlock
        {
            Text = ctx.DisplayName,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (!fixedMode)
            // Grow with the label, but only up to ~25 characters; past that it ellipsises.
            name.MaxWidth = TabNameMaxChars * ApproxCharPx;
        ToolTip.SetTip(name, ctx.RepositoryPath);
        ToolTip.SetShowDelay(name, 75);
        name.Dyn(TextBlock.ForegroundProperty, active ? "ColorText" : "ColorTextMuted");
        Grid.SetColumn(name, 0);
        grid.Children.Add(name);

        // terminal glyph and close — Path glyphs on identical 18×18 buttons so they match
        // in size and sit on one centre line (a font "✕" glyph renders small, half-height and
        // off-centre). The close button shows on every tab, not just the
        // active / hovered one.
        Button IconButton(string icon, string tip, double glyphSize = 13)
        {
            var glyph = Historia.Views.ViewKit.IconGlyphPath(icon, glyphSize, "ColorTextMuted");
            glyph.HorizontalAlignment = HorizontalAlignment.Center;
            glyph.VerticalAlignment = VerticalAlignment.Center;
            var b = new Button
            {
                Width = 18,
                Height = 18,
                Padding = new Thickness(0),
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Cursor = new Cursor(StandardCursorType.Hand),
                Content = glyph,
            };
            ToolTip.SetTip(b, tip);
            return b;
        }

        static Button TerminalButton()
        {
            var glyph = Historia.Views.ViewKit.IconGlyphPath("terminal", 15, "ColorTextMuted");
            glyph.HorizontalAlignment = HorizontalAlignment.Center;
            glyph.VerticalAlignment = VerticalAlignment.Center;
            var b = new Button
            {
                Width = 24,
                Height = 18,
                Padding = new Thickness(0),
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Cursor = new Cursor(StandardCursorType.Hand),
                Content = glyph,
            };
            ToolTip.SetTip(b, "Open terminal here");
            return b;
        }

        // The glyph buttons sit in one row, flush against each other, 5 px inset from the
        // label (the matching 5 px after the last icon is the body's right padding).
        var icons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 0,
            Margin = new Thickness(5, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(icons, 1);
        grid.Children.Add(icons);

        var termBtn = TerminalButton();
        termBtn.Click += (_, e) => { e.Handled = true; _ = OpenRepoTerminalAsync(ctx); };
        icons.Children.Add(termBtn);

        // The "close" glyph reads heavier than terminal at the same box, so shrink it to 10.
        var close = IconButton("close", "Close repository", glyphSize: 10);
        close.Click += async (_, e) => { e.Handled = true; await CloseAsync(ctx); };
        icons.Children.Add(close);

        body.Child = grid;

        // Tab context menu — reveal the repo folder, copy its path, close the tab.
        if (!ctx.IsBare || ctx.RepositoryPath is { Length: > 0 })
        {
            var tabMenu = new ContextMenu();
            tabMenu.ItemsSource = new Control[]
            {
                MenuLeaf("Open repository location", () => _launcher.OpenFolder(ctx.RepositoryPath)),
                MenuLeaf("Copy path", () => TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(ctx.RepositoryPath)),
                new Separator(),
                MenuLeaf("Close", () => _ = CloseAsync(ctx)),
            };
            body.ContextMenu = tabMenu;
        }

        body.PointerReleased += (_, e) =>
        {
            if (e.InitialPressMouseButton == MouseButton.Left && !_dragging)
            {
                _workspace.Focus(ctx);
                _nav.RequestNavigate("repo");
            }
            else if (e.InitialPressMouseButton == MouseButton.Middle)
            {
                e.Handled = true;
                _ = CloseAsync(ctx);
            }
        };

        // dev visuals: a 4 px colour bar down the tab's left edge (styled like the graph
        // column's right-edge lane tick), showing this repo's auto-fetch state. Hidden
        // unless AutoFetchDebug.Enabled; recoloured in place on IAutoFetchService.TabStatesChanged.
        var afBarToken = AutoFetchBarToken(_autoFetch.GetTabState(ctx));
        var afBar = new Border { Width = 4, IsVisible = AutoFetchDebug.Enabled };
        afBar.Dyn(Border.BackgroundProperty, afBarToken);
        _autoFetchBars[ctx] = (afBar, afBarToken);

        var layout = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        Grid.SetColumn(afBar, 0);
        Grid.SetColumn(body, 1);
        layout.Children.Add(afBar);
        layout.Children.Add(body);

        shell.Child = layout;
        shell.AddHandler(PointerPressedEvent, OnTabPressed, RoutingStrategies.Tunnel);
        return shell;
    }

    /// <summary>The tab shown for the duration of a clone — not backed by a
    /// <see cref="RepositoryContext"/>, so it is not focusable, carries no terminal glyph or
    /// context menu, and its <c>Tag</c> is left unset so the drag-reorder path (which keys off
    /// <c>Tag is RepositoryContext</c>) ignores it entirely. Its close button cancels the clone.</summary>
    private Border MakePendingCloneTab(CloneStatus status, bool first)
    {
        var shell = new Border
        {
            BorderThickness = new Thickness(first ? 0 : 1, 0, 0, 0),
            Height = RowHeight,
        };
        shell.Dyn(Border.BorderBrushProperty, "ColorBorder");

        var body = new Border
        {
            Padding = new Thickness(12, 0, 5, 0),
            BorderThickness = new Thickness(0, 2, 0, 0),
            BorderBrush = Brushes.Transparent,
        };
        body.Dyn(Border.BackgroundProperty, "ColorChrome");

        var leaf = System.IO.Path.GetFileName(status.TargetDirectory.TrimEnd('/', '\\'));
        if (string.IsNullOrEmpty(leaf))
            leaf = status.TargetDirectory;

        var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 1 };
        var name = new TextBlock { Text = leaf, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = TabNameMaxChars * ApproxCharPx };
        ToolTip.SetTip(name, status.TargetDirectory);
        ToolTip.SetShowDelay(name, 75);
        name.Dyn(TextBlock.ForegroundProperty, "ColorTextMuted");
        var statusText = new TextBlock { Text = status.Describe(), FontSize = 10, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = TabNameMaxChars * ApproxCharPx };
        statusText.Dyn(TextBlock.ForegroundProperty, "ColorTextMuted");
        labels.Children.Add(name);
        labels.Children.Add(statusText);
        _pendingCloneStatusText = statusText;

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto") };
        Grid.SetColumn(labels, 0);
        grid.Children.Add(labels);

        var closeGlyph = Historia.Views.ViewKit.IconGlyphPath("close", 10, "ColorTextMuted");
        closeGlyph.HorizontalAlignment = HorizontalAlignment.Center;
        closeGlyph.VerticalAlignment = VerticalAlignment.Center;
        var close = new Button
        {
            Width = 18,
            Height = 18,
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand),
            Content = closeGlyph,
            IsEnabled = !status.CancelRequested,
            Margin = new Thickness(5, 0, 0, 0),
        };
        ToolTip.SetTip(close, "Cancel clone");
        close.Click += (_, e) => { e.Handled = true; _cloneService.Cancel(); };
        Grid.SetColumn(close, 1);
        grid.Children.Add(close);
        _pendingCloneCloseButton = close;

        body.Child = grid;
        shell.Child = body;
        return shell;
    }

    // ── Tab drag-and-drop ──
    // The gesture is captured to _tabPanel, so ReorderTo can re-parent shells live without
    // dropping the pointer. The move / release / capture-lost handlers live on _tabPanel.

    private void OnTabPressed(object? sender, PointerPressedEventArgs e)
    {
        if ((e.Source as Visual)?.FindAncestorOfType<Button>(includeSelf: true) is not null)
            return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        _dragView = (Border)sender!;
        _pressPanelPos = e.GetPosition(_tabPanel);
        _grabOffset = e.GetPosition(_dragView);
        _dragFromIndex = _tabViews.IndexOf(_dragView);

        // record the pressed tab's group span up front — a linked-child drag clamps its
        // live target into this span (its owner's group); an owner/standalone drag moves this
        // span as one block.
        (_dragGroupStart, _dragGroupCount) = ComputeGroupSpan(_dragView.Tag as RepositoryContext);
    }

    /// <summary>the contiguous <c>_tabViews</c> index range occupied by <paramref name="member"/>'s
    /// group (the owner itself, or <paramref name="member"/> when it has no
    /// <see cref="RepositoryContext.LinkOwner"/>, plus every tab whose <c>LinkOwner</c> is that
    /// owner) — the App-layer mirror of <c>WorkspaceService.GroupSpan</c>, computed from
    /// <c>_tabViews</c> since <c>_workspace.OpenRepositories</c> and <c>_tabViews</c> share the
    /// same order (<see cref="RebuildTabs"/>).</summary>
    private (int Start, int Count) ComputeGroupSpan(RepositoryContext? member)
    {
        if (member is null)
            return (-1, 0);
        var owner = member.LinkOwner ?? member;
        var start = _tabViews.FindIndex(v => ReferenceEquals(v.Tag, owner));
        if (start < 0)
            return (-1, 0);
        var count = 1;
        while (start + count < _tabViews.Count
               && _tabViews[start + count].Tag is RepositoryContext sibling
               && ReferenceEquals(sibling.LinkOwner, owner))
            count++;
        return (start, count);
    }

    private void OnStripPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragView is null || !e.GetCurrentPoint(_tabPanel).Properties.IsLeftButtonPressed)
            return;

        var panelPos = e.GetPosition(_tabPanel);
        _lastPanelPos = panelPos;
        if (!_dragging)
        {
            if (!AppBandDragReorder.PassesThreshold(_pressPanelPos.X, panelPos.X, DragThreshold))
                return;
            _dragging = true;
            e.Pointer.Capture(_tabPanel);
            StartDragGhost(_dragView);
            _dragView.Opacity = 0.3;
            StartEdgeTimer();
        }

        ReorderTo(panelPos.X);
        _dragGhost?.MoveTo(new Point(
            e.GetPosition(this).X - _grabOffset.X,
            e.GetPosition(this).Y - _grabOffset.Y));

        _edgeDir = AppBandDragReorder.EdgeDirection(
            e.GetPosition(_scroller).X, _scroller.Viewport.Width, EdgeScrollZone);
    }

    // Capture lost without a normal drop (a dialog, Alt+Tab, …): abandon the drag and resync.
    private void OnStripCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (!_dragging && _dragView is null)
            return;
        if (_dragView is not null)
            _dragView.Opacity = 1;
        _dragView = null;
        _dragging = false;
        StopEdgeTimer();
        StopDragGhost();
        RebuildTabs();
    }

    private void OnStripPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragView is null)
            return;

        var view = _dragView;
        var wasDragging = _dragging;
        _dragView = null;
        _dragging = false;
        StopEdgeTimer();
        StopDragGhost();
        view.Opacity = 1;
        e.Pointer.Capture(null);

        if (!wasDragging)
            return;
        e.Handled = true;

        var to = _tabViews.IndexOf(view);
        if (view.Tag is RepositoryContext ctx && to >= 0 && to != _dragFromIndex)
            _workspace.Move(ctx, to);   // → OpenRepositoriesChanged → RebuildTabs
        else
            RebuildTabs();              // returned to its own slot — restore separators / opacity
    }

    /// <summary>Live shuffle: moves the dragged shell (or, for a group, the dragged shell's
    /// whole contiguous group) so its slot matches the cursor's X (panel space). This is
    /// only the live preview; <c>WorkspaceService.Move</c> (called from
    /// <see cref="OnStripPointerReleased"/>) is the authority on the final order, and a
    /// subsequent <see cref="RebuildTabs"/> repaints it.</summary>
    private void ReorderTo(double panelX)
    {
        var view = _dragView;
        if (view is null)
            return;
        var current = _tabViews.IndexOf(view);
        if (current < 0)
            return;

        var isLinkedChild = view.Tag is RepositoryContext ctx && ctx.LinkOwner is not null;

        if (isLinkedChild)
        {
            _reorderWidths.Clear();
            foreach (var t in _tabViews)
                _reorderWidths.Add(t.Bounds.Width);
            var raw = AppBandDragReorder.TargetIndex(panelX, _reorderWidths, TabWidth);
            var target = AppBandDragReorder.ClampChildTarget(raw, _dragGroupStart, _dragGroupCount);
            if (target == current || target == _dragGroupStart)
                return; // already there, or the group has no legal sibling slot

            _tabViews.RemoveAt(current);
            _tabPanel.Children.Remove(view);
            var insertAt = Math.Clamp(target, 0, _tabViews.Count);
            _tabViews.Insert(insertAt, view);
            _tabPanel.Children.Insert(Math.Min(insertAt, _tabPanel.Children.Count), view);
        }
        else
        {
            // Owner/standalone: the whole contiguous group moves as one block. Sum each group's
            // member widths into one per-group width, run the same TargetIndex math against
            // that per-group list, then map the returned group slot back onto a _tabViews index.
            var (groupStart, groupCount) = ComputeGroupSpan(view.Tag as RepositoryContext);
            if (groupStart < 0)
                return;

            _reorderGroupWidths.Clear();
            _reorderGroupStarts.Clear();
            _reorderGroupSizes.Clear();
            var i = 0;
            while (i < _tabViews.Count && _tabViews[i].Tag is RepositoryContext)
            {
                var start = i;
                var owner = _tabViews[i].Tag as RepositoryContext;
                var w = _tabViews[i].Bounds.Width;
                w = w <= 0 ? TabWidth : w;
                i++;
                while (i < _tabViews.Count
                       && _tabViews[i].Tag is RepositoryContext sibling
                       && ReferenceEquals(sibling.LinkOwner, owner))
                {
                    var sw = _tabViews[i].Bounds.Width;
                    w += sw <= 0 ? TabWidth : sw;
                    i++;
                }
                _reorderGroupWidths.Add(w);
                _reorderGroupStarts.Add(start);
                _reorderGroupSizes.Add(i - start);
            }

            var currentGroupIndex = _reorderGroupStarts.IndexOf(groupStart);
            if (currentGroupIndex < 0)
                return;

            var targetGroupIndex = AppBandDragReorder.TargetIndex(panelX, _reorderGroupWidths, TabWidth);
            if (targetGroupIndex == currentGroupIndex)
                return;

            var insertAt = AppBandDragReorder.BlockInsertIndex(_reorderGroupSizes, currentGroupIndex, targetGroupIndex);

            var block = _tabViews.GetRange(groupStart, groupCount);
            _tabViews.RemoveRange(groupStart, groupCount);
            foreach (var b in block)
                _tabPanel.Children.Remove(b);
            insertAt = Math.Clamp(insertAt, 0, _tabViews.Count);
            _tabViews.InsertRange(insertAt, block);
            var childInsertBase = Math.Min(insertAt, _tabPanel.Children.Count);
            for (var k = 0; k < block.Count; k++)
                _tabPanel.Children.Insert(childInsertBase + k, block[k]);
        }

        for (var idx = 0; idx < _tabViews.Count; idx++)
            _tabViews[idx].BorderThickness = new Thickness(idx == 0 ? 0 : 1, 0, 0, 0);
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
        if (_edgeDir == 0 || _dragView is null)
            return;
        var next = Math.Clamp(_scroller.Offset.X + EdgeScrollStep * _edgeDir, 0, ScrollableWidth);
        if (Math.Abs(next - _scroller.Offset.X) < 0.5)
            return;
        SetHorizontalOffset(next);
        ReorderTo(_lastPanelPos.X);
    }

    private void StartDragGhost(Border shell)
    {
        StopDragGhost();
        var layer = AdornerLayer.GetAdornerLayer(this);
        if (layer is null || shell.Bounds.Width <= 0 || shell.Bounds.Height <= 0)
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

    private async Task CloseAsync(RepositoryContext ctx)
    {
        if (_unsavedWork is not null && _dialogs is not null)
        {
            var work = _unsavedWork.For(ctx);
            if (work.Count > 0)
            {
                var proceed = await UnsavedWorkPrompt.ResolveAsync(work, _dialogs).ConfigureAwait(true);
                if (!proceed)
                    return;
            }
        }

        await _workspace.CloseAsync(ctx);
        if (_workspace.Focused is null)
            _nav.RequestNavigate("");
    }

    // ── multi-line tab strip ──

    private bool MultiLine => _settings?.Current.EnableMultiLineTabs ?? false;
    private int MaxRows => Math.Clamp(_settings?.Current.MaxTabRows ?? 3, 2, 5);

    // ── Tab width ──

    private TabWidthMode TabMode => _settings?.Current.TabWidthMode ?? TabWidthMode.Dynamic;
    private double FixedTabWidth =>
        AppSettings.ClampFixedTabWidth(_settings?.Current.FixedTabWidth ?? AppSettings.DefaultFixedTabWidth);

    private void ApplyTabStripMode()
    {
        var multi = MultiLine;
        _scroller.HorizontalScrollBarVisibility = multi ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Hidden;
        _scroller.VerticalScrollBarVisibility = multi ? ScrollBarVisibility.Hidden : ScrollBarVisibility.Disabled;
        _left.VerticalAlignment = _right.VerticalAlignment =
            multi ? VerticalAlignment.Bottom : VerticalAlignment.Center;
        _scroller.Offset = default;
        UpdateOverflow();
    }

    private void PageStrip(int dir)
    {
        if (MultiLine)
            SetVerticalOffset(_scroller.Offset.Y + dir * RowHeight);
        else
            SetHorizontalOffset(_scroller.Offset.X + dir * TabWidth);
    }

    private void SetBandRows(int rows)
    {
        var h = Math.Max(1, rows) * RowHeight + 1;
        if (Math.Abs(Height - h) > 0.5)
            Height = h;
    }

    private bool _inUpdateOverflow;

    private void UpdateOverflow()
    {
        if (_inUpdateOverflow)
            return;
        _inUpdateOverflow = true;
        try
        {
            var viewport = _scroller.Viewport.Width;
            if (viewport <= 0)
            {
                PlaceAddButton(overflow: false);
                SetBandRows(1);
                return;
            }

            var tabCount = _tabViews.Count;
            // Tabs may be variable-width (Dynamic mode) — sum their laid-out widths (each
            // shell's Bounds already includes its 1 px left separator). Fall back to the
            // best per-mode estimate for any tab that hasn't been measured yet.
            var fallback = TabMode == TabWidthMode.Fixed ? FixedTabWidth : TabWidth;
            double tabsWidth = 0;
            foreach (var t in _tabViews)
                tabsWidth += t.Bounds.Width > 0.5 ? t.Bounds.Width : fallback;
            var addWidth = _add.Bounds.Width > 0 ? _add.Bounds.Width + _add.Margin.Left + _add.Margin.Right : 28;

            bool overflow;
            if (!MultiLine)
            {
                overflow = tabsWidth > viewport + 0.5;
                SetBandRows(1);
            }
            else
            {
                var rowsNeeded = Math.Max(1, (int)Math.Ceiling((tabsWidth + addWidth) / viewport - 1e-6));
                overflow = rowsNeeded > MaxRows;
                SetBandRows(Math.Min(rowsNeeded, MaxRows));
            }

            SetArrowVisible(_left, overflow);
            SetArrowVisible(_right, overflow);

            if (MultiLine)
            {
                _left.IsEnabled = _scroller.Offset.Y > 0.5;
                _right.IsEnabled = _scroller.Offset.Y < ScrollableHeight - 0.5;
            }
            else
            {
                _left.IsEnabled = _scroller.Offset.X > 0.5;
                _right.IsEnabled = _scroller.Offset.X < ScrollableWidth - 0.5;
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

    // ── Small widget factories ──

    private static Button MakeGlyphButton(string glyph, string tip)
    {
        var b = new Button
        {
            Content = glyph,
            Width = 24,
            Height = 24,
            Margin = new Thickness(2, 0, 2, 0),
            Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        b.Classes.Add("flat");
        ToolTip.SetTip(b, tip);
        return b;
    }

    private static RepeatButton MakeArrow(string glyph)
    {
        var b = new RepeatButton
        {
            Content = glyph,
            Width = 18,
            Height = 24,
            FontSize = 8,
            Padding = new Thickness(0),
            Margin = new Thickness(1, 0, 1, 0),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Opacity = 0,
            IsHitTestVisible = false,
        };
        b.Classes.Add("flat");
        return b;
    }
}
