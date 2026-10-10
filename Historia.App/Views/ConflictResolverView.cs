namespace Historia.Views;

/// <summary>
/// Native 3-way conflict resolver view with aligned Ours | Theirs panes and editable Result editor.
/// Adapted from SourceGit (MIT) — see docs/third-party-licenses.md.
/// </summary>
public sealed partial class ConflictResolverView : UserControl, IDisposable
{
    internal sealed class ConflictParseSnapshot
    {
        public ConflictParse Parse { get; }
        public IReadOnlyList<ConflictPaneLine> OursLines { get; }
        public IReadOnlyList<ConflictPaneLine> TheirsLines { get; }
        public IReadOnlyList<ConflictRegionAnalysis> Analyses { get; }
        public ITextSourceVersion Version { get; }

        public ConflictParseSnapshot(
            ConflictParse parse,
            IReadOnlyList<ConflictPaneLine> oursLines,
            IReadOnlyList<ConflictPaneLine> theirsLines,
            IReadOnlyList<ConflictRegionAnalysis> analyses,
            ITextSourceVersion version)
        {
            Parse = parse;
            OursLines = oursLines;
            TheirsLines = theirsLines;
            Analyses = analyses;
            Version = version;
        }
    }

    private sealed class ConflictUnsavedWork : IUnsavedWork
    {
        private readonly ConflictResolverView _owner;
        public RepositoryContext Context { get; }
        public string Description { get; }

        public ConflictUnsavedWork(ConflictResolverView owner, RepositoryContext ctx, string path)
        {
            _owner = owner;
            Context = ctx;
            Description = $"the conflict resolution in {path}";
        }

        public Task<bool> SaveAsync() => _owner.SaveCurrentFileAsync(stage: false);
        public void Discard() => _owner.DiscardUnsavedState();
    }

    // Thickness of the Ours | Theirs and top | Result dividers; matches the window's sidebar dividers.
    private const double PaneDividerThickness = 4;

    private readonly IServiceProvider _sp;
    private readonly RepositoryContext _ctx;
    private readonly IConflictService _conflicts;
    private readonly IDialogService _dialogs;
    private readonly INotificationService _notifications;
    private readonly IUnsavedWorkRegistry? _unsavedRegistry;
    private readonly INavigationBus? _nav;

    // Cancelled in Dispose to signal in-flight work, but not disposed so in-flight tasks
    // observing its token do not throw ObjectDisposedException. A plain CTS with no timer
    // or wait handle holds no unmanaged resources.
    private readonly CancellationTokenSource _viewCts = new();
    private CancellationTokenSource? _debounceCts;
    private CancellationTokenSource? _switchCts;
    private int _switchGeneration;

    private readonly List<ConflictFile> _conflictedFiles = [];
    private ConflictFile? _currentFile;
    private ConflictWorkingCopy? _workingCopy;
    private string? _diskHash;
    private ConflictParseSnapshot? _snapshot;
    private ConflictParse? _parse;
    private ITextSourceVersion? _parseVersion;
    private IDisposable? _unsavedRegistration;
    private bool _saving;
    private string? _stopId;
    private bool _disposed;
    private bool _isSyncingScroll;
    private bool _statusRefreshRunning;
    private bool _statusRefreshPending;
    private bool _suppressTextChanged;
    private bool _inResultTextChanged;
    private bool _swallowNextSpace;

    private readonly ConflictSelectionSession _session;
    private readonly ConflictToggleMargin _oursToggleMargin;
    private readonly ConflictToggleMargin _theirsToggleMargin;
    private readonly ConflictToggleMargin _resultToggleMargin;
    private readonly ConflictExcludedLineColorizer _excludedColorizer;

    // Controls
    private readonly TextBlock _headerOurs = Text("", "ColorTextMuted", 11);
    private readonly TextBlock _headerTheirs = Text("", "ColorTextMuted", 11);

