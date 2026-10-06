namespace Historia.Views;

/// <summary>
/// Native 3-way conflict resolver view with aligned Ours | Theirs panes and editable Result editor.
/// Adapted from SourceGit (MIT) — see docs/third-party-licenses.md.
/// </summary>
public sealed class ConflictResolverView : UserControl, IDisposable
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
    internal Button AbortButtonForTesting => _abortBtn;
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
        _oursEditor.ServicesProvider = sp;
        _theirsEditor.ServicesProvider = sp;
        _resultEditor.ServicesProvider = sp;

        // Managed conflicts: kept / struck rows over the Result document
        _session = new ConflictSelectionSession(_resultEditor.Document);
        _session.Changed += OnSessionChanged;

        // Margins and Renderers
        _oursToggleMargin = new ConflictToggleMargin(_oursEditor, row => PaneHunkGlyph(ConflictSide.Ours, row), _ => "Use these ours lines");
        _oursToggleMargin.ToggleClicked += (_, row) => OnHunkToggleClicked(ConflictSide.Ours, row);
        _oursEditor.TextArea.LeftMargins.Add(_oursToggleMargin);
        _oursEditor.TextArea.LeftMargins.Add(new ConflictLineNumberMargin(_oursEditor, () => GetPaneLines(ConflictSide.Ours), row => PaneTint(ConflictSide.Ours, row)));
        _oursEditor.TextArea.TextView.BackgroundRenderers.Add(
            new ConflictBackgroundRenderer(_oursEditor, () => ConflictSide.Ours, () => _parse, () => GetPaneLines(ConflictSide.Ours), () => _snapshot?.Analyses));

        _theirsToggleMargin = new ConflictToggleMargin(_theirsEditor, row => PaneHunkGlyph(ConflictSide.Theirs, row), _ => "Use these theirs lines");
        _theirsToggleMargin.ToggleClicked += (_, row) => OnHunkToggleClicked(ConflictSide.Theirs, row);
        _theirsEditor.TextArea.LeftMargins.Add(_theirsToggleMargin);
        _theirsEditor.TextArea.LeftMargins.Add(new ConflictLineNumberMargin(_theirsEditor, () => GetPaneLines(ConflictSide.Theirs), row => PaneTint(ConflictSide.Theirs, row)));
        _theirsEditor.TextArea.TextView.BackgroundRenderers.Add(
            new ConflictBackgroundRenderer(_theirsEditor, () => ConflictSide.Theirs, () => _parse, () => GetPaneLines(ConflictSide.Theirs), () => _snapshot?.Analyses));

        _resultToggleMargin = new ConflictToggleMargin(_resultEditor, _session.RowGlyph, line => _session.IsExcluded(line) ? "Keep this line" : "Drop this line");
        _resultToggleMargin.ToggleClicked += (_, line) => OnRowToggleClicked(line);
        _resultEditor.TextArea.LeftMargins.Add(_resultToggleMargin);
        _resultNumberMargin = new ConflictLineNumberMargin(_resultEditor, getTint: ResultTint);
        _resultEditor.TextArea.LeftMargins.Add(_resultNumberMargin);
        _resultEditor.TextArea.TextView.BackgroundRenderers.Add(
            new ConflictBackgroundRenderer(_resultEditor, () => null, () => _parse, getRowVisual: _session.RowVisual));
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

        _ctx.State.StatusChanged += OnStatusChanged;
        _ctx.State.SelectedConflictPathChanged += OnSelectedConflictPathChanged;
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

    private void OnResultDocumentTextChanged(object? sender, EventArgs e)
    {
        if (_suppressTextChanged || _currentFile == null)
        {
            return;
        }

        // Rows first: everything below reads the session's spans for this text.
        bool reinstalled;
        _inResultTextChanged = true;
        try
        {
            reinstalled = _session.Normalize();
        }
        finally
        {
            _inResultTextChanged = false;
        }

        RegisterUnsavedWork();

        if (_session.IsMutating)
        {
            return; // the session realigns the panes once its edit is done
        }

        if (reinstalled)
        {
            // An undo/redo of a toggle can move rows: realign now rather than after the debounce.
            _debounceCts?.Cancel();
            UpdateConflictParseSync();
            UpdateEditorsFromParse();
            return;
        }

        _debounceCts?.Cancel();
        _debounceCts?.Dispose();
        _debounceCts = new CancellationTokenSource();
        var ct = _debounceCts.Token;

        var docSnapshot = _resultEditor.Document.CreateSnapshot();
        var versionSnapshot = _resultEditor.Document.Version;
        int markerSize = _workingCopy?.MarkerSize ?? 7;
        var spans = _session.Spans;
        var managedAnalyses = ManagedAnalyses();

        Task.Delay(150, ct).ContinueWith(_ =>
        {
            if (ct.IsCancellationRequested || _disposed)
            {
                return;
            }

            var text = docSnapshot.Text;
            var parse = ConflictMarkerParser.Parse(text, markerSize);
            var oursLines = ConflictPaneBuilder.Build(parse, ConflictSide.Ours, spans);
            var theirsLines = ConflictPaneBuilder.Build(parse, ConflictSide.Theirs, spans);
            var analyses = ConflictRegionAnalyzer.AnalyzeAll(parse).Concat(managedAnalyses).ToList();
            var snapshot = new ConflictParseSnapshot(parse, oursLines, theirsLines, analyses, versionSnapshot);

            Dispatcher.UIThread.Post(() =>
            {
                if (ct.IsCancellationRequested || _disposed)
                {
                    return;
                }

                if (_resultEditor.Document.Version.CompareAge(versionSnapshot) == 0)
                {
                    _snapshot = snapshot;
                    _parse = parse;
                    _parseVersion = versionSnapshot;

                    ConflictPaneUpdateHelper.UpdateDocumentIncremental(_oursEditor.Document, oursLines);
                    ConflictPaneUpdateHelper.UpdateDocumentIncremental(_theirsEditor.Document, theirsLines);

                    _session.SetMarkerRegions(parse.Regions);
                    _minimap.Parse = parse;
                    _minimap.ResolvedRanges = ManagedRanges();
                    UpdateConflictsCount();
                    _oursEditor.TextArea.TextView.Redraw();
                    _theirsEditor.TextArea.TextView.Redraw();
                    _resultEditor.TextArea.TextView.Redraw();
                }
            });
        }, TaskScheduler.Default);
    }

    private void UpdateConflictParseSync()
    {
        int markerSize = _workingCopy?.MarkerSize ?? 7;
        var text = _resultEditor.Text;
        _parse = ConflictMarkerParser.Parse(text, markerSize);
        var parse = _parse;
        var spans = _session.Spans;
        var oursLines = ConflictPaneBuilder.Build(parse, ConflictSide.Ours, spans);
        var theirsLines = ConflictPaneBuilder.Build(parse, ConflictSide.Theirs, spans);
        var version = _resultEditor.Document.Version;
        _parseVersion = version;
        _session.SetMarkerRegions(parse.Regions);
        var managedAnalyses = ManagedAnalyses();

        // No-blink carryover: retain analyses for regions whose text is unchanged
        var prevAnalyses = _snapshot?.Analyses;
        var carriedAnalyses = new List<ConflictRegionAnalysis>();
        if (prevAnalyses != null && prevAnalyses.Count > 0)
        {
            foreach (var r in parse.Regions)
            {
                var match = prevAnalyses.FirstOrDefault(a => RegionLinesEqual(r, a.Region));
                if (match != null)
                {
                    carriedAnalyses.Add(RemapAnalysis(match, r));
                }
            }
        }

        carriedAnalyses.AddRange(managedAnalyses);
        _snapshot = new ConflictParseSnapshot(parse, oursLines, theirsLines, carriedAnalyses, version);
        _minimap.Parse = parse;
        _minimap.ResolvedRanges = ManagedRanges();
        UpdateConflictsCount();

        Task.Run(() =>
        {
            if (_disposed)
            {
                return;
            }

            var fullAnalyses = ConflictRegionAnalyzer.AnalyzeAll(parse).Concat(managedAnalyses).ToList();
            Dispatcher.UIThread.Post(() =>
            {
                if (_disposed)
                {
                    return;
                }

                if (_resultEditor.Document.Version.CompareAge(version) == 0)
                {
                    _snapshot = new ConflictParseSnapshot(parse, oursLines, theirsLines, fullAnalyses, version);
                    _oursEditor.TextArea.TextView.Redraw();
                    _theirsEditor.TextArea.TextView.Redraw();
                    _resultEditor.TextArea.TextView.Redraw();
                }
            });
        });
    }

    private static bool RegionLinesEqual(ConflictRegion a, ConflictRegion b)
    {
        if (a.OursLines.Count != b.OursLines.Count || a.TheirsLines.Count != b.TheirsLines.Count)
            return false;
        for (int i = 0; i < a.OursLines.Count; i++)
            if (a.OursLines[i] != b.OursLines[i]) return false;
        for (int i = 0; i < a.TheirsLines.Count; i++)
            if (a.TheirsLines[i] != b.TheirsLines[i]) return false;

        if (a.HasBase != b.HasBase) return false;
        if (a.HasBase)
        {
            if (a.BaseLines == null || b.BaseLines == null || a.BaseLines.Count != b.BaseLines.Count)
                return false;
            for (int i = 0; i < a.BaseLines.Count; i++)
                if (a.BaseLines[i] != b.BaseLines[i]) return false;
        }

        return true;
    }

    private static ConflictRegionAnalysis RemapAnalysis(ConflictRegionAnalysis prev, ConflictRegion newRegion)
    {
        if (prev.Region.OursStartLine == newRegion.OursStartLine &&
            prev.Region.TheirsStartLine == newRegion.TheirsStartLine &&
            prev.Region.BaseStartLine == newRegion.BaseStartLine)
        {
            return new ConflictRegionAnalysis(newRegion, prev.OursHighlights, prev.TheirsHighlights, prev.BaseHighlights);
        }

        var oursHl = new List<RegionLineHighlight>(prev.OursHighlights.Count);
        for (int i = 0; i < prev.OursHighlights.Count; i++)
        {
            var h = prev.OursHighlights[i];
            oursHl.Add(new RegionLineHighlight(h.LineIndex, newRegion.OursStartLine + i, h.Spans, h.ChangedFromBase));
        }

        var theirsHl = new List<RegionLineHighlight>(prev.TheirsHighlights.Count);
        for (int i = 0; i < prev.TheirsHighlights.Count; i++)
        {
            var h = prev.TheirsHighlights[i];
            theirsHl.Add(new RegionLineHighlight(h.LineIndex, newRegion.TheirsStartLine + i, h.Spans, h.ChangedFromBase));
        }

        List<RegionLineHighlight>? baseHl = null;
        if (prev.BaseHighlights != null)
        {
            baseHl = new List<RegionLineHighlight>(prev.BaseHighlights.Count);
            for (int i = 0; i < prev.BaseHighlights.Count; i++)
            {
                var h = prev.BaseHighlights[i];
                baseHl.Add(new RegionLineHighlight(h.LineIndex, newRegion.BaseStartLine + i, h.Spans, h.ChangedFromBase));
            }
        }

        return new ConflictRegionAnalysis(newRegion, oursHl, theirsHl, baseHl);
    }

    private void UpdateEditorsFromParse()
    {
        if (_snapshot == null)
        {
            return;
        }

        _oursEditor.Text = string.Join('\n', _snapshot.OursLines.Select(l => l.Text));
        _theirsEditor.Text = string.Join('\n', _snapshot.TheirsLines.Select(l => l.Text));

        _oursEditor.TextArea.TextView.Redraw();
        _theirsEditor.TextArea.TextView.Redraw();
        _resultEditor.TextArea.TextView.Redraw();
    }

    private IReadOnlyList<ConflictPaneLine>? GetPaneLines(ConflictSide side)
    {
        return side == ConflictSide.Ours ? _snapshot?.OursLines : _snapshot?.TheirsLines;
    }

    /// <summary>The Result line of a side's row in a top pane; -1 for a marker, padding or common
    /// row, which carry no hunk box.</summary>
    private int PaneDocLine(ConflictSide side, int paneRow)
    {
        var lines = GetPaneLines(side);
        if (lines == null || paneRow < 0 || paneRow >= lines.Count)
        {
            return -1;
        }

        var line = lines[paneRow];
        return line.Type is ConflictPaneLineType.Ours or ConflictPaneLineType.Theirs ? line.DocumentLineIndex : -1;
    }

    private ConflictToggleGlyph PaneHunkGlyph(ConflictSide side, int paneRow)
        => _session.HunkGlyph(side, PaneDocLine(side, paneRow));

    private ConflictRowTint PaneTint(ConflictSide side, int row)
    {
        var lines = GetPaneLines(side);
        return lines != null && row >= 0 && row < lines.Count ? ConflictRowTints.ForPane(lines[row].Type) : ConflictRowTint.None;
    }

    private ConflictRowTint ResultTint(int row) => ConflictRowTints.ForResult(row, _session.RowVisual(row), _parse);

    /// <summary>The conflicts the arrows and the counter step through — unresolved marker regions
    /// and managed regions — by document line, in order.</summary>
    internal IReadOnlyList<(int Start, int End)> NavigableRegions()
    {
        var regions = new List<(int Start, int End)>();
        if (_parse != null)
        {
            foreach (var r in _parse.Regions)
            {
                regions.Add((r.StartLine, r.EndLine));
            }
        }
        foreach (var r in _session.Regions)
        {
            if (!r.IsBroken)
            {
                regions.Add((r.StartLine, r.EndLine));
            }
        }
        regions.Sort((a, b) => a.Start.CompareTo(b.Start));
        return regions;
    }

    /// <summary>
    /// The 0-based index of the conflict the caret is in; otherwise of the next one below the
    /// caret; the last one when the caret is below them all. -1 when there are none.
    /// </summary>
    internal int CurrentRegionIndex() => CurrentRegionIndex(NavigableRegions());

    private int CurrentRegionIndex(IReadOnlyList<(int Start, int End)> regions)
    {
        if (regions.Count == 0)
        {
            return -1;
        }

        int caretLine = _resultEditor.TextArea.Caret.Line - 1;
        for (int i = 0; i < regions.Count; i++)
        {
            if (caretLine <= regions[i].End)
            {
                return i;
            }
        }
        return regions.Count - 1;
    }

    private void UpdateConflictsCount()
    {
        var regions = NavigableRegions();
        int total = regions.Count;
        int malformed = _parse?.MalformedAt.Count ?? 0;
        int current = CurrentRegionIndex(regions) + 1;
        int unresolved = _parse?.Regions.Count ?? 0;

        _conflictsCountText.IsVisible = total > 0;
        _conflictsCountText.Text = total > 0 ? $"{current}/{total}" : string.Empty;
        ToolTip.SetTip(_conflictsCountText, total > 0 ? $"Conflict {current} of {total} in this file · {unresolved} unresolved" : null);
        bool canNavigate = !_isBusy && _isEditable && total > 0;
        _prevConflictBtn.IsEnabled = canNavigate;
        _nextConflictBtn.IsEnabled = canNavigate;

        if (malformed > 0)
        {
            var firstLine = _parse!.MalformedAt[0] + 1;
            _damagedMarkersHint.Text = $"Conflict markers damaged near line {firstLine}";
            _damagedMarkersHint.IsVisible = true;
        }
        else
        {
            _damagedMarkersHint.IsVisible = false;
        }
    }

    private void OnResultCaretPositionChanged(object? sender, EventArgs e)
    {
        if (_isEditable)
        {
            UpdateConflictsCount();
        }
    }

    private void SyncScroll(ConflictPaneEditor source, Vector offset)
    {
        if (_isSyncingScroll)
        {
            return;
        }

        _isSyncingScroll = true;
        try
        {
            if (!ReferenceEquals(source, _oursEditor))
            {
                _oursEditor.SetScrollOffset(offset);
            }
            if (!ReferenceEquals(source, _theirsEditor))
            {
                _theirsEditor.SetScrollOffset(offset);
            }
            if (!ReferenceEquals(source, _resultEditor))
            {
                _resultEditor.SetScrollOffset(offset);
            }

            if (_resultEditor.TextArea.TextView is { VisualLinesValid: true } tv && tv.VisualLines.Count > 0)
            {
                var first = tv.VisualLines[0].FirstDocumentLine.LineNumber;
                var last = tv.VisualLines[^1].LastDocumentLine.LineNumber;
                _minimap.DisplayRange = (first, last);
            }
            else
            {
                int line = _resultEditor.TextArea.Caret.Line;
                _minimap.DisplayRange = (line, line + 20);
            }
        }
        finally
        {
            _isSyncingScroll = false;
        }
    }

    public void GoToNextConflict()
    {
        var regions = NavigableRegions();
        if (regions.Count == 0)
        {
            return;
        }

        int currentLine = _resultEditor.TextArea.Caret.Line - 1;
        var next = regions.FirstOrDefault(r => r.Start > currentLine, regions[0]);
        _resultEditor.ScrollToLine(next.Start + 1);
        _resultEditor.TextArea.Caret.Line = next.Start + 1;
    }

    public void GoToPreviousConflict()
    {
        var regions = NavigableRegions();
        if (regions.Count == 0)
        {
            return;
        }

        int currentLine = _resultEditor.TextArea.Caret.Line - 1;
        var prev = regions.LastOrDefault(r => r.End < currentLine, regions[^1]);
        _resultEditor.ScrollToLine(prev.Start + 1);
        _resultEditor.TextArea.Caret.Line = prev.Start + 1;
    }

    public async Task<bool> SaveCurrentFileAsync(bool stage)
    {
        if (_isBusy || !_isEditable || _currentFile == null || _workingCopy == null || _workingCopy.Format is null)
        {
            return false;
        }

        var savedVersion = _resultEditor.Document.Version;
        var textToSave = _session.ComposeOutput();

        _saving = true;
        IsBusy = true;
        try
        {
            var res = await _conflicts.SaveResolutionAsync(
                _ctx,
                _currentFile.FilePath,
                textToSave,
                _workingCopy.Format.Value,
                _diskHash ?? string.Empty,
                stage,
                _viewCts.Token);

            if (!res.Saved)
            {
                _changedOnDiskBanner.IsVisible = true;
                return false;
            }

            _diskHash = res.DiskHash;
            _changedOnDiskBanner.IsVisible = false;

            if (res.StageError != null)
            {
                _notifications.Publish(NotificationSeverity.Error, $"Saved {_currentFile.FilePath}, but could not mark it resolved: {res.StageError.Message}", res.StageError.NotificationDetails());
                return false;
            }

            bool versionUnchanged = _resultEditor.Document.Version.CompareAge(savedVersion) == 0;
            if (versionUnchanged)
            {
                _resultEditor.Document.UndoStack.MarkAsOriginalFile();
                UnregisterUnsavedWork();
            }

            if (stage && versionUnchanged)
            {
                await RefreshConflictsAsync();
                if (_conflictedFiles.Count > 0)
                {
                    var next = _conflictedFiles.FirstOrDefault(f => !string.Equals(f.FilePath, _currentFile.FilePath, StringComparison.Ordinal))
                               ?? _conflictedFiles[0];
                    await SwitchToFileAsync(next);
                }
                else
                {
                    ShowAllConflictsResolvedCard();
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            _notifications.Publish(NotificationSeverity.Error, ex.NotificationMessage("Save"), ex.NotificationDetails());
            return false;
        }
        finally
        {
            _saving = false;
            IsBusy = false;
        }
    }

    public async Task SaveAndMarkResolvedAsync()
    {
        if (_isBusy)
        {
            return;
        }

        if (!await ConfirmMarkResolvedAsync())
        {
            return;
        }

        await SaveCurrentFileAsync(stage: true);
    }

    /// <summary>
    /// Before marking a file resolved: warn about conflict blocks still in it, and about managed
    /// conflicts that keep no line and will save as nothing. Neither is destructive — the text is
    /// what the user sees, and it stays editable after staging.
    /// </summary>
    private async Task<bool> ConfirmMarkResolvedAsync()
    {
        int remaining = (_parse?.Regions.Count ?? 0) + (_parse?.MalformedAt.Count ?? 0);
        int emptied = _session.EmptyRegionCount;
        if (remaining > 0)
        {
            string message = $"{remaining} conflict block(s) remain — mark resolved anyway?";
            if (emptied > 0)
            {
                message += $" {emptied} conflict(s) will save as no lines.";
            }
            return await _dialogs.ConfirmAsync("Unresolved conflicts", message, confirmLabel: "Mark resolved");
        }

        if (emptied > 0)
        {
            return await _dialogs.ConfirmAsync(
                "Emptied conflicts",
                $"{emptied} conflict(s) will save as no lines — mark resolved anyway?",
                confirmLabel: "Mark resolved");
        }

        return true;
    }

    private async Task TakeSideAsync(ConflictSide side)
    {
        if (_isBusy || _currentFile == null)
        {
            return;
        }

        var path = _currentFile.FilePath;
        IsBusy = true;
        try
        {
            var labels = await _conflicts.GetSideLabelsAsync(_ctx, _viewCts.Token);
            bool isAbsent = side == ConflictSide.Ours
                ? (_currentFile.Status is ConflictStatus.DeletedByUs or ConflictStatus.AddedByThem or ConflictStatus.BothDeletedRenamed)
                : (_currentFile.Status is ConflictStatus.DeletedByThem or ConflictStatus.AddedByUs or ConflictStatus.BothDeletedRenamed);
            var sideTitle = side == ConflictSide.Ours ? labels.OursTitle : labels.TheirsTitle;
            string title = isAbsent ? $"Delete {path}" : (side == ConflictSide.Ours ? "Take ours" : "Take theirs");
            string message = isAbsent
                ? $"Delete {path} — {sideTitle} deleted it. Any edits to this file will be lost."
                : $"Replace {path} with {sideTitle}'s version? Any edits to this file will be lost.";
            string verb = isAbsent ? "Delete" : (side == ConflictSide.Ours ? "Take ours" : "Take theirs");

            if (!await _dialogs.ConfirmDestructiveAsync(title, message, verb))
            {
                return;
            }

            await _conflicts.ResolveWithSideAsync(_ctx, path, side, _viewCts.Token);
            UnregisterUnsavedWork();
            await RefreshConflictsAsync();
            if (_conflictedFiles.Count > 0)
            {
                var next = _conflictedFiles.FirstOrDefault(f => !string.Equals(f.FilePath, path, StringComparison.Ordinal))
                           ?? _conflictedFiles[0];
                await SwitchToFileAsync(next);
            }
            else
            {
                ShowAllConflictsResolvedCard();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _notifications.Publish(NotificationSeverity.Error, ex.NotificationMessage($"Take {(side == ConflictSide.Ours ? "ours" : "theirs")}"), ex.NotificationDetails());
        }
        finally
        {
            IsBusy = false;
        }
    }

    public Task TakeOursCurrentAsync() => TakeSideAsync(ConflictSide.Ours);

    public Task TakeTheirsCurrentAsync() => TakeSideAsync(ConflictSide.Theirs);

    public async Task RecreateConflictAsync()
    {
        if (_isBusy || _currentFile == null)
        {
            return;
        }

        var path = _currentFile.FilePath;
        IsBusy = true;
        try
        {
            if (!await _dialogs.ConfirmDestructiveAsync(
                    "Reset file to conflicted",
                    $"Rewrite {path} back to its conflicted state, including the common-ancestor (base) section? Any edits to this file will be lost.",
                    "Reset"))
            {
                return;
            }

            await _conflicts.RecreateConflictAsync(_ctx, path, _viewCts.Token);
            UnregisterUnsavedWork();
            await RefreshConflictsAsync();
            var target = _conflictedFiles.FirstOrDefault(f => string.Equals(f.FilePath, path, StringComparison.Ordinal));
            if (target != null)
            {
                await SwitchToFileAsync(target);
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
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _notifications.Publish(NotificationSeverity.Error, ex.NotificationMessage("Reset file to conflicted"), ex.NotificationDetails());
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task LaunchExternalToolAsync()
    {
        if (_isBusy || _currentFile == null)
        {
            return;
        }

        try
        {
            if (HasUnsavedWork)
            {
                var work = _unsavedRegistry?.For(_ctx) ?? [new ConflictUnsavedWork(this, _ctx, _currentFile.FilePath)];
                var proceed = await UnsavedWorkPrompt.ResolveAsync(work, _dialogs);
                if (!proceed)
                {
                    return;
                }
            }

            IsBusy = true;
            try
            {
                var launched = await _conflicts.LaunchExternalMergeToolAsync(_ctx, _currentFile.FilePath, _viewCts.Token);
                if (!launched)
                {
                    var settings = _sp.GetService(typeof(ISettingsService)) as ISettingsService;
                    var tool = settings?.Current.ExternalMergeTool;
                    if (!string.IsNullOrWhiteSpace(tool))
                    {
                        _notifications.Publish(NotificationSeverity.Warning, $"The merge tool exited with conflict markers still in {_currentFile.FilePath}.");
                    }
                    else
                    {
                        _notifications.Publish(NotificationSeverity.Warning, "No external merge tool is configured (git config merge.tool).");
                    }
                    return;
                }

                UnregisterUnsavedWork();
                await RefreshConflictsAsync();
                var target = _conflictedFiles.FirstOrDefault(f => string.Equals(f.FilePath, _currentFile.FilePath, StringComparison.Ordinal));
                if (target != null)
                {
                    await SwitchToFileAsync(target);
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
            finally
            {
                IsBusy = false;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _notifications.Publish(NotificationSeverity.Error, ex.NotificationMessage("External tool"), ex.NotificationDetails());
        }
    }

    public async Task ContinueAsync()
    {
        if (_isBusy)
        {
            return;
        }

        try
        {
            if (HasUnsavedWork && _currentFile != null)
            {
                var choice = await UnsavedWorkPrompt.ResolveAsync(
                    _dialogs,
                    "Unsaved changes",
                    $"{_currentFile.FilePath} has unsaved edits. Save and mark it resolved before continuing?",
                    "Save & mark resolved",
                    "Discard changes");

                switch (choice)
                {
                    case UnsavedWorkChoice.Save:
                        if (!await ConfirmMarkResolvedAsync())
                        {
                            return;
                        }

                        var saved = await SaveCurrentFileAsync(stage: true);
                        if (!saved)
                        {
                            return;
                        }
                        break;

                    case UnsavedWorkChoice.Discard:
                        DiscardUnsavedState();
                        await SwitchToFileAsync(_currentFile);
                        break;

                    case UnsavedWorkChoice.Cancel:
                    default:
                        return;
                }
            }

            IsBusy = true;
            try
            {
                await _conflicts.ContinueOperationAsync(_ctx, _ctx.Lifetime);
                if (_ctx.State.PendingOperation == OperationState.None)
                {
                    _ctx.State.SetKeepConflictResolverOpen(false);
                }
            }
            finally
            {
                IsBusy = false;
            }
        }
        catch (OperationCanceledException) { }
        catch (HistoriaGitException ex) when (ex.Kind == GitErrorKind.MergeConflict)
        {
            _notifications.Publish(NotificationSeverity.Warning, ex.Message);
        }
        catch (Exception ex)
        {
            _notifications.Publish(NotificationSeverity.Error, ex.NotificationMessage("Continue"), ex.NotificationDetails());
        }
    }

    public async Task SkipAsync()
    {
        if (_isBusy)
        {
            return;
        }

        try
        {
            var labels = await _conflicts.GetSideLabelsAsync(_ctx, _viewCts.Token);
            var commitInfo = string.IsNullOrWhiteSpace(labels.TheirsDetail) ? "the current commit" : labels.TheirsDetail;
            if (!await _dialogs.ConfirmDestructiveAsync(
                    "Skip commit",
                    $"Skip {commitInfo}? Resolutions and unsaved edits in this stop will be discarded.",
                    "Skip"))
            {
                return;
            }

            IsBusy = true;
            try
            {
                await _conflicts.SkipCurrentAsync(_ctx, _ctx.Lifetime);
                DiscardUnsavedState();
                _changedOnDiskBanner.IsVisible = false;
                if (_ctx.State.PendingOperation == OperationState.None)
                {
                    _ctx.State.SetKeepConflictResolverOpen(false);
                }
            }
            finally
            {
                IsBusy = false;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _notifications.Publish(NotificationSeverity.Error, ex.NotificationMessage("Skip"), ex.NotificationDetails());
        }
    }

    public async Task AbortAsync()
    {
        if (_isBusy)
        {
            return;
        }

        try
        {
            if (!await _dialogs.ConfirmDestructiveAsync(
                    "Abort operation",
                    "Abort the in-progress merge / rebase / cherry-pick? Conflict resolutions and unsaved edits made so far will be discarded.",
                    "Abort"))
            {
                return;
            }

            IsBusy = true;
            try
            {
                await _conflicts.AbortOperationAsync(_ctx, _ctx.Lifetime);
                DiscardUnsavedState();
                _changedOnDiskBanner.IsVisible = false;
                if (_ctx.State.PendingOperation == OperationState.None)
                {
                    _ctx.State.SetKeepConflictResolverOpen(false);
                }
            }
            finally
            {
                IsBusy = false;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _notifications.Publish(NotificationSeverity.Error, ex.NotificationMessage("Abort"), ex.NotificationDetails());
        }
    }

    private async Task CheckExternalDiskChangeAsync()
    {
        if (_disposed || _saving || _currentFile == null || _diskHash == null)
        {
            return;
        }

        string currentHash;
        try
        {
            currentHash = await _conflicts.GetDiskHashAsync(_ctx, _currentFile.FilePath, _viewCts.Token);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unreadable right now (locked mid-save, scanned): the next status change checks again.
            return;
        }

        if (_disposed) return;
        if (currentHash != _diskHash)
        {
            if (!HasUnsavedWork)
            {
                await SwitchToFileAsync(_currentFile);
            }
            else
            {
                _changedOnDiskBanner.IsVisible = true;
            }
        }
    }

    private async Task ReloadFromDiskAsync()
    {
        if (_isBusy || _currentFile == null)
        {
            return;
        }

        try
        {
            if (!await _dialogs.ConfirmDestructiveAsync(
                    "Reload from disk",
                    $"Reload {_currentFile.FilePath} from disk? Your unsaved edits will be discarded.",
                    "Reload"))
            {
                return;
            }

            _changedOnDiskBanner.IsVisible = false;
            UnregisterUnsavedWork();
            await SwitchToFileAsync(_currentFile);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _notifications.Publish(NotificationSeverity.Error, ex.NotificationMessage("Reload"), ex.NotificationDetails());
        }
    }

    internal async Task KeepMineAsync()
    {
        if (_isBusy || _currentFile == null)
        {
            return;
        }

        var path = _currentFile.FilePath;
        try
        {
            if (!await _dialogs.ConfirmDestructiveAsync(
                    "Keep my version",
                    $"Your next save will overwrite {path} on disk, which changed outside Historia.",
                    "Keep mine"))
            {
                return;
            }

            _changedOnDiskBanner.IsVisible = false;
            IsBusy = true;
            try
            {
                var copy = await _conflicts.LoadWorkingCopyAsync(_ctx, path, _ctx.Lifetime);
                _diskHash = copy.DiskHash;
            }
            finally
            {
                IsBusy = false;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _notifications.Publish(NotificationSeverity.Error, ex.NotificationMessage("Keep mine"), ex.NotificationDetails());
        }
    }

    private void RegisterUnsavedWork()
    {
        if (_unsavedRegistration != null || _unsavedRegistry == null || _currentFile == null)
        {
            return;
        }

        _unsavedRegistration = _unsavedRegistry.Register(new ConflictUnsavedWork(this, _ctx, _currentFile.FilePath));
    }

    private void UnregisterUnsavedWork()
    {
        _unsavedRegistration?.Dispose();
        _unsavedRegistration = null;
    }

    private void DiscardUnsavedState()
    {
        UnregisterUnsavedWork();
        _resultEditor.Document.UndoStack.MarkAsOriginalFile();
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
        _viewCts.Cancel();
        _switchCts?.Cancel();
        _switchCts?.Dispose();
        _debounceCts?.Cancel();
        _debounceCts?.Dispose();
        UnregisterUnsavedWork();
    }
}
