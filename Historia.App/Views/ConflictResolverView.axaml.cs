namespace Historia.Views;

/// <summary>
/// Native 3-way conflict resolver view with aligned Ours | Theirs panes and editable Result editor.
/// The toolbar, banners and cards are markup bound to a <see cref="ConflictResolverViewModel"/>; the
/// code here owns the three editors, their margins, the minimap and the selection session, and
/// hands them to the view model as an <see cref="IConflictEditorSurface"/>.
/// Adapted from SourceGit (MIT) — see docs/third-party-licenses.md.
/// </summary>
public sealed partial class ConflictResolverView : UserControl, IDisposable, IConflictEditorSurface
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

    private CancellationTokenSource? _debounceCts;
    private ConflictParseSnapshot? _snapshot;
    private ConflictParse? _parse;
    private ITextSourceVersion? _parseVersion;
    private bool _isSyncingScroll;
    private bool _suppressTextChanged;
    private bool _inResultTextChanged;
    private bool _swallowNextSpace;

    private readonly ConflictSelectionSession _session;
    private readonly ConflictToggleMargin _oursToggleMargin;
    private readonly ConflictToggleMargin _theirsToggleMargin;
    private readonly ConflictToggleMargin _resultToggleMargin;
    private readonly ConflictLineNumberMargin _resultNumberMargin;
    private readonly ConflictExcludedLineColorizer _excludedColorizer;

    public ConflictResolverView(IServiceProvider sp, RepositoryContext ctx)
    {
        InitializeComponent();
        ViewModel = ActivatorUtilities.CreateInstance<ConflictResolverViewModel>(sp, ctx, new UiDispatcher());
        DataContext = ViewModel;
        ViewModel.BusyChanged += OnViewModelBusyChanged;

        OursEditor.ServicesProvider = sp;
        TheirsEditor.ServicesProvider = sp;
        ResultEditor.ServicesProvider = sp;

        // Managed conflicts: kept / struck rows over the Result document
        _session = new ConflictSelectionSession(ResultEditor.Document);
        _session.Changed += OnSessionChanged;

        // Margins and Renderers
        _oursToggleMargin = new ConflictToggleMargin(row => PaneHunkGlyph(ConflictSide.Ours, row), row => PaneHunkTip(ConflictSide.Ours, row));
        _oursToggleMargin.ToggleClicked += (_, row) => OnHunkToggleClicked(ConflictSide.Ours, row);
        OursEditor.TextArea.LeftMargins.Add(_oursToggleMargin);
        OursEditor.TextArea.LeftMargins.Add(new ConflictLineNumberMargin(OursEditor, () => GetPaneLines(ConflictSide.Ours), row => PaneTint(ConflictSide.Ours, row)));
        OursEditor.TextArea.TextView.BackgroundRenderers.Add(
            new ConflictBackgroundRenderer(() => ConflictSide.Ours, () => _parse, () => GetPaneLines(ConflictSide.Ours), () => _snapshot?.Analyses));

        _theirsToggleMargin = new ConflictToggleMargin(row => PaneHunkGlyph(ConflictSide.Theirs, row), row => PaneHunkTip(ConflictSide.Theirs, row));
        _theirsToggleMargin.ToggleClicked += (_, row) => OnHunkToggleClicked(ConflictSide.Theirs, row);
        TheirsEditor.TextArea.LeftMargins.Add(_theirsToggleMargin);
        TheirsEditor.TextArea.LeftMargins.Add(new ConflictLineNumberMargin(TheirsEditor, () => GetPaneLines(ConflictSide.Theirs), row => PaneTint(ConflictSide.Theirs, row)));
        TheirsEditor.TextArea.TextView.BackgroundRenderers.Add(
            new ConflictBackgroundRenderer(() => ConflictSide.Theirs, () => _parse, () => GetPaneLines(ConflictSide.Theirs), () => _snapshot?.Analyses));

        _resultToggleMargin = new ConflictToggleMargin(_session.RowGlyph, line => _session.IsDeletionRow(line)
            ? (_session.IsExcluded(line) ? "Mark as deleted" : "Unmark the deletion")
            : (_session.IsExcluded(line) ? "Keep this line" : "Drop this line"));
        _resultToggleMargin.ToggleClicked += (_, line) => OnRowToggleClicked(line);
        ResultEditor.TextArea.LeftMargins.Add(_resultToggleMargin);
        _resultNumberMargin = new ConflictLineNumberMargin(ResultEditor, getTint: ResultTint);
        ResultEditor.TextArea.LeftMargins.Add(_resultNumberMargin);
        ResultEditor.TextArea.TextView.BackgroundRenderers.Add(
            new ConflictBackgroundRenderer(() => null, () => _parse, getRowVisual: _session.RowVisual));
        _excludedColorizer = new ConflictExcludedLineColorizer(_session.IsExcluded);
        ResultEditor.TextArea.TextView.LineTransformers.Add(_excludedColorizer);
        ResultEditor.AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(EnsureExcludedColorizerLast);

        // Shared scroll synchronization
        OursEditor.ScrollOffsetChanged += (_, offset) => SyncScroll(OursEditor, offset);
        TheirsEditor.ScrollOffsetChanged += (_, offset) => SyncScroll(TheirsEditor, offset);
        ResultEditor.ScrollOffsetChanged += (_, offset) => SyncScroll(ResultEditor, offset);

        Minimap.LineNavigationRequested += (_, line) =>
        {
            ResultEditor.ScrollToLine(line);
            OursEditor.ScrollToLine(line);
            TheirsEditor.ScrollToLine(line);
        };

        // Wire document changes and caret changes
        ResultEditor.Document.TextChanged += OnResultDocumentTextChanged;
        ResultEditor.TextArea.Caret.PositionChanged += OnResultCaretPositionChanged;

        // The flyout's items sit outside the view's tree until it opens, so their commands are set here.
        var resolveFileItems = ((MenuFlyout)ResolveFileButton.Flyout!).Items.OfType<MenuItem>().ToList();
        resolveFileItems[0].Command = ViewModel.TakeOursCurrentCommand;
        resolveFileItems[1].Command = ViewModel.TakeTheirsCurrentCommand;
        resolveFileItems[2].Command = ViewModel.ResetFileCommand;
        resolveFileItems[3].Command = ViewModel.LaunchExternalToolCommand;

        AddHandler(KeyDownEvent, OnViewKeyDown, RoutingStrategies.Tunnel);
        AddHandler(TextInputEvent, OnViewTextInput, RoutingStrategies.Tunnel);
        AddHandler(PointerPressedEvent, OnViewPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);

        ViewModel.Attach(this);
    }

    internal ConflictResolverViewModel ViewModel { get; }

    public bool IsBusy => ViewModel.IsBusy;

    public bool HasUnsavedWork => ViewModel.HasUnsavedWork;

    public event EventHandler? BusyChanged;

    private void OnViewModelBusyChanged(object? sender, EventArgs e) => BusyChanged?.Invoke(this, e);

    internal ConflictPaneEditor ResultEditorForTesting => ResultEditor;
    internal ConflictPaneEditor OursEditorForTesting => OursEditor;
    internal ConflictPaneEditor TheirsEditorForTesting => TheirsEditor;
    internal ConflictResolverMinimap MinimapForTesting => Minimap;
    internal Button ContinueButtonForTesting => ContinueButton;
    internal Button SkipButtonForTesting => SkipButton;
    internal StackPanel OpBarForTesting => OpBar;
    internal Button SaveAndResolveButtonForTesting => SaveAndResolveButton;
    internal Button ResolveFileButtonForTesting => ResolveFileButton;
    internal IReadOnlyList<MenuItem> ResolveFileMenuItemsForTesting => ((MenuFlyout)ResolveFileButton.Flyout!).Items.OfType<MenuItem>().ToList();
    internal Button PrevConflictButtonForTesting => PrevConflictButton;
    internal Button NextConflictButtonForTesting => NextConflictButton;
    internal TextBlock OursCaptionForTesting => OursCaptionText;
    internal TextBlock TheirsCaptionForTesting => TheirsCaptionText;
    internal IReadOnlyList<Button> ActionButtonsForTesting => [ContinueButton, SkipButton, AbortButton, ReloadButton, KeepMineButton];
    internal IReadOnlyList<Button> TextActionButtonsForTesting => [SaveAndResolveButton, PrevConflictButton, NextConflictButton];
    internal TextBlock ConflictsCountTextForTesting => ConflictsCountLabel;
    internal TextBlock DamagedMarkersHintForTesting => DamagedMarkersHint;
    internal Border ExternalEndedBannerForTesting => ExternalEndedBanner;
    internal Border ChangedOnDiskBannerForTesting => ChangedOnDiskBanner;
    internal StackPanel AllResolvedCardForTesting => AllResolvedCard;
    internal StackPanel FileCardForTesting => FileCard;
    internal ConflictParse? CurrentParseForTesting => _parse;
    internal ConflictParseSnapshot? SnapshotForTesting => _snapshot;

    /// <summary>Raised on the UI thread each time a parse snapshot is installed.</summary>
    internal event Action<ConflictParseSnapshot>? SnapshotInstalledForTesting;

    private void InstallSnapshot(ConflictParseSnapshot snapshot)
    {
        _snapshot = snapshot;
        SnapshotInstalledForTesting?.Invoke(snapshot);
    }

    internal bool IsEditableForTesting => ViewModel.IsEditable;
    internal string? DiskHashForTesting => ViewModel.DiskHash;
    internal ConflictFile? CurrentFileForTesting => ViewModel.CurrentFile;
    internal IReadOnlyList<ConflictFile> ConflictedFilesForTesting => ViewModel.ConflictedFiles;
    internal ConflictSelectionSession SessionForTesting => _session;
    internal ConflictToggleMargin OursToggleMarginForTesting => _oursToggleMargin;
    internal ConflictToggleMargin TheirsToggleMarginForTesting => _theirsToggleMargin;
    internal ConflictToggleMargin ResultToggleMarginForTesting => _resultToggleMargin;
    internal string ComposedTextForTesting => _session.ComposeOutput();
    internal Rectangle PaneDividerForTesting => PaneDivider;
    internal Rectangle ResultDividerForTesting => ResultDivider;
    internal GridSplitter ResultSplitterForTesting => ResultSplitter;
    internal Grid SplitGridForTesting => SplitGrid;
    internal Button CloseButtonForTesting => CloseButton;
    internal ConflictLineNumberMargin ResultNumberMarginForTesting => _resultNumberMargin;

    /// <summary>Clicks the hunk check box on <paramref name="paneRow"/> of the Ours or Theirs pane.</summary>
    internal void ClickHunkForTesting(ConflictSide side, int paneRow) => OnHunkToggleClicked(side, paneRow);

    /// <summary>The hunk box drawn on <paramref name="paneRow"/> of the Ours or Theirs pane.</summary>
    internal ConflictToggleGlyph HunkGlyphForTesting(ConflictSide side, int paneRow) => PaneHunkGlyph(side, paneRow);

    public Task LoadAsync() => ViewModel.LoadAsync();

    public Task<bool> SaveCurrentFileAsync(bool stage) => ViewModel.SaveCurrentFileAsync(stage);

    public Task SaveAndMarkResolvedAsync() => ViewModel.SaveAndMarkResolvedAsync();

    public Task TakeOursCurrentAsync() => ViewModel.TakeOursCurrentAsync();

    public Task TakeTheirsCurrentAsync() => ViewModel.TakeTheirsCurrentAsync();

    public Task RecreateConflictAsync() => ViewModel.ResetFileAsync();

    public Task LaunchExternalToolAsync() => ViewModel.LaunchExternalToolAsync();

    public Task ContinueAsync() => ViewModel.ContinueAsync();

    public Task SkipAsync() => ViewModel.SkipAsync();

    public Task AbortAsync() => ViewModel.AbortAsync();

    internal Task KeepMineAsync() => ViewModel.KeepMineAsync();

    public void GoToNextConflict()
    {
        var regions = NavigableRegions();
        if (regions.Count == 0)
        {
            return;
        }

        int currentLine = ResultEditor.TextArea.Caret.Line - 1;
        var next = regions.FirstOrDefault(r => r.Start > currentLine, regions[0]);
        ResultEditor.ScrollToLine(next.Start + 1);
        ResultEditor.TextArea.Caret.Line = next.Start + 1;
    }

    public void GoToPreviousConflict()
    {
        var regions = NavigableRegions();
        if (regions.Count == 0)
        {
            return;
        }

        int currentLine = ResultEditor.TextArea.Caret.Line - 1;
        var prev = regions.LastOrDefault(r => r.End < currentLine, regions[^1]);
        ResultEditor.ScrollToLine(prev.Start + 1);
        ResultEditor.TextArea.Caret.Line = prev.Start + 1;
    }

    string IConflictEditorSurface.ComposeOutput() => _session.ComposeOutput();

    object IConflictEditorSurface.CaptureRevision() => ResultEditor.Document.Version;

    bool IConflictEditorSurface.IsRevisionCurrent(object revision)
        => ResultEditor.Document.Version.CompareAge((ITextSourceVersion)revision) == 0;

    void IConflictEditorSurface.MarkClean() => ResultEditor.Document.UndoStack.MarkAsOriginalFile();

    ConflictDocumentSummary IConflictEditorSurface.Summarize()
        => new((_parse?.Regions.Count ?? 0) + (_parse?.MalformedAt.Count ?? 0), _session.EmptyRegionCount);

    void IConflictEditorSurface.LoadDocument(string filePath, string text)
    {
        _snapshot = null;
        OursEditor.FilePath = filePath;
        TheirsEditor.FilePath = filePath;
        ResultEditor.FilePath = filePath;
        EnsureExcludedColorizerLast();

        _suppressTextChanged = true;
        try
        {
            _session.Clear();
            ResultEditor.Text = text;
        }
        finally
        {
            _suppressTextChanged = false;
        }
        ResultEditor.Document.UndoStack.MarkAsOriginalFile();
        _parseVersion = ResultEditor.Document.Version;
    }

    void IConflictEditorSurface.EnterEditMode()
    {
        ResultEditor.IsReadOnly = false;
        // Setting IsReadOnly replaces the read-only provider; struck rows need the session's.
        ResultEditor.TextArea.ReadOnlySectionProvider = _session.ReadOnlySections;
        UpdateConflictParseSync();
        UpdateEditorsFromParse();
    }

    void IConflictEditorSurface.LeaveEditMode()
    {
        _debounceCts?.Cancel();
        _parse = null;
        _parseVersion = null;
        _snapshot = null;
        Minimap.Parse = null;
        Minimap.ResolvedRanges = null;
    }

    void IConflictEditorSurface.ClearDocument()
    {
        ((IConflictEditorSurface)this).LeaveEditMode();
        _suppressTextChanged = true;
        try
        {
            _session.Clear();
            ResultEditor.Text = string.Empty;
        }
        finally
        {
            _suppressTextChanged = false;
        }
        OursEditor.Text = string.Empty;
        TheirsEditor.Text = string.Empty;
    }

    /// <summary>Ctrl+S saves the file without marking it resolved; Ctrl+Space, pressed in the
    /// Result, keeps or drops the managed row under its caret.</summary>
    private void OnViewKeyDown(object? sender, KeyEventArgs e)
    {
        _swallowNextSpace = false;
        if (e.Key == Key.S && e.KeyModifiers == KeyModifiers.Control)
        {
            if (ViewModel.IsEditable && !ViewModel.IsBusy)
            {
                _ = ViewModel.SaveCurrentFileAsync(stage: false);
            }
            e.Handled = true;
        }
        else if (e.Key == Key.Space && e.KeyModifiers == KeyModifiers.Control)
        {
            if (ViewModel.IsEditable && !ViewModel.IsBusy
                && e.Source is Visual source && ResultEditor.IsVisualAncestorOf(source)
                && _session.ToggleRow(ResultEditor.TextArea.Caret.Line - 1))
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

    private void OnViewPointerPressed(object? sender, PointerPressedEventArgs e) => ViewModel.ShowChangesPanel();

    private void OnHunkToggleClicked(ConflictSide side, int paneRow)
    {
        int line = PaneDocLine(side, paneRow);
        if (line < 0 || ViewModel.IsBusy || !ViewModel.IsEditable)
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
        if (ViewModel.IsBusy || !ViewModel.IsEditable)
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
        if (_suppressTextChanged || _inResultTextChanged || ViewModel.CurrentFile == null || !ViewModel.IsEditable)
        {
            return;
        }

        ViewModel.RegisterUnsavedWork();
        if (_parseVersion == null || ResultEditor.Document.Version.CompareAge(_parseVersion) != 0)
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
        Minimap.ResolvedRanges = ManagedRanges();
        UpdateConflictsCount();
        OursEditor.TextArea.TextView.Redraw();
        TheirsEditor.TextArea.TextView.Redraw();
        ResultEditor.TextArea.TextView.Redraw();
        _resultNumberMargin.InvalidateVisual();
    }

    private (int Start, int End)[] ManagedRanges()
        => _session.Regions.Where(r => !r.IsBroken).Select(r => (r.StartLine, r.EndLine)).ToArray();

    /// <summary>Syntax colouring is attached after the editor joins the tree and again on every
    /// file change; the struck-line colorizer must run after it to win the foreground.</summary>
    private void EnsureExcludedColorizerLast()
    {
        var transformers = ResultEditor.TextArea.TextView.LineTransformers;
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
        if (_parseVersion == null || ResultEditor.Document.Version.CompareAge(_parseVersion) != 0)
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

    public void Dispose()
    {
        if (ViewModel.IsDisposed)
        {
            return;
        }

        ViewModel.Dispose();
        _debounceCts?.Cancel();
        _debounceCts?.Dispose();
    }
}