    private readonly StackPanel _opBar = new() { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
    private readonly Button _continueBtn;
    private readonly Button _skipBtn;
    private readonly Button _abortBtn;

    private readonly Border _externalEndedBanner = new();
    private readonly Border _changedOnDiskBanner = new();
    private readonly TextBlock _damagedMarkersHint = Text("", "ColorWarning", 11);

    private readonly Button _saveAndResolveBtn;
    private readonly Button _resolveFileBtn;
    private readonly List<MenuItem> _resolveFileMenuItems = [];
    private readonly Button _prevConflictBtn;
    private readonly Button _nextConflictBtn;
    private readonly TextBlock _conflictsCountText = new TextBlock
    {
        FontSize = 11,
        MinWidth = 44,
        TextAlignment = TextAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    }.Dyn(TextBlock.ForegroundProperty, "ColorTextMuted");

    private readonly ConflictPaneEditor _oursEditor = new() { IsReadOnly = true };
    private readonly ConflictPaneEditor _theirsEditor = new() { IsReadOnly = true };
    private readonly ConflictPaneEditor _resultEditor = new() { IsReadOnly = false };
    private readonly ConflictResolverMinimap _minimap = new();
    private readonly ContentControl _editorArea = new();
    private readonly Grid _splitGrid;
    private readonly ConflictLineNumberMargin _resultNumberMargin;
    private readonly Rectangle _paneDivider;
    private readonly Rectangle _resultDivider;
    private readonly GridSplitter _resultSplitter;
    private readonly Button _closeBtn;

    private readonly List<Button> _actionButtons = [];
    private readonly List<Button> _dynamicActionButtons = [];
    private readonly List<Button> _textActionButtons = [];
    private bool _isEditable;

    private Button ActionButton(string text, Action onClick, bool primary = false)
    {
        var btn = TextButton(text, onClick, primary: primary);
        btn.IsEnabled = !_isBusy;
        _actionButtons.Add(btn);
        return btn;
    }

    private Button TextActionButton(string text, Action onClick, bool primary = false)
    {
        var btn = TextButton(text, onClick, primary: primary);
        btn.IsEnabled = !_isBusy && _isEditable;
        _textActionButtons.Add(btn);
        return btn;
    }

    private Button TextActionIconButton(string icon, string tip, Action onClick)
    {
        var btn = IconButton(icon, tip, onClick);
        btn.IsEnabled = !_isBusy && _isEditable;
        _textActionButtons.Add(btn);
        return btn;
    }

    private Button DynamicActionButton(string text, Action onClick, bool primary = false)
    {
        var btn = TextButton(text, onClick, primary: primary);
        btn.IsEnabled = !_isBusy;
        _dynamicActionButtons.Add(btn);
        return btn;
    }

    private void UpdateActionButtonsEnabled()
    {
        bool enabled = !_isBusy;
        foreach (var b in _actionButtons)
        {
            b.IsEnabled = enabled;
        }
        foreach (var b in _dynamicActionButtons)
        {
            b.IsEnabled = enabled;
        }
        bool textEnabled = !_isBusy && _isEditable;
        foreach (var b in _textActionButtons)
        {
            b.IsEnabled = textEnabled;
        }

        // Whole-file actions need a loaded file; the navigation arrows need a conflict to go to.
        _resolveFileBtn.IsEnabled = enabled && _currentFile != null;
        bool canNavigate = textEnabled && NavigableRegions().Count > 0;
        _prevConflictBtn.IsEnabled = canNavigate;
        _nextConflictBtn.IsEnabled = canNavigate;
    }

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (_isBusy != value)
            {
                _isBusy = value;
                UpdateActionButtonsEnabled();
                BusyChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public bool HasUnsavedWork => _unsavedRegistration != null;

    public event EventHandler? BusyChanged;

    internal ConflictPaneEditor ResultEditorForTesting => _resultEditor;
    internal ConflictPaneEditor OursEditorForTesting => _oursEditor;
    internal ConflictPaneEditor TheirsEditorForTesting => _theirsEditor;
    internal ConflictResolverMinimap MinimapForTesting => _minimap;
    internal Button ContinueButtonForTesting => _continueBtn;
    internal Button SkipButtonForTesting => _skipBtn;
    internal StackPanel OpBarForTesting => _opBar;
    internal Button SaveAndResolveButtonForTesting => _saveAndResolveBtn;
    internal Button ResolveFileButtonForTesting => _resolveFileBtn;
    internal IReadOnlyList<MenuItem> ResolveFileMenuItemsForTesting => _resolveFileMenuItems;
    internal Button PrevConflictButtonForTesting => _prevConflictBtn;
    internal Button NextConflictButtonForTesting => _nextConflictBtn;
    internal TextBlock OursCaptionForTesting => _headerOurs;
    internal TextBlock TheirsCaptionForTesting => _headerTheirs;
    internal IReadOnlyList<Button> ActionButtonsForTesting => _actionButtons;
    internal IReadOnlyList<Button> TextActionButtonsForTesting => _textActionButtons;
    internal TextBlock ConflictsCountTextForTesting => _conflictsCountText;
    internal TextBlock DamagedMarkersHintForTesting => _damagedMarkersHint;
    internal Border ExternalEndedBannerForTesting => _externalEndedBanner;
    internal Border ChangedOnDiskBannerForTesting => _changedOnDiskBanner;
    internal ConflictParse? CurrentParseForTesting => _parse;
    internal ConflictParseSnapshot? SnapshotForTesting => _snapshot;

    /// <summary>Raised on the UI thread each time a parse snapshot is installed.</summary>
    internal event Action<ConflictParseSnapshot>? SnapshotInstalledForTesting;

    private void InstallSnapshot(ConflictParseSnapshot snapshot)
    {
        _snapshot = snapshot;
        SnapshotInstalledForTesting?.Invoke(snapshot);
    }
    internal bool IsEditableForTesting => _isEditable;
    internal string? DiskHashForTesting => _diskHash;
    internal ConflictFile? CurrentFileForTesting => _currentFile;
    internal IReadOnlyList<ConflictFile> ConflictedFilesForTesting => _conflictedFiles;
    internal ContentControl EditorAreaForTesting => _editorArea;
    internal ConflictSelectionSession SessionForTesting => _session;
    internal ConflictToggleMargin OursToggleMarginForTesting => _oursToggleMargin;
    internal ConflictToggleMargin TheirsToggleMarginForTesting => _theirsToggleMargin;
    internal ConflictToggleMargin ResultToggleMarginForTesting => _resultToggleMargin;
    internal string ComposedTextForTesting => _session.ComposeOutput();
    internal Rectangle PaneDividerForTesting => _paneDivider;
    internal Rectangle ResultDividerForTesting => _resultDivider;
    internal GridSplitter ResultSplitterForTesting => _resultSplitter;
    internal Grid SplitGridForTesting => _splitGrid;
    internal Button CloseButtonForTesting => _closeBtn;
    internal ConflictLineNumberMargin ResultNumberMarginForTesting => _resultNumberMargin;

    /// <summary>Clicks the hunk check box on <paramref name="paneRow"/> of the Ours or Theirs pane.</summary>
    internal void ClickHunkForTesting(ConflictSide side, int paneRow) => OnHunkToggleClicked(side, paneRow);

    /// <summary>The hunk box drawn on <paramref name="paneRow"/> of the Ours or Theirs pane.</summary>
    internal ConflictToggleGlyph HunkGlyphForTesting(ConflictSide side, int paneRow) => PaneHunkGlyph(side, paneRow);

    public ConflictResolverView(IServiceProvider sp, RepositoryContext ctx)
    {
        _sp = sp;
        _ctx = ctx;
        _conflicts = sp.GetRequiredService<IConflictService>();
        _dialogs = sp.GetRequiredService<IDialogService>();
        _notifications = sp.GetRequiredService<INotificationService>();
        _unsavedRegistry = sp.GetService(typeof(IUnsavedWorkRegistry)) as IUnsavedWorkRegistry;
        _nav = sp.GetService(typeof(INavigationBus)) as INavigationBus;
        _oursEditor.ServicesProvider = sp;
        _theirsEditor.ServicesProvider = sp;
        _resultEditor.ServicesProvider = sp;

        // Managed conflicts: kept / struck rows over the Result document
        _session = new ConflictSelectionSession(_resultEditor.Document);
        _session.Changed += OnSessionChanged;

        // Margins and Renderers
        _oursToggleMargin = new ConflictToggleMargin(row => PaneHunkGlyph(ConflictSide.Ours, row), row => PaneHunkTip(ConflictSide.Ours, row));
        _oursToggleMargin.ToggleClicked += (_, row) => OnHunkToggleClicked(ConflictSide.Ours, row);
        _oursEditor.TextArea.LeftMargins.Add(_oursToggleMargin);
        _oursEditor.TextArea.LeftMargins.Add(new ConflictLineNumberMargin(_oursEditor, () => GetPaneLines(ConflictSide.Ours), row => PaneTint(ConflictSide.Ours, row)));
        _oursEditor.TextArea.TextView.BackgroundRenderers.Add(
            new ConflictBackgroundRenderer(() => ConflictSide.Ours, () => _parse, () => GetPaneLines(ConflictSide.Ours), () => _snapshot?.Analyses));

        _theirsToggleMargin = new ConflictToggleMargin(row => PaneHunkGlyph(ConflictSide.Theirs, row), row => PaneHunkTip(ConflictSide.Theirs, row));
        _theirsToggleMargin.ToggleClicked += (_, row) => OnHunkToggleClicked(ConflictSide.Theirs, row);
        _theirsEditor.TextArea.LeftMargins.Add(_theirsToggleMargin);
        _theirsEditor.TextArea.LeftMargins.Add(new ConflictLineNumberMargin(_theirsEditor, () => GetPaneLines(ConflictSide.Theirs), row => PaneTint(ConflictSide.Theirs, row)));
        _theirsEditor.TextArea.TextView.BackgroundRenderers.Add(
            new ConflictBackgroundRenderer(() => ConflictSide.Theirs, () => _parse, () => GetPaneLines(ConflictSide.Theirs), () => _snapshot?.Analyses));

        _resultToggleMargin = new ConflictToggleMargin(_session.RowGlyph, line => _session.IsDeletionRow(line)
            ? (_session.IsExcluded(line) ? "Mark as deleted" : "Unmark the deletion")
            : (_session.IsExcluded(line) ? "Keep this line" : "Drop this line"));
        _resultToggleMargin.ToggleClicked += (_, line) => OnRowToggleClicked(line);
        _resultEditor.TextArea.LeftMargins.Add(_resultToggleMargin);
        _resultNumberMargin = new ConflictLineNumberMargin(_resultEditor, getTint: ResultTint);
        _resultEditor.TextArea.LeftMargins.Add(_resultNumberMargin);
        _resultEditor.TextArea.TextView.BackgroundRenderers.Add(
            new ConflictBackgroundRenderer(() => null, () => _parse, getRowVisual: _session.RowVisual));
        _excludedColorizer = new ConflictExcludedLineColorizer(_session.IsExcluded);
        _resultEditor.TextArea.TextView.LineTransformers.Add(_excludedColorizer);
        _resultEditor.AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(EnsureExcludedColorizerLast);

        // Shared scroll synchronization
        _oursEditor.ScrollOffsetChanged += (_, offset) => SyncScroll(_oursEditor, offset);
        _theirsEditor.ScrollOffsetChanged += (_, offset) => SyncScroll(_theirsEditor, offset);
        _resultEditor.ScrollOffsetChanged += (_, offset) => SyncScroll(_resultEditor, offset);

        _minimap.LineNavigationRequested += (_, line) =>
        {
            _resultEditor.ScrollToLine(line);
            _oursEditor.ScrollToLine(line);
            _theirsEditor.ScrollToLine(line);
        };

        // Wire document changes and caret changes
        _resultEditor.Document.TextChanged += OnResultDocumentTextChanged;
        _resultEditor.TextArea.Caret.PositionChanged += OnResultCaretPositionChanged;

        // Operation actions (toolbar, right)
        _continueBtn = ActionButton("Continue", () => _ = ContinueAsync(), primary: true);
        _skipBtn = ActionButton("Skip", () => _ = SkipAsync());
        _abortBtn = ActionButton("Abort", () => _ = AbortAsync());
        ToolTip.SetTip(_continueBtn, "Finish the operation once every file is resolved.");
        ToolTip.SetTip(_skipBtn, "Drop this commit and move to the next one.");
        ToolTip.SetTip(_abortBtn, "Cancel the operation and restore the state before it started.");

        _opBar.Children.Add(_continueBtn);
        _opBar.Children.Add(_skipBtn);
        _opBar.Children.Add(_abortBtn);

        // Banners
        _externalEndedBanner.IsVisible = false;
        _externalEndedBanner.Padding = new Thickness(12, 6);
        _externalEndedBanner.BorderThickness = new Thickness(0, 0, 0, 1);
        _externalEndedBanner.Dyn(Border.BackgroundProperty, "ColorSurface2").Dyn(Border.BorderBrushProperty, "ColorBorder");
        var endMsg = Text("The operation ended outside Historia — save or discard your edits.", "ColorWarning", 12);
        _externalEndedBanner.Child = endMsg;

        _changedOnDiskBanner.IsVisible = false;
        _changedOnDiskBanner.Padding = new Thickness(12, 6);
        _changedOnDiskBanner.BorderThickness = new Thickness(0, 0, 0, 1);
        _changedOnDiskBanner.Dyn(Border.BackgroundProperty, "ColorSurface2").Dyn(Border.BorderBrushProperty, "ColorBorder");
        var diskMsgPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
        diskMsgPanel.Children.Add(Text("This file changed on disk outside Historia.", "ColorWarning", 12));
        diskMsgPanel.Children.Add(ActionButton("Reload", () => _ = ReloadFromDiskAsync(), primary: true));
        diskMsgPanel.Children.Add(ActionButton("Keep mine", () => _ = KeepMineAsync()));
        _changedOnDiskBanner.Child = diskMsgPanel;

        // File actions (toolbar, left)
        _saveAndResolveBtn = TextActionButton("Save & mark resolved", () => _ = SaveAndMarkResolvedAsync(), primary: true);
        ToolTip.SetTip(_saveAndResolveBtn, "Save this file and mark it resolved (git add). Ctrl+S saves without marking.");

        var resolveFileMenu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
        var resolveFileItems = new List<Control>
        {
            ResolveFileMenuItem("Take ours (whole file)", () => _ = TakeOursCurrentAsync()),
            ResolveFileMenuItem("Take theirs (whole file)", () => _ = TakeTheirsCurrentAsync()),
            new Separator(),
            ResolveFileMenuItem("Reset file to conflicted", () => _ = RecreateConflictAsync()),
            new Separator(),
            ResolveFileMenuItem("Open in external tool", () => _ = LaunchExternalToolAsync()),
        };
        resolveFileMenu.ItemsSource = resolveFileItems;
        _resolveFileBtn = TextButton("Resolve file ▾", () => { });
        _resolveFileBtn.Flyout = resolveFileMenu;
        _resolveFileBtn.IsEnabled = false;
        ToolTip.SetTip(_resolveFileBtn, "Whole-file actions");

        var fileActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        fileActions.Children.Add(_saveAndResolveBtn);
        fileActions.Children.Add(_resolveFileBtn);
        fileActions.Children.Add(_damagedMarkersHint);

        // Conflict navigation (toolbar, centre)
        _prevConflictBtn = TextActionIconButton("arrow_upward", "Previous conflict", GoToPreviousConflict);
        _nextConflictBtn = TextActionIconButton("arrow_downward", "Next conflict", GoToNextConflict);
        var navigation = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        navigation.Children.Add(_prevConflictBtn);
        navigation.Children.Add(_conflictsCountText);
        navigation.Children.Add(_nextConflictBtn);

        // Closing hides the resolver and keeps its edits; it never saves or discards.
        _closeBtn = IconButton("close", "Close the resolver — unsaved edits are kept", () => _ctx.State.SetConflictResolverClosed(true));
        _closeBtn.VerticalAlignment = VerticalAlignment.Center;
        var trailing = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        trailing.Children.Add(_opBar);
        trailing.Children.Add(_closeBtn);

        var toolbar = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,*"), Margin = new Thickness(8, 4) };
        Grid.SetColumn(fileActions, 0);
        Grid.SetColumn(navigation, 1);
        Grid.SetColumn(trailing, 2);
        toolbar.Children.Add(fileActions);
        toolbar.Children.Add(navigation);
        toolbar.Children.Add(trailing);

        // Two-column Ours | Theirs grid, each pane under a caption naming its side
        var topPanesGrid = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*"),
            ColumnDefinitions = new ColumnDefinitions("*,Auto,*"),
        };
        var oursCaption = PaneCaption(_headerOurs);
        var theirsCaption = PaneCaption(_headerTheirs);
        Grid.SetColumn(oursCaption, 0);
        Grid.SetColumn(theirsCaption, 2);
        Grid.SetRow(_oursEditor, 1);
        Grid.SetColumn(_oursEditor, 0);
        _paneDivider = new Rectangle { Width = PaneDividerThickness, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Stretch };
        _paneDivider.Dyn(Shape.FillProperty, "ColorBorder");
        Grid.SetColumn(_paneDivider, 1);
        Grid.SetRowSpan(_paneDivider, 2);
        Grid.SetRow(_theirsEditor, 1);
        Grid.SetColumn(_theirsEditor, 2);
        topPanesGrid.Children.Add(oursCaption);
        topPanesGrid.Children.Add(theirsCaption);
        topPanesGrid.Children.Add(_oursEditor);
        topPanesGrid.Children.Add(_paneDivider);
        topPanesGrid.Children.Add(_theirsEditor);

        // Editor layout: TopPanes / Splitter / Result
        _splitGrid = new Grid
        {
            RowDefinitions = new RowDefinitions($"*,{PaneDividerThickness},*"),
            ColumnDefinitions = new ColumnDefinitions("*,12"),
        };
        Grid.SetRow(topPanesGrid, 0);
        Grid.SetColumn(topPanesGrid, 0);

        // A divider bar marks the boundary; the transparent splitter over it stays the drag target.
        _resultDivider = new Rectangle { Height = PaneDividerThickness, IsHitTestVisible = false, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Center };
        _resultDivider.Dyn(Shape.FillProperty, "ColorBorder");
        Grid.SetRow(_resultDivider, 1);
        Grid.SetColumn(_resultDivider, 0);

        _resultSplitter = new GridSplitter
        {
            Height = PaneDividerThickness,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Background = Brushes.Transparent,
            Focusable = false,
        };
        Grid.SetRow(_resultSplitter, 1);
        Grid.SetColumn(_resultSplitter, 0);

        Grid.SetRow(_resultEditor, 2);
        Grid.SetColumn(_resultEditor, 0);

        Grid.SetRow(_minimap, 0);
        Grid.SetRowSpan(_minimap, 3);
        Grid.SetColumn(_minimap, 1);

        _splitGrid.Children.Add(topPanesGrid);
        _splitGrid.Children.Add(_resultDivider);
        _splitGrid.Children.Add(_resultSplitter);
        _splitGrid.Children.Add(_resultEditor);
        _splitGrid.Children.Add(_minimap);

        _editorArea.Content = _splitGrid;

        // Toolbar & banner container
        var rightTop = new StackPanel { Spacing = 4, Margin = new Thickness(4, 2) };
        rightTop.Children.Add(toolbar);
        rightTop.Children.Add(_externalEndedBanner);
        rightTop.Children.Add(_changedOnDiskBanner);

        var rightDock = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(rightTop, Dock.Top);
        rightDock.Children.Add(rightTop);
        rightDock.Children.Add(_editorArea);

        Content = rightDock;
        this.Dyn(BackgroundProperty, "ColorBg");

        AddHandler(KeyDownEvent, OnViewKeyDown, RoutingStrategies.Tunnel);
        AddHandler(TextInputEvent, OnViewTextInput, RoutingStrategies.Tunnel);
        AddHandler(PointerPressedEvent, OnViewPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);

        _ctx.State.StatusChanged += OnStatusChanged;
        _ctx.State.SelectedConflictPathChanged += OnSelectedConflictPathChanged;
        _ctx.State.ConflictSessionChanged += OnConflictSessionChanged;
        UpdateCloseButton();
        _ = LoadAsync();
    }

    private MenuItem ResolveFileMenuItem(string header, Action onClick)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => onClick();
        _resolveFileMenuItems.Add(item);
        return item;
    }

