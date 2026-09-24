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
            Description = $"Unsaved conflict resolution in {path}";
        }

        public Task<bool> SaveAsync() => _owner.SaveCurrentFileAsync(stage: false);
        public void Discard() => _owner.DiscardUnsavedState();
    }

    public enum RegionActionChoice { Ours, Theirs, BothOursFirst, BothTheirsFirst, Base }

    private readonly IServiceProvider _sp;
    private readonly RepositoryContext _ctx;
    private readonly IConflictService _conflicts;
    private readonly IDialogService _dialogs;
    private readonly INotificationService _notifications;
    private readonly IUnsavedWorkRegistry? _unsavedRegistry;

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

    // Controls
    private readonly TextBlock _headerTitle = Text("", "ColorText", 13, FontWeight.SemiBold);
    private readonly TextBlock _headerOurs = Text("", "ColorTextMuted", 11);
    private readonly TextBlock _headerTheirs = Text("", "ColorTextMuted", 11);

    private readonly StackPanel _opBar = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
    private readonly Button _continueBtn;
    private readonly Button _skipBtn;
    private readonly Button _abortBtn;

    private readonly Border _externalEndedBanner = new();
    private readonly Border _changedOnDiskBanner = new();
    private readonly TextBlock _damagedMarkersHint = Text("", "ColorWarning", 11);

    private readonly Button _saveBtn;
    private readonly Button _saveAndResolveBtn;
    private readonly TextBlock _conflictsCountText = Text("", "ColorTextMuted", 11);

    private readonly ConflictPaneEditor _oursEditor = new() { IsReadOnly = true };
    private readonly ConflictPaneEditor _theirsEditor = new() { IsReadOnly = true };
    private readonly ConflictPaneEditor _resultEditor = new() { IsReadOnly = false };
    private readonly ConflictResolverMinimap _minimap = new();
    private readonly ContentControl _editorArea = new();
    private readonly Panel _editorOverlay = new();

    private readonly StackPanel _floatingRegionStrip = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
    private readonly Border _floatingRegionBox;

    private readonly List<Button> _actionButtons = [];
    private readonly List<Button> _dynamicActionButtons = [];

    private Button ActionButton(string text, Action onClick, bool primary = false)
    {
        var btn = TextButton(text, onClick, primary: primary);
        btn.IsEnabled = !_isBusy;
        _actionButtons.Add(btn);
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
    internal Button SaveButtonForTesting => _saveBtn;
    internal Button SaveAndResolveButtonForTesting => _saveAndResolveBtn;
    internal IReadOnlyList<Button> ActionButtonsForTesting => _actionButtons;
    internal TextBlock ConflictsCountTextForTesting => _conflictsCountText;
    internal TextBlock DamagedMarkersHintForTesting => _damagedMarkersHint;
    internal Border ExternalEndedBannerForTesting => _externalEndedBanner;
    internal Border ChangedOnDiskBannerForTesting => _changedOnDiskBanner;
    internal ConflictParse? CurrentParseForTesting => _parse;
    internal ConflictFile? CurrentFileForTesting => _currentFile;
    internal IReadOnlyList<ConflictFile> ConflictedFilesForTesting => _conflictedFiles;
    internal ContentControl EditorAreaForTesting => _editorArea;

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

        // Margins and Renderers
        _oursEditor.TextArea.LeftMargins.Add(new ConflictLineNumberMargin(_oursEditor, () => GetPaneLines(ConflictSide.Ours)));
        _oursEditor.TextArea.TextView.BackgroundRenderers.Add(
            new ConflictBackgroundRenderer(_oursEditor, () => ConflictSide.Ours, () => _parse, () => GetPaneLines(ConflictSide.Ours), () => _snapshot?.Analyses));

        _theirsEditor.TextArea.LeftMargins.Add(new ConflictLineNumberMargin(_theirsEditor, () => GetPaneLines(ConflictSide.Theirs)));
        _theirsEditor.TextArea.TextView.BackgroundRenderers.Add(
            new ConflictBackgroundRenderer(_theirsEditor, () => ConflictSide.Theirs, () => _parse, () => GetPaneLines(ConflictSide.Theirs), () => _snapshot?.Analyses));

        _resultEditor.TextArea.LeftMargins.Add(new ConflictLineNumberMargin(_resultEditor));
        _resultEditor.TextArea.TextView.BackgroundRenderers.Add(
            new ConflictBackgroundRenderer(_resultEditor, () => null, () => _parse));

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

        // Operation bar
        _continueBtn = ActionButton("Continue", () => _ = ContinueAsync(), primary: true);
        _skipBtn = ActionButton("Skip", () => _ = SkipAsync());
        _abortBtn = ActionButton("Abort", () => _ = AbortAsync());

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

        // Toolbar
        _saveBtn = ActionButton("Save", () => _ = SaveCurrentFileAsync(stage: false));
        _saveAndResolveBtn = ActionButton("Save & mark resolved", () => _ = SaveAndMarkResolvedAsync(), primary: true);

        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(8, 4) };
        toolbar.Children.Add(ActionButton("Prev conflict", () => GoToPreviousConflict()));
        toolbar.Children.Add(ActionButton("Next conflict", () => GoToNextConflict()));
        toolbar.Children.Add(_conflictsCountText);
        toolbar.Children.Add(_damagedMarkersHint);
        toolbar.Children.Add(_saveBtn);
        toolbar.Children.Add(_saveAndResolveBtn);
        toolbar.Children.Add(ActionButton("Take ours", () => _ = TakeOursCurrentAsync()));
        toolbar.Children.Add(ActionButton("Take theirs", () => _ = TakeTheirsCurrentAsync()));
        toolbar.Children.Add(ActionButton("Re-create with base", () => _ = RecreateConflictAsync()));
        toolbar.Children.Add(ActionButton("External tool", () => _ = LaunchExternalToolAsync()));

        // Floating region action box
        _floatingRegionBox = new Border
        {
            Child = _floatingRegionStrip,
            Padding = new Thickness(6, 3),
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1),
            IsVisible = false,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 6, 24, 0),
        };
        _floatingRegionBox.Dyn(Border.BackgroundProperty, "ColorSurface2")
                          .Dyn(Border.BorderBrushProperty, "ColorBorder");

        // Two-column Ours | Theirs grid
        var topPanesGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,*"),
        };
        Grid.SetColumn(_oursEditor, 0);
        var sep = new Rectangle { Width = 1, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Stretch };
        sep.Dyn(Shape.FillProperty, "ColorBorder");
        Grid.SetColumn(sep, 1);
        Grid.SetColumn(_theirsEditor, 2);
        topPanesGrid.Children.Add(_oursEditor);
        topPanesGrid.Children.Add(sep);
        topPanesGrid.Children.Add(_theirsEditor);

        // Editor layout: TopPanes / Splitter / Result
        var splitGrid = new Grid
        {
            RowDefinitions = new RowDefinitions("*,3,*"),
            ColumnDefinitions = new ColumnDefinitions("*,12"),
        };
        Grid.SetRow(topPanesGrid, 0);
        Grid.SetColumn(topPanesGrid, 0);

        var hSplitter = new GridSplitter
        {
            Height = 3,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Background = Brushes.Transparent,
            Focusable = false,
        };
        hSplitter.Dyn(Border.BorderBrushProperty, "ColorBorder");
        Grid.SetRow(hSplitter, 1);
        Grid.SetColumn(hSplitter, 0);

        Grid.SetRow(_resultEditor, 2);
        Grid.SetColumn(_resultEditor, 0);

        Grid.SetRow(_minimap, 0);
        Grid.SetRowSpan(_minimap, 3);
        Grid.SetColumn(_minimap, 1);

        splitGrid.Children.Add(topPanesGrid);
        splitGrid.Children.Add(hSplitter);
        splitGrid.Children.Add(_resultEditor);
        splitGrid.Children.Add(_minimap);

        _editorOverlay.Children.Add(splitGrid);
        _editorOverlay.Children.Add(_floatingRegionBox);
        _editorArea.Content = _editorOverlay;

        // Main header & banner container
        var rightTop = new StackPanel { Spacing = 4, Margin = new Thickness(12, 6) };
        var labelsGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*") };
        Grid.SetColumn(_headerOurs, 0);
        Grid.SetColumn(_headerTheirs, 1);
        labelsGrid.Children.Add(_headerOurs);
        labelsGrid.Children.Add(_headerTheirs);

        rightTop.Children.Add(_headerTitle);
        rightTop.Children.Add(labelsGrid);
        rightTop.Children.Add(_opBar);
        rightTop.Children.Add(_externalEndedBanner);
        rightTop.Children.Add(_changedOnDiskBanner);
        rightTop.Children.Add(toolbar);

        var rightDock = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(rightTop, Dock.Top);
        rightDock.Children.Add(rightTop);
        rightDock.Children.Add(_editorArea);

        Content = rightDock;
        this.Dyn(BackgroundProperty, "ColorBg");

        _ctx.State.StatusChanged += OnStatusChanged;
        _ctx.State.SelectedConflictPathChanged += OnSelectedConflictPathChanged;
        _ = LoadAsync();
    }

    private void OnStatusChanged(object? s, StatusChangedEventArgs e)
    {
        Dispatcher.UIThread.Post(async () =>
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                var labels = await _conflicts.GetSideLabelsAsync(_ctx, _viewCts.Token);
                bool stopChanged = _stopId != null && labels.StopId != _stopId;
                _stopId = labels.StopId;
                _headerOurs.Text = $"Ours: {labels.OursTitle}";
                _headerTheirs.Text = $"Theirs: {labels.TheirsTitle}";
                ToolTip.SetTip(_headerOurs, labels.OursDetail);
                ToolTip.SetTip(_headerTheirs, labels.TheirsDetail);

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
                await CheckExternalDiskChangeAsync();

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
                _notifications.Publish(NotificationSeverity.Error, ex.Message);
            }
        });
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
        });
    }

    public async Task LoadAsync()
    {
        try
        {
            var labels = await _conflicts.GetSideLabelsAsync(_ctx, _viewCts.Token);
            _stopId = labels.StopId;
            _headerOurs.Text = $"Ours: {labels.OursTitle}";
            _headerTheirs.Text = $"Theirs: {labels.TheirsTitle}";
            ToolTip.SetTip(_headerOurs, labels.OursDetail);
            ToolTip.SetTip(_headerTheirs, labels.TheirsDetail);

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
        var files = await _conflicts.GetConflictsAsync(_ctx, _viewCts.Token);
        _conflictedFiles.Clear();
        _conflictedFiles.AddRange(files);

        _headerTitle.Text = _conflictedFiles.Count == 0
            ? "All conflicts resolved — you can continue the operation."
            : $"{_conflictedFiles.Count} conflicted file(s)";
    }

    private void ShowAllConflictsResolvedCard()
    {
        _dynamicActionButtons.Clear();
        _currentFile = null;
        _workingCopy = null;
        _diskHash = null;
        _resultEditor.Text = string.Empty;
        _oursEditor.Text = string.Empty;
        _theirsEditor.Text = string.Empty;
        _headerTitle.Text = "All conflicts resolved — you can continue the operation.";
        var card = new StackPanel { Spacing = 10, Margin = new Thickness(24) };
        card.Children.Add(Text("✓ All conflicts resolved", "ColorSuccess", 14, FontWeight.Bold));
        card.Children.Add(Text("You can continue the operation using the Continue button above.", "ColorTextMuted", 12));
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
        _oursEditor.FilePath = file.FilePath;
        _theirsEditor.FilePath = file.FilePath;
        _resultEditor.FilePath = file.FilePath;

        _resultEditor.Text = copy.Text ?? string.Empty;
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
            if (!ReferenceEquals(_editorArea.Content, _editorOverlay))
            {
                _editorArea.Content = _editorOverlay;
            }

            _resultEditor.IsReadOnly = false;
            UpdateConflictParseSync();
            UpdateEditorsFromParse();
        }

        _ctx.State.SetSelectedConflictPath(file.FilePath);
    }

    private void ShowFileLevelCard(ConflictFile file, ConflictWorkingCopy copy)
    {
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
        if (_currentFile == null)
        {
            return;
        }

        RegisterUnsavedWork();

        _debounceCts?.Cancel();
        _debounceCts?.Dispose();
        _debounceCts = new CancellationTokenSource();
        var ct = _debounceCts.Token;

        var docSnapshot = _resultEditor.Document.CreateSnapshot();
        var versionSnapshot = _resultEditor.Document.Version;
        int markerSize = _workingCopy?.MarkerSize ?? 7;

        Task.Delay(150, ct).ContinueWith(_ =>
        {
            if (ct.IsCancellationRequested || _disposed)
            {
                return;
            }

            var text = docSnapshot.Text;
            var parse = ConflictMarkerParser.Parse(text, markerSize);
            var oursLines = ConflictPaneBuilder.Build(parse, ConflictSide.Ours);
            var theirsLines = ConflictPaneBuilder.Build(parse, ConflictSide.Theirs);
            var analyses = ConflictRegionAnalyzer.AnalyzeAll(parse);
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

                    _minimap.Parse = parse;
                    UpdateConflictsCount();
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
        var oursLines = ConflictPaneBuilder.Build(_parse, ConflictSide.Ours);
        var theirsLines = ConflictPaneBuilder.Build(_parse, ConflictSide.Theirs);
        var analyses = ConflictRegionAnalyzer.AnalyzeAll(_parse);
        _snapshot = new ConflictParseSnapshot(_parse, oursLines, theirsLines, analyses, _resultEditor.Document.Version);
        _parseVersion = _resultEditor.Document.Version;
        _minimap.Parse = _parse;
        UpdateConflictsCount();
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

    private void UpdateConflictsCount()
    {
        int remaining = _parse?.Regions.Count ?? 0;
        int malformed = _parse?.MalformedAt.Count ?? 0;
        int total = remaining + malformed;

        _conflictsCountText.Text = total == 1 ? "1 conflict left" : $"{total} conflicts left";

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
        UpdateFloatingRegionBar();
    }

    private void UpdateFloatingRegionBar()
    {
        if (_parse == null || _parse.Regions.Count == 0)
        {
            _floatingRegionBox.IsVisible = false;
            return;
        }

        int caretLine = _resultEditor.TextArea.Caret.Line - 1;
        var region = _parse.Regions.FirstOrDefault(r => caretLine >= r.StartLine && caretLine <= r.EndLine);

        if (region == null)
        {
            _floatingRegionBox.IsVisible = false;
            return;
        }

        _floatingRegionStrip.Children.Clear();
        _dynamicActionButtons.Clear();
        _floatingRegionStrip.Children.Add(DynamicActionButton("Use ours", () => ApplyRegionAction(region, RegionActionChoice.Ours)));
        _floatingRegionStrip.Children.Add(DynamicActionButton("Use theirs", () => ApplyRegionAction(region, RegionActionChoice.Theirs)));
        _floatingRegionStrip.Children.Add(DynamicActionButton("Both (ours first)", () => ApplyRegionAction(region, RegionActionChoice.BothOursFirst)));
        _floatingRegionStrip.Children.Add(DynamicActionButton("Both (theirs first)", () => ApplyRegionAction(region, RegionActionChoice.BothTheirsFirst)));

        if (region.HasBase)
        {
            _floatingRegionStrip.Children.Add(DynamicActionButton("Use base", () => ApplyRegionAction(region, RegionActionChoice.Base)));
        }

        _floatingRegionBox.IsVisible = true;
    }

    public void ApplyRegionAction(ConflictRegion targetRegion, RegionActionChoice side)
    {
        // Guard against stale offsets: if version moved, re-parse synchronously on UI thread
        if (_parseVersion == null || _resultEditor.Document.Version.CompareAge(_parseVersion) != 0)
        {
            UpdateConflictParseSync();
        }

        if (_parse == null)
        {
            return;
        }

        // Find region again by line
        int line = targetRegion.StartLine;
        var freshRegion = _parse.Regions.FirstOrDefault(r => line >= r.StartLine && line <= r.EndLine)
                          ?? _parse.Regions.FirstOrDefault(r => targetRegion.OursStartLine >= r.StartLine && targetRegion.OursStartLine <= r.EndLine);

        if (freshRegion == null)
        {
            return;
        }

        var replacementLines = side switch
        {
            RegionActionChoice.Ours => freshRegion.OursLines,
            RegionActionChoice.Theirs => freshRegion.TheirsLines,
            RegionActionChoice.BothOursFirst => freshRegion.OursLines.Concat(freshRegion.TheirsLines).ToList(),
            RegionActionChoice.BothTheirsFirst => freshRegion.TheirsLines.Concat(freshRegion.OursLines).ToList(),
            RegionActionChoice.Base => freshRegion.BaseLines,
            _ => freshRegion.OursLines,
        };

        var linesToUse = replacementLines ?? freshRegion.OursLines;

        int endLineIdx = freshRegion.EndLine;
        string delim = (endLineIdx >= 0 && endLineIdx < _parse.Delimiters.Count && !string.IsNullOrEmpty(_parse.Delimiters[endLineIdx]))
            ? _parse.Delimiters[endLineIdx]
            : (_parse.Delimiters.FirstOrDefault(d => !string.IsNullOrEmpty(d)) ?? "\n");

        var doc = _resultEditor.Document;
        var startDocLine = doc.GetLineByNumber(freshRegion.StartLine + 1);
        var endDocLine = doc.GetLineByNumber(freshRegion.EndLine + 1);

        int startOffset = startDocLine.Offset;

        using (doc.RunUpdate())
        {
            if (linesToUse.Count == 0)
            {
                int endOffset = endDocLine.Offset + endDocLine.TotalLength;
                doc.Replace(startOffset, endOffset - startOffset, string.Empty);
            }
            else
            {
                int endOffset = endDocLine.EndOffset;
                string replacement = string.Join(delim, linesToUse);
                doc.Replace(startOffset, endOffset - startOffset, replacement);
            }
        }

        RegisterUnsavedWork();
        UpdateConflictParseSync();
        UpdateEditorsFromParse();
        UpdateFloatingRegionBar();
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
        if (_parse == null || _parse.Regions.Count == 0)
        {
            return;
        }

        int currentLine = _resultEditor.TextArea.Caret.Line - 1;
        var next = _parse.Regions.FirstOrDefault(r => r.StartLine > currentLine) ?? _parse.Regions[0];
        _resultEditor.ScrollToLine(next.StartLine + 1);
        _resultEditor.TextArea.Caret.Line = next.StartLine + 1;
    }

    public void GoToPreviousConflict()
    {
        if (_parse == null || _parse.Regions.Count == 0)
        {
            return;
        }

        int currentLine = _resultEditor.TextArea.Caret.Line - 1;
        var prev = _parse.Regions.LastOrDefault(r => r.EndLine < currentLine) ?? _parse.Regions[^1];
        _resultEditor.ScrollToLine(prev.StartLine + 1);
        _resultEditor.TextArea.Caret.Line = prev.StartLine + 1;
    }

    public async Task<bool> SaveCurrentFileAsync(bool stage)
    {
        if (_isBusy || _currentFile == null || _workingCopy == null)
        {
            return false;
        }

        _saving = true;
        IsBusy = true;
        try
        {
            var res = await _conflicts.SaveResolutionAsync(
                _ctx,
                _currentFile.FilePath,
                _resultEditor.Text,
                _workingCopy.Format ?? default,
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
            _resultEditor.Document.UndoStack.MarkAsOriginalFile();
            UnregisterUnsavedWork();

            if (stage)
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
            _notifications.Publish(NotificationSeverity.Error, $"Save failed: {ex.Message}", ex.NotificationDetails());
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

        int remaining = (_parse?.Regions.Count ?? 0) + (_parse?.MalformedAt.Count ?? 0);
        if (remaining > 0)
        {
            var ok = await _dialogs.ConfirmAsync(
                "Unresolved conflicts",
                $"{remaining} conflict block(s) remain — mark resolved anyway?",
                confirmLabel: "Mark resolved");
            if (!ok)
            {
                return;
            }
        }

        await SaveCurrentFileAsync(stage: true);
    }

    public async Task TakeOursCurrentAsync()
    {
        if (_isBusy || _currentFile == null)
        {
            return;
        }

        var path = _currentFile.FilePath;
        try
        {
            var labels = await _conflicts.GetSideLabelsAsync(_ctx, _viewCts.Token);
            bool isAbsent = _currentFile.Status is ConflictStatus.DeletedByUs or ConflictStatus.AddedByThem;
            string title = isAbsent ? $"Delete {path}" : "Take ours";
            string message = isAbsent
                ? $"Delete {path} — {labels.OursTitle} deleted it. Any edits to this file will be lost."
                : $"Replace {path} with {labels.OursTitle}'s version? Any edits to this file will be lost.";
            string verb = isAbsent ? "Delete" : "Take ours";

            if (!await _dialogs.ConfirmDestructiveAsync(title, message, verb))
            {
                return;
            }

            IsBusy = true;
            try
            {
                await _conflicts.ResolveWithSideAsync(_ctx, path, ConflictSide.Ours, _viewCts.Token);
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
            finally
            {
                IsBusy = false;
            }
        }
        catch (Exception ex)
        {
            _notifications.Publish(NotificationSeverity.Error, $"Take ours failed: {ex.Message}", ex.NotificationDetails());
        }
    }

    public async Task TakeTheirsCurrentAsync()
    {
        if (_isBusy || _currentFile == null)
        {
            return;
        }

        var path = _currentFile.FilePath;
        try
        {
            var labels = await _conflicts.GetSideLabelsAsync(_ctx, _viewCts.Token);
            bool isAbsent = _currentFile.Status is ConflictStatus.DeletedByThem or ConflictStatus.AddedByUs;
            string title = isAbsent ? $"Delete {path}" : "Take theirs";
            string message = isAbsent
                ? $"Delete {path} — {labels.TheirsTitle} deleted it. Any edits to this file will be lost."
                : $"Replace {path} with {labels.TheirsTitle}'s version? Any edits to this file will be lost.";
            string verb = isAbsent ? "Delete" : "Take theirs";

            if (!await _dialogs.ConfirmDestructiveAsync(title, message, verb))
            {
                return;
            }

            IsBusy = true;
            try
            {
                await _conflicts.ResolveWithSideAsync(_ctx, path, ConflictSide.Theirs, _viewCts.Token);
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
            finally
            {
                IsBusy = false;
            }
        }
        catch (Exception ex)
        {
            _notifications.Publish(NotificationSeverity.Error, $"Take theirs failed: {ex.Message}", ex.NotificationDetails());
        }
    }

    public async Task RecreateConflictAsync()
    {
        if (_isBusy || _currentFile == null)
        {
            return;
        }

        var path = _currentFile.FilePath;
        if (!await _dialogs.ConfirmDestructiveAsync(
                "Re-create conflict",
                $"Re-create conflict markers for {path} with base section? Any edits to this file will be lost.",
                "Re-create"))
        {
            return;
        }

        IsBusy = true;
        try
        {
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
        catch (Exception ex)
        {
            _notifications.Publish(NotificationSeverity.Error, $"Re-create conflict failed: {ex.Message}", ex.NotificationDetails());
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
        catch (Exception ex)
        {
            _notifications.Publish(NotificationSeverity.Error, $"External tool failed: {ex.Message}", ex.NotificationDetails());
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task ContinueAsync()
    {
        if (_isBusy)
        {
            return;
        }

        if (HasUnsavedWork && _currentFile != null)
        {
            var choice = await _dialogs.ChooseAsync(
                "Unsaved changes",
                $"{_currentFile.FilePath} has unsaved edits. Save and mark it resolved before continuing?",
                ["Save & mark resolved", "Discard changes"],
                defaultIndex: 0,
                destructiveIndex: 1);

            if (choice == 0)
            {
                int remaining = (_parse?.Regions.Count ?? 0) + (_parse?.MalformedAt.Count ?? 0);
                if (remaining > 0)
                {
                    var ok = await _dialogs.ConfirmAsync(
                        "Unresolved conflicts",
                        $"{remaining} conflict block(s) remain — mark resolved anyway?",
                        confirmLabel: "Mark resolved");
                    if (!ok)
                    {
                        return;
                    }
                }

                var saved = await SaveCurrentFileAsync(stage: true);
                if (!saved)
                {
                    return;
                }
            }
            else if (choice == 1)
            {
                DiscardUnsavedState();
                await SwitchToFileAsync(_currentFile);
            }
            else
            {
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
        catch (HistoriaGitException ex) when (ex.Kind == GitErrorKind.MergeConflict)
        {
            _notifications.Publish(NotificationSeverity.Warning, ex.Message);
        }
        catch (Exception ex)
        {
            _notifications.Publish(NotificationSeverity.Error, $"Continue failed: {ex.Message}", ex.NotificationDetails());
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task SkipAsync()
    {
        if (_isBusy)
        {
            return;
        }

        var labels = await _conflicts.GetSideLabelsAsync(_ctx, _ctx.Lifetime);
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
        catch (Exception ex)
        {
            _notifications.Publish(NotificationSeverity.Error, $"Skip failed: {ex.Message}", ex.NotificationDetails());
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task AbortAsync()
    {
        if (_isBusy)
        {
            return;
        }

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
        catch (Exception ex)
        {
            _notifications.Publish(NotificationSeverity.Error, $"Abort failed: {ex.Message}", ex.NotificationDetails());
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task CheckExternalDiskChangeAsync()
    {
        if (_saving || _currentFile == null || _diskHash == null)
        {
            return;
        }

        var currentCopy = await _conflicts.LoadWorkingCopyAsync(_ctx, _currentFile.FilePath, _viewCts.Token);
        if (currentCopy.DiskHash != _diskHash)
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

    internal async Task KeepMineAsync()
    {
        if (_isBusy || _currentFile == null)
        {
            return;
        }

        var path = _currentFile.FilePath;
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
        catch (Exception ex)
        {
            _notifications.Publish(NotificationSeverity.Error, $"Keep mine failed: {ex.Message}", ex.NotificationDetails());
        }
        finally
        {
            IsBusy = false;
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
        _viewCts.Dispose();
        _switchCts?.Cancel();
        _switchCts?.Dispose();
        _debounceCts?.Cancel();
        _debounceCts?.Dispose();
        UnregisterUnsavedWork();
    }
}
