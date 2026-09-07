using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Historia.Components;
using Historia.Platform;
using Historia.Services.AutoFetch;
using Historia.Services.Profiles;
using Historia.Services.Settings;
using Historia.Services.Terminal;
using Path = Avalonia.Controls.Shapes.Path;

namespace Historia.Controls;

/// <summary>
/// The window's app band (Row 0, §V2-9.3): the branded app-menu button plus the repo tab
/// strip. Native control, not a WebView — the app menu drops over the sidebar and the graph.
/// Avalonia port of the WPF <c>NativeControls.AppBandControl</c>. Bound to
/// <see cref="IWorkspaceService"/>; no new service.
/// </summary>
public sealed class AppBandControl : UserControl, IDisposable
{
    private const double RowHeight = 31;
    // V5.5: tab width is a user setting (AppSettings.TabWidthMode). Dynamic (default) sizes each
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
    private ISettingsService _settings = default!;
    private Historia.Services.UI.IExternalLauncher _launcher = default!;
    private Historia.Services.UI.IScrollDirectionProbe _scrollProbe = default!;

    private EventHandler<OpenRepositoriesChangedEventArgs>? _onOpenChanged;
    private EventHandler<FocusedRepositoryChangedEventArgs>? _onFocusChanged;
    private EventHandler? _onProfilesChanged;
    private EventHandler<ActiveProfileChangedEventArgs>? _onActiveProfileChanged;
    private EventHandler<SettingsChangedEventArgs>? _onSettingsChanged;
    private EventHandler? _onAutoFetchStates;
    private Action? _onAutoFetchDebug;
    private bool _disposed;

    // §21 dev visuals: per-open-repo left-edge colour bar + its last-applied token (skip re-binds).
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