    private static Border PaneCaption(TextBlock text)
    {
        text.Margin = default;
        var caption = new Border
        {
            Child = text,
            Padding = new Thickness(8, 2),
            BorderThickness = new Thickness(0, 0, 0, 1),
        };
        caption.Dyn(Border.BorderBrushProperty, "ColorBorder");
        return caption;
    }

    private void SetSideCaptions(ConflictSideLabels labels)
    {
        _headerOurs.Text = $"Ours · {labels.OursTitle}";
        _headerTheirs.Text = $"Theirs · {labels.TheirsTitle}";
        ToolTip.SetTip(_headerOurs, labels.OursDetail);
        ToolTip.SetTip(_headerTheirs, labels.TheirsDetail);
    }

    /// <summary>Ctrl+S saves the file without marking it resolved; Ctrl+Space, pressed in the
    /// Result, keeps or drops the managed row under its caret.</summary>
    private void OnViewKeyDown(object? sender, KeyEventArgs e)
    {
        _swallowNextSpace = false;
        if (e.Key == Key.S && e.KeyModifiers == KeyModifiers.Control)
        {
            if (_isEditable && !_isBusy)
            {
                _ = SaveCurrentFileAsync(stage: false);
            }
            e.Handled = true;
        }
        else if (e.Key == Key.Space && e.KeyModifiers == KeyModifiers.Control)
        {
            if (_isEditable && !_isBusy
                && e.Source is Visual source && _resultEditor.IsVisualAncestorOf(source)
                && _session.ToggleRow(_resultEditor.TextArea.Caret.Line - 1))
            {
                e.Handled = true;
                _swallowNextSpace = true;
            }
        }
    }

    /// <summary>Keeps a handled Ctrl+Space from also typing a space into the row it toggled.</summary>
    private void OnViewTextInput(object? sender, TextInputEventArgs e)
    {
        if (_swallowNextSpace && e.Text == " ")
        {
            e.Handled = true;
        }
        _swallowNextSpace = false;
    }

    private void OnViewPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        ShowChangesPanel();
    }

    private void ShowChangesPanel()
    {
        if (_ctx.State.SelectedCommitId is not null)
        {
            _ctx.State.SetSelectedCommit(null);
        }

        // Skip requesting navigation when already on the repo route because DetailRouter rebuilds the view on every request.
        if (_nav is not null)
        {
            var head = (_nav.CurrentRoute ?? string.Empty).Split('?', 2)[0].TrimStart('/');
            if (!string.Equals(head, "repo", StringComparison.Ordinal))
            {
                _nav.RequestNavigate("repo");
            }
        }
    }

    private void OnHunkToggleClicked(ConflictSide side, int paneRow)
    {
        int line = PaneDocLine(side, paneRow);
        if (line < 0 || _isBusy || !_isEditable)
        {
            return;
        }

        var managed = _session.RegionAt(line);
        if (managed != null)
        {
            _session.ToggleHunk(managed, side);
            return;
        }

        var region = FreshMarkerRegionAt(line);
        if (region != null)
        {
            _session.Activate(region, s => s.SetHunk(side, true), AnalysisFor(region));
        }
    }

    private void OnRowToggleClicked(int line)
    {
        if (_isBusy || !_isEditable)
        {
            return;
        }

        _session.ToggleRow(line);
    }

    /// <summary>
    /// Every session change: a toggle registers unsaved work exactly like typing, and the panes
    /// realign at once. A change raised while the Result's own TextChanged handler runs is left to
    /// that handler.
    /// </summary>
    private void OnSessionChanged(object? sender, EventArgs e)
    {
        if (_suppressTextChanged || _inResultTextChanged || _currentFile == null || !_isEditable)
        {
            return;
        }

        RegisterUnsavedWork();
        if (_parseVersion == null || _resultEditor.Document.Version.CompareAge(_parseVersion) != 0)
        {
            _debounceCts?.Cancel();
            UpdateConflictParseSync();
            UpdateEditorsFromParse();
        }
        else
        {
            RefreshSessionVisuals();
        }
    }

    /// <summary>Redraws what a selection change alters without moving text: struck lines, check
    /// boxes, row washes, minimap and counter.</summary>
    private void RefreshSessionVisuals()
    {
        _minimap.ResolvedRanges = ManagedRanges();
        UpdateConflictsCount();
        _oursEditor.TextArea.TextView.Redraw();
        _theirsEditor.TextArea.TextView.Redraw();
        _resultEditor.TextArea.TextView.Redraw();
        _resultNumberMargin.InvalidateVisual();
    }

    private (int Start, int End)[] ManagedRanges()
        => _session.Regions.Where(r => !r.IsBroken).Select(r => (r.StartLine, r.EndLine)).ToArray();

    /// <summary>Syntax colouring is attached after the editor joins the tree and again on every
    /// file change; the struck-line colorizer must run after it to win the foreground.</summary>
    private void EnsureExcludedColorizerLast()
    {
        var transformers = _resultEditor.TextArea.TextView.LineTransformers;
        if (transformers.Count > 0 && ReferenceEquals(transformers[^1], _excludedColorizer))
        {
            return;
        }
        transformers.Remove(_excludedColorizer);
        transformers.Add(_excludedColorizer);
    }

    /// <summary>The marker region on <paramref name="line"/> in a parse of the current text.</summary>
    private ConflictRegion? FreshMarkerRegionAt(int line)
    {
        if (_parseVersion == null || _resultEditor.Document.Version.CompareAge(_parseVersion) != 0)
        {
            UpdateConflictParseSync();
        }
        return _parse?.Regions.FirstOrDefault(r => line >= r.StartLine && line <= r.EndLine);
    }

    private ConflictRegionAnalysis? AnalysisFor(ConflictRegion region)
        => _snapshot?.Analyses.FirstOrDefault(a => a.Region.StartLine == region.StartLine && RegionLinesEqual(a.Region, region))
           ?? _snapshot?.Analyses.FirstOrDefault(a => RegionLinesEqual(a.Region, region));

    /// <summary>Managed regions' intra-line highlights, moved to their rows' current lines.</summary>
    private List<ConflictRegionAnalysis> ManagedAnalyses()
    {
        var result = new List<ConflictRegionAnalysis>();
        foreach (var region in _session.Regions)
        {
            if (region.IsBroken || region.Analysis is not { } analysis)
            {
                continue;
            }
            result.Add(new ConflictRegionAnalysis(
                analysis.Region,
                RemapToRows(analysis.OursHighlights, region, ConflictSide.Ours),
                RemapToRows(analysis.TheirsHighlights, region, ConflictSide.Theirs),
                null));
        }
        return result;
    }

    private static List<RegionLineHighlight> RemapToRows(IReadOnlyList<RegionLineHighlight> highlights, ConflictSelectionSession.ManagedRegion region, ConflictSide side)
    {
        int count = Math.Min(highlights.Count, region.Selection.RowCount(side));
        var remapped = new List<RegionLineHighlight>(count);
        for (int i = 0; i < count; i++)
        {
            var h = highlights[i];
            remapped.Add(new RegionLineHighlight(h.LineIndex, region.RowLine(side, i), h.Spans, h.ChangedFromBase));
        }
        return remapped;
    }

    /// <summary>The single primary (blue) button is the next step: Save &amp; mark resolved
    /// while conflicted files remain, Continue once none do.</summary>
    private void UpdatePrimaryAction()
    {
        bool filesRemain = _conflictedFiles.Count > 0;
        SetPrimary(_saveAndResolveBtn, filesRemain);
        SetPrimary(_continueBtn, !filesRemain);
    }

    private static void SetPrimary(Button button, bool primary)
    {
        button.Classes.Set("primary", primary);
        button.Classes.Set("secondary", !primary);
    }

    private void OnStatusChanged(object? s, StatusChangedEventArgs e)
    {
        Dispatcher.UIThread.Post(async () =>
        {
            if (_disposed)
            {
                return;
            }

            if (_statusRefreshRunning)
            {
                _statusRefreshPending = true;
                return;
            }

            _statusRefreshRunning = true;
            try
            {
                do
                {
                    _statusRefreshPending = false;
                    await ProcessStatusChangeAsync();
                } while (_statusRefreshPending && !_disposed);
            }
            finally
            {
                _statusRefreshRunning = false;
            }
        });
    }

    private async Task ProcessStatusChangeAsync()
    {
        try
        {
            var labels = await _conflicts.GetSideLabelsAsync(_ctx, _viewCts.Token);
            if (_disposed) return;
            bool stopChanged = _stopId != null && labels.StopId != _stopId;
            _stopId = labels.StopId;
            SetSideCaptions(labels);
            UpdateCloseButton();

            var op = _ctx.State.PendingOperation;
            if (op == OperationState.None)
            {
                _opBar.IsVisible = false;
                _externalEndedBanner.IsVisible = HasUnsavedWork;
            }
            else
            {
                _externalEndedBanner.IsVisible = false;
                _opBar.IsVisible = true;
                _skipBtn.IsVisible = op is OperationState.Rebasing or OperationState.CherryPicking or OperationState.Reverting;
            }

            await RefreshConflictsAsync();
            if (_disposed) return;
            await CheckExternalDiskChangeAsync();
            if (_disposed) return;

            bool currentStillConflicted = _currentFile != null &&
                _conflictedFiles.Any(f => string.Equals(f.FilePath, _currentFile.FilePath, StringComparison.Ordinal));

            if (stopChanged)
            {
                if (HasUnsavedWork)
                {
                    _changedOnDiskBanner.IsVisible = true;
                }
                else
                {
                    if (currentStillConflicted)
                    {
                        var matching = _conflictedFiles.First(f => string.Equals(f.FilePath, _currentFile!.FilePath, StringComparison.Ordinal));
                        await SwitchToFileAsync(matching);
                    }
                    else if (_conflictedFiles.Count > 0)
                    {
                        await SwitchToFileAsync(_conflictedFiles[0]);
                    }
                    else
                    {
                        ShowAllConflictsResolvedCard();
                    }
                }
            }
            else if (!currentStillConflicted)
            {
                if (HasUnsavedWork)
                {
                    _changedOnDiskBanner.IsVisible = true;
                }
                else
                {
                    if (_conflictedFiles.Count > 0)
                    {
                        await SwitchToFileAsync(_conflictedFiles[0]);
                    }
                    else
                    {
                        ShowAllConflictsResolvedCard();
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!_disposed)
            {
                _notifications.Publish(NotificationSeverity.Error, ex.Message);
            }
        }
    }

    private void OnConflictSessionChanged(object? sender, EventArgs e)
        => Dispatcher.UIThread.Post(() =>
        {
            if (!_disposed)
            {
                UpdateCloseButton();
            }
        });

    /// <summary>Closing needs a live session: once it has ended nothing would bring back a hidden
    /// resolver, so one kept open only for its unsaved edits cannot be closed.</summary>
    private void UpdateCloseButton() => _closeBtn.IsVisible = _ctx.State.IsConflictSessionActive;

    private void OnSelectedConflictPathChanged(object? sender, SelectedConflictPathChangedEventArgs e)
    {
        Dispatcher.UIThread.Post(async () =>
        {
            if (_disposed)
            {
                return;
            }

            var path = e.Path;
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            if (_currentFile != null && string.Equals(_currentFile.FilePath, path, StringComparison.Ordinal))
            {
                return;
            }

            try
            {
                var target = _conflictedFiles.FirstOrDefault(f => string.Equals(f.FilePath, path, StringComparison.Ordinal));
                if (target == null)
                {
                    await RefreshConflictsAsync();
                    target = _conflictedFiles.FirstOrDefault(f => string.Equals(f.FilePath, path, StringComparison.Ordinal));
                    if (target == null)
                    {
                        return;
                    }
                }

                if (HasUnsavedWork)
                {
                    var work = _unsavedRegistry?.For(_ctx) ?? [new ConflictUnsavedWork(this, _ctx, _currentFile?.FilePath ?? "")];
                    var proceed = await UnsavedWorkPrompt.ResolveAsync(work, _dialogs);
                    if (!proceed)
                    {
                        _ctx.State.SetSelectedConflictPath(_currentFile?.FilePath);
                        return;
                    }
                }

                await SwitchToFileAsync(target);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _notifications.Publish(NotificationSeverity.Error, $"Could not open {path}: {ex.Message}", ex.NotificationDetails());
                _ctx.State.SetSelectedConflictPath(_currentFile?.FilePath);
            }
        });
    }

    public async Task LoadAsync()
    {
        try
        {
            var labels = await _conflicts.GetSideLabelsAsync(_ctx, _viewCts.Token);
            _stopId = labels.StopId;
            SetSideCaptions(labels);
            UpdateCloseButton();

            var op = _ctx.State.PendingOperation;
            if (op == OperationState.None)
            {
                _opBar.IsVisible = false;
                if (HasUnsavedWork)
                {
                    _externalEndedBanner.IsVisible = true;
                }
            }
            else
            {
                _opBar.IsVisible = true;
                _skipBtn.IsVisible = op is OperationState.Rebasing or OperationState.CherryPicking or OperationState.Reverting;
            }

            await RefreshConflictsAsync();

            if (_conflictedFiles.Count == 0)
            {
                ShowAllConflictsResolvedCard();
            }
            else if (_currentFile == null || !_conflictedFiles.Any(f => string.Equals(f.FilePath, _currentFile.FilePath, StringComparison.Ordinal)))
            {
                if (!HasUnsavedWork)
                {
                    var initialPath = _ctx.State.SelectedConflictPath;
                    var initialFile = initialPath != null
                        ? _conflictedFiles.FirstOrDefault(f => string.Equals(f.FilePath, initialPath, StringComparison.Ordinal)) ?? _conflictedFiles[0]
                        : _conflictedFiles[0];
                    await SwitchToFileAsync(initialFile);
                }
            }
            else
            {
                _currentFile = _conflictedFiles.First(f => string.Equals(f.FilePath, _currentFile.FilePath, StringComparison.Ordinal));
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _notifications.Publish(NotificationSeverity.Error, ex.Message, ex.NotificationDetails());
        }
    }

    private async Task RefreshConflictsAsync()
    {
        if (_disposed) return;
        var files = await _conflicts.GetConflictsAsync(_ctx, _viewCts.Token);
        if (_disposed) return;
        _conflictedFiles.Clear();
        _conflictedFiles.AddRange(files);
        UpdatePrimaryAction();
    }

    private void ShowAllConflictsResolvedCard()
    {
        _isEditable = false;
        _debounceCts?.Cancel();
        _parse = null;
        _parseVersion = null;
        _snapshot = null;
        _minimap.Parse = null;
        _conflictsCountText.IsVisible = false;
        _damagedMarkersHint.IsVisible = false;
        _changedOnDiskBanner.IsVisible = false;
        _minimap.ResolvedRanges = null;

        _dynamicActionButtons.Clear();
        _currentFile = null;
        _workingCopy = null;
        _diskHash = null;
        UpdateActionButtonsEnabled();
        _suppressTextChanged = true;
        try
        {
            _session.Clear();
            _resultEditor.Text = string.Empty;
        }
        finally
        {
            _suppressTextChanged = false;
        }
        _oursEditor.Text = string.Empty;
        _theirsEditor.Text = string.Empty;
        var card = new StackPanel { Spacing = 10, Margin = new Thickness(24) };
        card.Children.Add(Text("✓ All conflicts resolved", "ColorSuccess", 14, FontWeight.Bold));
        card.Children.Add(Text("Press Continue to finish the operation.", "ColorTextMuted", 12));
        _editorArea.Content = card;
        _ctx.State.SetSelectedConflictPath(null);
    }

    /// <summary>
    /// Switches the active conflict file in the resolver.
    /// Callers must hold no unsaved work or have resolved it through Save / Discard / Reload.
    /// </summary>
    private async Task SwitchToFileAsync(ConflictFile file)
    {
        int gen = ++_switchGeneration;
        _switchCts?.Cancel();
        _switchCts?.Dispose();
        _switchCts = CancellationTokenSource.CreateLinkedTokenSource(_viewCts.Token);
        var ct = _switchCts.Token;

        ConflictWorkingCopy copy;
        try
        {
            copy = await _conflicts.LoadWorkingCopyAsync(_ctx, file.FilePath, ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (gen != _switchGeneration || _disposed)
        {
            return;
        }

        _currentFile = file;
        _workingCopy = copy;
        _diskHash = copy.DiskHash;
        UpdateActionButtonsEnabled();
        _changedOnDiskBanner.IsVisible = false;
        _snapshot = null;
        _oursEditor.FilePath = file.FilePath;
        _theirsEditor.FilePath = file.FilePath;
        _resultEditor.FilePath = file.FilePath;
        EnsureExcludedColorizerLast();

        _suppressTextChanged = true;
        try
        {
            _session.Clear();
            _resultEditor.Text = copy.Text ?? string.Empty;
        }
        finally
        {
            _suppressTextChanged = false;
        }
        _resultEditor.Document.UndoStack.MarkAsOriginalFile();
        _parseVersion = _resultEditor.Document.Version;
        UnregisterUnsavedWork();

        bool isEditable = !file.IsBinary && !file.IsSubmodule &&
                          !copy.IsBinary && !copy.UnsupportedEncoding &&
                          !copy.ExceedsBudget && copy.Text != null;

        if (!isEditable)
        {
            ShowFileLevelCard(file, copy);
        }
        else
        {
            _isEditable = true;
            UpdateActionButtonsEnabled();

            if (!ReferenceEquals(_editorArea.Content, _splitGrid))
            {
                _editorArea.Content = _splitGrid;
            }

            _resultEditor.IsReadOnly = false;
            // Setting IsReadOnly replaces the read-only provider; struck rows need the session's.
            _resultEditor.TextArea.ReadOnlySectionProvider = _session.ReadOnlySections;
            UpdateConflictParseSync();
            UpdateEditorsFromParse();
        }

        _ctx.State.SetSelectedConflictPath(file.FilePath);
    }

    private void ShowFileLevelCard(ConflictFile file, ConflictWorkingCopy copy)
    {
        _isEditable = false;
        UpdateActionButtonsEnabled();
        _debounceCts?.Cancel();
        _parse = null;
        _parseVersion = null;
        _snapshot = null;
        _minimap.Parse = null;
        _minimap.ResolvedRanges = null;
        _conflictsCountText.IsVisible = false;
        _damagedMarkersHint.IsVisible = false;

        _dynamicActionButtons.Clear();
        var card = new StackPanel { Spacing = 10, Margin = new Thickness(24) };
        card.Children.Add(Text(file.FilePath, "ColorText", 14, FontWeight.Bold));
        card.Children.Add(Text($"Status: {file.Status}", "ColorTextMuted", 12));

        if (file.IsSubmodule)
        {
            card.Children.Add(Text("Submodule conflict — take a side to resolve.", "ColorWarning", 12));
        }
        else if (copy.IsBinary)
        {
            card.Children.Add(Text("Binary file conflict — textual merge unavailable.", "ColorTextMuted", 12));
        }
        else if (copy.ExceedsBudget)
        {
            card.Children.Add(Text("File exceeds merge size budget (5 MB / 50 000 lines).", "ColorWarning", 12));
        }

        var acts = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        acts.Children.Add(DynamicActionButton("Take ours", () => _ = TakeOursCurrentAsync()));
        acts.Children.Add(DynamicActionButton("Take theirs", () => _ = TakeTheirsCurrentAsync()));
        if (!file.IsSubmodule)
        {
            acts.Children.Add(DynamicActionButton("External tool", () => _ = LaunchExternalToolAsync()));
        }
        card.Children.Add(acts);

        _editorArea.Content = card;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _ctx.State.StatusChanged -= OnStatusChanged;
        _ctx.State.SelectedConflictPathChanged -= OnSelectedConflictPathChanged;
        _ctx.State.ConflictSessionChanged -= OnConflictSessionChanged;
        _viewCts.Cancel();
        _switchCts?.Cancel();
        _switchCts?.Dispose();
        _debounceCts?.Cancel();
        _debounceCts?.Dispose();
        UnregisterUnsavedWork();
    }
}