    public AppBandControl()
    {
        Height = RowHeight + 1;
        Dyn(this, BackgroundProperty, "ColorChrome");

        var border = new Border { BorderThickness = new Thickness(0, 0, 0, 1) };
        Dyn(border, Border.BorderBrushProperty, "ColorBorder");

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
        Dyn(menuDivider, Border.BackgroundProperty, "ColorBorder");
        brandRow.Children.Add(menuDivider);

        // The ship emblem — a full-colour raster that follows the theme (wired in Initialize,
        // once the theme service is available). Was a monochrome silhouette Path off Historia.svg.
        _brandMark = new Image { Width = 24, Height = 24, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var word = new TextBlock { Text = "Historia", FontWeight = FontWeight.SemiBold, Margin = new Thickness(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        Dyn(word, TextBlock.ForegroundProperty, "ColorText");

        // v8.1: the mark + wordmark are their own click target, separate from the hamburger —
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
        Dyn(brandDivider, Border.BackgroundProperty, "ColorBorder");
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
            // v8 M9: honour the OS natural/inverted-scroll preference (Windows probes false).
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
        // v8 M8: the + button opens a small Open / Clone / Initialize menu rather than just
        // landing on Home. Each item routes to Home with an intent the panel acts on.
        _addMenu = new ContextMenu { Placement = PlacementMode.BottomEdgeAlignedLeft };
        _addMenu.ItemsSource = new Control[]
        {
            MenuItem("Open…", () => _nav.RequestNavigate("?intent=open")),
            MenuItem("Clone…", () => _nav.RequestNavigate("?intent=clone")),
            MenuItem("Initialize…", () => _nav.RequestNavigate("?intent=init")),
        };
        _add.Click += (_, _) => _addMenu.Open(_add);

        _dock.Children.Add(_scroller);
    }

    private static void Dyn(Control c, AvaloniaProperty p, string key)
        => c.Bind(p, new DynamicResourceExtension(key));

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
        _workspace = services.GetRequiredService<IWorkspaceService>();
        _autoFetch = services.GetRequiredService<IAutoFetchService>();
        _terminals = services.GetRequiredService<ITerminalSessionService>();
        _nav = services.GetRequiredService<INavigationBus>();
        _profiles = services.GetRequiredService<IProfileService>();
        _settings = services.GetRequiredService<ISettingsService>();
        _launcher = services.GetRequiredService<Historia.Services.UI.IExternalLauncher>();
        _scrollProbe = services.GetRequiredService<Historia.Services.UI.IScrollDirectionProbe>();

        Historia.Views.ViewKit.BindThemedMark(_brandMark, services.GetRequiredService<Historia.Services.Theming.IThemeService>());

        _onOpenChanged = (_, _) => Dispatcher.UIThread.Post(RebuildTabs);
        _onFocusChanged = (_, _) => Dispatcher.UIThread.Post(RebuildTabs);
        _onProfilesChanged = (_, _) => Dispatcher.UIThread.Post(BuildAppMenu);
        _onActiveProfileChanged = (_, _) => Dispatcher.UIThread.Post(BuildAppMenu);
        _onSettingsChanged = (_, _) => Dispatcher.UIThread.Post(() =>
        {
            ApplyTabStripMode();
            RebuildTabs(); // V5.5: tab-width mode / fixed width changes need the tabs rebuilt
        });

        _onAutoFetchStates = (_, _) => Dispatcher.UIThread.Post(RecolorAutoFetchBars);
        _onAutoFetchDebug = () => Dispatcher.UIThread.Post(ApplyAutoFetchBarVisibility);

        _workspace.OpenRepositoriesChanged += _onOpenChanged;
        _workspace.FocusedRepositoryChanged += _onFocusChanged;
        _profiles.ProfilesChanged += _onProfilesChanged;
        _profiles.ActiveProfileChanged += _onActiveProfileChanged;
        _settings.Changed += _onSettingsChanged;
        _autoFetch.TabStatesChanged += _onAutoFetchStates;
        AutoFetchDebug.Changed += _onAutoFetchDebug;

        BuildAppMenu();
        ApplyTabStripMode();
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
            _profiles.ActiveProfileChanged -= _onActiveProfileChanged;
        }
        if (_settings is not null)
            _settings.Changed -= _onSettingsChanged;
        if (_autoFetch is not null && _onAutoFetchStates is not null)
            _autoFetch.TabStatesChanged -= _onAutoFetchStates;
        if (_onAutoFetchDebug is not null)
            AutoFetchDebug.Changed -= _onAutoFetchDebug;
    }

    // ── §21 dev visuals: per-tab auto-fetch colour bar ──

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
            Dyn(bar, Border.BackgroundProperty, want);
            _autoFetchBars[ctx] = (bar, want);
        }
    }

    // ── Terminal entry points (V5 §1) ──

    private static string HomeDir()
        => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    // The Historia menu's sole terminal item, labelled "Open Terminal" — always adds another Home
    // shell and focuses it (the label is cosmetic; behaviour is unchanged "new" semantics).
    private Task NewHomeTerminalAsync()
        => _terminals.OpenNewAsync(TerminalOrigin.Home(HomeDir()));

    private Task OpenRepoTerminalAsync(RepositoryContext ctx)
    {
        _workspace.Focus(ctx);
        return _terminals.OpenOrFocusAsync(RepoOrigin(ctx));
    }

    internal static TerminalOrigin RepoOrigin(RepositoryContext ctx)
    {
        var key = ctx.IdentityKey ?? RepoIdentityKey.FromExplicitId(
            Guid.Parse(System.Security.Cryptography.MD5.HashData(
                System.Text.Encoding.UTF8.GetBytes(ctx.GitDirectory.ToLowerInvariant()))
                .Aggregate(new System.Text.StringBuilder(32), (sb, b) => sb.Append(b.ToString("x2"))).ToString()));
        var startDir = Historia.Services.Terminal.TerminalStartDirectory.ForRepository(
            ctx.IsBare ? null : ctx.RepositoryPath, ctx.GitDirectory);
        return TerminalOrigin.ForRepository(key, startDir, ctx.DisplayName);
    }

    // ── App menu ──

    private void BuildAppMenu()
    {
        var items = new List<Control>
        {
            MenuItem("Open Repository…", () => _nav.RequestNavigate("")),
            MenuItem("Clone Repository…", () => _nav.RequestNavigate("")),
            MenuItem("Initialize Repository…", () => _nav.RequestNavigate("")),
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
            var id = p.Id;
            item.Click += async (_, _) => await _profiles.SetDefaultProfileAsync(id);
            profileItems.Add(item);
        }
        if (profileItems.Count > 0)
            profileItems.Add(new Separator());
        profileItems.Add(MenuItem("Manage profiles…", () => _nav.RequestNavigate("profiles")));
        profileRoot.ItemsSource = profileItems;
        items.Add(profileRoot);

        items.Add(MenuItem("Open Terminal", () => _ = NewHomeTerminalAsync()));
        items.Add(MenuItem("Settings", () => _nav.RequestNavigate("settings")));
        if (DevMode.Enabled)
            items.Add(MenuItem("Developer", () => _nav.RequestNavigate("dev")));
        items.Add(new Separator());
        items.Add(MenuItem("About Historia", () => _nav.RequestNavigate("settings")));
        items.Add(MenuItem("Exit", () =>
            (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown()));

        _appMenu.ItemsSource = items;
    }

    private static MenuItem MenuItem(string header, Action onClick)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => onClick();
        return item;
    }

    // ── Tabs ──

    private void RebuildTabs()
    {
        if (_dragging)
            return;

        _tabPanel.Children.Clear();
        _tabViews.Clear();
        _autoFetchBars.Clear();
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
        UpdateOverflow();

        // Tabs are variable-width, so the estimate above ran before any tab was measured.
        // Re-check once the strip has laid out (this also keeps the focused tab on screen —
        // e.g. a repo opened by a forwarded second launch (§3) lands at the far right).
        Dispatcher.UIThread.Post(() =>
        {
            UpdateOverflow();
            focusedView?.BringIntoView();
        }, DispatcherPriority.Loaded);
    }

    private Border MakeTab(RepositoryContext ctx, bool active, bool first)
    {
        var shell = new Border
        {
            Tag = ctx,
            BorderThickness = new Thickness(first ? 0 : 1, 0, 0, 0),
            Height = RowHeight,
        };
        Dyn(shell, Border.BorderBrushProperty, "ColorBorder");

        // V5.5: Dynamic (default) → no Width, the tab sizes to its label. Fixed → every tab is
        // the configured pixel width (pre-V5.5 behaviour). 5 px of breathing room after the last
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
            Dyn(body, Border.BackgroundProperty, "ColorBg");
            Dyn(body, Border.BorderBrushProperty, "ColorPrimary");
        }
        else
        {
            Dyn(body, Border.BackgroundProperty, "ColorChrome");
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
        Dyn(name, TextBlock.ForegroundProperty, active ? "ColorText" : "ColorTextMuted");
        Grid.SetColumn(name, 0);
        grid.Children.Add(name);

        // V5 §1: terminal glyph and close — Path glyphs on identical 18×18 buttons so they match
        // in size and sit on one centre line (the old "✕" was a small font glyph that rendered
        // half-height and off-centre). The close button shows on every tab, not just the
        // active / hovered one. v8.4: the repo-settings cog moved off the tab onto the graph's
        // BRANCH / TAGS header.
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

        // v8.4: the terminal entry point is a wide "> _" prompt drawn in a rectangle (stretched
        // to fill, so it reads as a terminal window, not the old square "square-terminal" box)
        // and vertically centred in the tab row.
        static Button TerminalButton()
        {
            var glyph = new Path
            {
                Data = Geometry.Parse(Historia.Components.IconPaths.Get("terminal")),
                Stretch = Stretch.Fill,
                Width = 16,
                Height = 11,
                StrokeThickness = 1.5,
                StrokeLineCap = PenLineCap.Round,
                StrokeJoin = PenLineJoin.Round,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Dyn(glyph, Path.StrokeProperty, "ColorTextMuted");
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

        // v8 M6: tab context menu — reveal the repo folder, copy its path, close the tab.
        if (!ctx.IsBare || ctx.RepositoryPath is { Length: > 0 })
        {
            var tabMenu = new ContextMenu();
            tabMenu.ItemsSource = new Control[]
            {
                MenuItem("Open repository location", () => _launcher.OpenFolder(ctx.RepositoryPath)),
                MenuItem("Copy path", () => TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(ctx.RepositoryPath)),
                new Separator(),
                MenuItem("Close", () => _ = CloseAsync(ctx)),
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

        // §21 dev visuals: a 4 px colour bar down the tab's left edge (styled like the graph
        // column's right-edge lane tick), showing this repo's auto-fetch state (§12.1). Hidden
        // unless AutoFetchDebug.Enabled; recoloured in place on IAutoFetchService.TabStatesChanged.
        var afBarToken = AutoFetchBarToken(_autoFetch.GetTabState(ctx));
        var afBar = new Border { Width = 4, IsVisible = AutoFetchDebug.Enabled };
        Dyn(afBar, Border.BackgroundProperty, afBarToken);
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

    // ── Tab drag-and-drop (§V3-B/19.21) ──
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
    }

    private void OnStripPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragView is null || !e.GetCurrentPoint(_tabPanel).Properties.IsLeftButtonPressed)
            return;

        var panelPos = e.GetPosition(_tabPanel);
        _lastPanelPos = panelPos;
        if (!_dragging)
        {
            if (Math.Abs(panelPos.X - _pressPanelPos.X) < DragThreshold)
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

        var vpX = e.GetPosition(_scroller).X;
        _edgeDir = vpX < EdgeScrollZone ? -1
                 : vpX > _scroller.Viewport.Width - EdgeScrollZone ? 1
                 : 0;
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

    /// <summary>Live shuffle: moves the dragged shell so its slot matches the cursor's X (panel space).</summary>
    private void ReorderTo(double panelX)
    {
        var view = _dragView;
        if (view is null)
            return;
        var current = _tabViews.IndexOf(view);
        if (current < 0)
            return;

        var target = 0;
        double acc = 0;
        for (var i = 0; i < _tabViews.Count; i++)
        {
            var w = _tabViews[i].Bounds.Width;
            if (w <= 0) w = TabWidth;
            if (panelX < acc + w / 2)
                break;
            acc += w;
            target = i + 1;
        }
        target = Math.Clamp(target, 0, _tabViews.Count - 1);
        if (target == current)
            return;

        _tabViews.RemoveAt(current);
        _tabPanel.Children.Remove(view);
        _tabViews.Insert(target, view);
        // _add (when present) is always appended after the shells, so the model index maps
        // straight onto the visual index.
        _tabPanel.Children.Insert(Math.Min(target, _tabPanel.Children.Count), view);

        for (var i = 0; i < _tabViews.Count; i++)
            _tabViews[i].BorderThickness = new Thickness(i == 0 ? 0 : 1, 0, 0, 0);
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
        await _workspace.CloseAsync(ctx);
        if (_workspace.Focused is null)
            _nav.RequestNavigate("");
    }

    // ── §V3-B: multi-line tab strip ──

    private bool MultiLine => _settings?.Current.EnableMultiLineTabs ?? false;
    private int MaxRows => Math.Clamp(_settings?.Current.MaxTabRows ?? 3, 2, 5);

    // ── V5.5: tab width ──

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
    // (a disabled arrow reads as a faint ghost, matching V3).
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

/// <summary>The translucent tab copy that follows the cursor during a reorder drag.</summary>
internal sealed class AppBandDragGhost : Control, IDisposable
{
    private readonly Bitmap _image;
    private readonly Size _size;
    private Point _topLeft;

    public AppBandDragGhost(Bitmap image, Size size)
    {
        _image = image;
        _size = size;
        Opacity = 0.75;
        IsHitTestVisible = false;
        Effect = new DropShadowEffect { BlurRadius = 10, OffsetX = 0, OffsetY = 3, Opacity = 0.35 };
    }

    public void Dispose() => _image.Dispose();

    public void MoveTo(Point topLeft)
    {
        _topLeft = topLeft;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
        => context.DrawImage(_image, new Rect(_topLeft, _size));
}
