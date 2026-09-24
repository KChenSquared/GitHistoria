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

    private readonly ListBox _fileListBox = new();
    private readonly List<ConflictFile> _conflictedFiles = [];
    private readonly HashSet<string> _resolvedPaths = new(StringComparer.Ordinal);
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

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (_isBusy != value)
            {
                _isBusy = value;
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
    internal TextBlock ConflictsCountTextForTesting => _conflictsCountText;
    internal TextBlock DamagedMarkersHintForTesting => _damagedMarkersHint;
    internal Border ExternalEndedBannerForTesting => _externalEndedBanner;
    internal Border ChangedOnDiskBannerForTesting => _changedOnDiskBanner;
    internal ConflictParse? CurrentParseForTesting => _parse;
    internal ConflictFile? CurrentFileForTesting => _currentFile;
    internal ListBox FileListBoxForTesting => _fileListBox;
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
        _continueBtn = TextButton("Continue", () => _ = ContinueAsync(), primary: true);
        _skipBtn = TextButton("Skip", () => _ = SkipAsync());
        _abortBtn = TextButton("Abort", () => _ = AbortAsync());

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
        diskMsgPanel.Children.Add(TextButton("Reload", () => _ = ReloadFromDiskAsync(), primary: true));
        diskMsgPanel.Children.Add(TextButton("Keep mine", () => _ = KeepMineAsync()));
        _changedOnDiskBanner.Child = diskMsgPanel;

        // Toolbar
        _saveBtn = TextButton("Save", () => _ = SaveCurrentFileAsync(stage: false));
        _saveAndResolveBtn = TextButton("Save & mark resolved", () => _ = SaveAndMarkResolvedAsync(), primary: true);

        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(8, 4) };
        toolbar.Children.Add(TextButton("Prev conflict", () => GoToPreviousConflict()));
        toolbar.Children.Add(TextButton("Next conflict", () => GoToNextConflict()));
        toolbar.Children.Add(_conflictsCountText);
        toolbar.Children.Add(_damagedMarkersHint);
        toolbar.Children.Add(_saveBtn);
        toolbar.Children.Add(_saveAndResolveBtn);
        toolbar.Children.Add(TextButton("Take ours", () => _ = TakeOursCurrentAsync()));
        toolbar.Children.Add(TextButton("Take theirs", () => _ = TakeTheirsCurrentAsync()));
        toolbar.Children.Add(TextButton("Re-create with base", () => _ = RecreateConflictAsync()));
        toolbar.Children.Add(TextButton("External tool", () => _ = LaunchExternalToolAsync()));

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

        // File list on left
        _fileListBox.SelectionChanged += OnFileListSelectionChanged;
        var leftPanel = new DockPanel { Width = 260, Margin = new Thickness(6) };
        var leftHeader = Text("Conflicted Files", "ColorText", 12, FontWeight.SemiBold);
        DockPanel.SetDock(leftHeader, Dock.Top);
        leftPanel.Children.Add(leftHeader);
        leftPanel.Children.Add(_fileListBox);

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

        var rootGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*"),
        };
        Grid.SetColumn(leftPanel, 0);
        var vSplitter = new GridSplitter
        {
            Width = 3,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Background = Brushes.Transparent,
            Focusable = false,
        };
        vSplitter.Dyn(Border.BorderBrushProperty, "ColorBorder");
        Grid.SetColumn(vSplitter, 1);
        Grid.SetColumn(rightDock, 2);

        rootGrid.Children.Add(leftPanel);
        rootGrid.Children.Add(vSplitter);
        rootGrid.Children.Add(rightDock);

        Content = rootGrid;
        this.Dyn(BackgroundProperty, "ColorBg");

        _ctx.State.StatusChanged += OnStatusChanged;
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

                if (stopChanged)
                {
                    _resolvedPaths.Clear();
                }

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

                await RefreshFileListAsync();
                await CheckExternalDiskChangeAsync();

                if (stopChanged)
                {
                    if (_currentFile != null && _conflictedFiles.Any(f => string.Equals(f.FilePath, _currentFile.FilePath, StringComparison.Ordinal)))
                    {
                        var matching = _conflictedFiles.First(f => string.Equals(f.FilePath, _currentFile.FilePath, StringComparison.Ordinal));
                        await SwitchToFileAsync(matching);
                    }
                    else if (_conflictedFiles.Count > 0)
                    {
                        await SwitchToFileAsync(_conflictedFiles[0]);
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

            await RefreshFileListAsync();

            if (_conflictedFiles.Count > 0 && _currentFile == null)
            {
                await SwitchToFileAsync(_conflictedFiles[0]);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _notifications.Publish(NotificationSeverity.Error, ex.Message, ex.NotificationDetails());
        }
    }

    private async Task RefreshFileListAsync()
    {
        var files = await _conflicts.GetConflictsAsync(_ctx, _viewCts.Token);
        var fileDict = files.ToDictionary(f => f.FilePath, StringComparer.Ordinal);
        var previousDict = _conflictedFiles.ToDictionary(f => f.FilePath, StringComparer.Ordinal);
        var merged = new List<ConflictFile>(files);
        foreach (var path in _resolvedPaths)
        {
            if (!fileDict.ContainsKey(path))
            {
                if (previousDict.TryGetValue(path, out var prev))
                {
                    merged.Add(prev);
                }
                else
                {
                    merged.Add(new ConflictFile { FilePath = path, Status = ConflictStatus.BothModified, IsBinary = false });
                }
            }
        }

        _conflictedFiles.Clear();
        _conflictedFiles.AddRange(merged);

        var listItems = new List<Control>();
        foreach (var file in _conflictedFiles)
        {
            var isResolved = _resolvedPaths.Contains(file.FilePath);
            var title = isResolved ? $"✓ {file.FilePath}" : file.FilePath;
            var text = Text($"{title} ({file.Status})", isResolved ? "ColorSuccess" : "ColorText", 12);
            listItems.Add(text);
        }

        _fileListBox.ItemsSource = listItems;

        var selectedIndex = -1;
        if (_currentFile != null)
        {
            selectedIndex = _conflictedFiles.FindIndex(f => string.Equals(f.FilePath, _currentFile.FilePath, StringComparison.Ordinal));
        }
        if (selectedIndex >= 0)
        {
            _fileListBox.SelectedIndex = selectedIndex;
        }
        else if (_conflictedFiles.Count > 0)
        {
            _fileListBox.SelectedIndex = 0;
        }

        _headerTitle.Text = _conflictedFiles.Count == 0 || _conflictedFiles.All(f => _resolvedPaths.Contains(f.FilePath))
            ? "All conflicts resolved — you can continue the operation."
            : $"{_conflictedFiles.Count(f => !_resolvedPaths.Contains(f.FilePath))} conflicted file(s)";
    }

    private async void OnFileListSelectionChanged(object? sender, Avalonia.Controls.SelectionChangedEventArgs e)
    {
        int index = _fileListBox.SelectedIndex;
        if (index < 0 || index >= _conflictedFiles.Count)
        {
            return;
        }

        var nextFile = _conflictedFiles[index];
        if (_currentFile != null && string.Equals(_currentFile.FilePath, nextFile.FilePath, StringComparison.Ordinal))
        {
            return;
        }

        if (HasUnsavedWork)
        {
            var work = _unsavedRegistry?.For(_ctx) ?? [new ConflictUnsavedWork(this, _ctx, _currentFile?.FilePath ?? "")];
            var proceed = await UnsavedWorkPrompt.ResolveAsync(work, _dialogs);
            if (!proceed)
            {
                // Revert selection
                _fileListBox.SelectedIndex = _conflictedFiles.FindIndex(f => string.Equals(f.FilePath, _currentFile?.FilePath, StringComparison.Ordinal));
                return;
            }
        }

        await SwitchToFileAsync(nextFile);
    }

    private async Task SwitchToFileAsync(ConflictFile file)
    {
        _currentFile = file;
        _workingCopy = await _conflicts.LoadWorkingCopyAsync(_ctx, file.FilePath, _viewCts.Token);
        _diskHash = _workingCopy.DiskHash;
        _oursEditor.FilePath = file.FilePath;
        _theirsEditor.FilePath = file.FilePath;
        _resultEditor.FilePath = file.FilePath;

        bool isEditable = !file.IsBinary && !file.IsSubmodule &&
                          !_workingCopy.IsBinary && !_workingCopy.UnsupportedEncoding &&
                          !_workingCopy.ExceedsBudget && _workingCopy.Text != null &&
                          !_resolvedPaths.Contains(file.FilePath);

        if (!isEditable)
        {
            // Show file card only
            ShowFileLevelCard(file, _workingCopy);
            return;
        }

        if (!ReferenceEquals(_editorArea.Content, _editorOverlay))
        {
            _editorArea.Content = _editorOverlay;
        }

        _resultEditor.IsReadOnly = false;
        _resultEditor.Text = _workingCopy.Text ?? string.Empty;
        _resultEditor.Document.UndoStack.MarkAsOriginalFile();
        _parseVersion = _resultEditor.Document.Version;

        UnregisterUnsavedWork();
        UpdateConflictParseSync();
        UpdateEditorsFromParse();
    }

    private void ShowFileLevelCard(ConflictFile file, ConflictWorkingCopy copy)
    {
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
        else if (_resolvedPaths.Contains(file.FilePath))
        {
            card.Children.Add(Text("✓ Marked resolved.", "ColorSuccess", 12));
        }

        var acts = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        acts.Children.Add(TextButton("Take ours", () => _ = TakeOursCurrentAsync()));
        acts.Children.Add(TextButton("Take theirs", () => _ = TakeTheirsCurrentAsync()));
        if (!file.IsSubmodule)
        {
            acts.Children.Add(TextButton("External tool", () => _ = LaunchExternalToolAsync()));
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
        _floatingRegionStrip.Children.Add(TextButton("Use ours", () => ApplyRegionAction(region, RegionActionChoice.Ours)));
        _floatingRegionStrip.Children.Add(TextButton("Use theirs", () => ApplyRegionAction(region, RegionActionChoice.Theirs)));
        _floatingRegionStrip.Children.Add(TextButton("Both (ours first)", () => ApplyRegionAction(region, RegionActionChoice.BothOursFirst)));
        _floatingRegionStrip.Children.Add(TextButton("Both (theirs first)", () => ApplyRegionAction(region, RegionActionChoice.BothTheirsFirst)));

        if (region.HasBase)
        {
            _floatingRegionStrip.Children.Add(TextButton("Use base", () => ApplyRegionAction(region, RegionActionChoice.Base)));
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
        if (_currentFile == null || _workingCopy == null)
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
                _resolvedPaths.Add(_currentFile.FilePath);
                await RefreshFileListAsync();
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
        if (saved)
        {
            // Move to next conflicted file
            var next = _conflictedFiles.FirstOrDefault(f => !_resolvedPaths.Contains(f.FilePath));
            if (next != null)
            {
                await SwitchToFileAsync(next);
            }
        }
    }

    public async Task TakeOursCurrentAsync()
    {
        if (_currentFile == null)
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
                _resolvedPaths.Add(path);
                UnregisterUnsavedWork();
                await RefreshFileListAsync();
                await SwitchToFileAsync(_currentFile);
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
        if (_currentFile == null)
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
                _resolvedPaths.Add(path);
                UnregisterUnsavedWork();
                await RefreshFileListAsync();
                await SwitchToFileAsync(_currentFile);
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
        if (_currentFile == null)
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
            _resolvedPaths.Remove(path);
            UnregisterUnsavedWork();
            await RefreshFileListAsync();
            await SwitchToFileAsync(_currentFile);
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
        if (_currentFile == null)
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

            await SwitchToFileAsync(_currentFile);
        }
        catch (Exception ex)
        {
            _notifications.Publish(NotificationSeverity.Error, $"External tool failed: {ex.Message}", ex.NotificationDetails());
        }
    }

    public async Task ContinueAsync()
    {
        IsBusy = true;
        try
        {
            await _conflicts.ContinueOperationAsync(_ctx, _ctx.Lifetime);
            await _ctx.State.RefreshAllAsync(_ctx.Lifetime);
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
            await _ctx.State.RefreshAllAsync(_ctx.Lifetime);
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
        if (!await _dialogs.ConfirmDestructiveAsync(
                "Abort operation",
                "Abort the in-progress merge / rebase / cherry-pick? Conflict resolutions made so far will be discarded.",
                "Abort"))
        {
            return;
        }

        IsBusy = true;
        try
        {
            await _conflicts.AbortOperationAsync(_ctx, _ctx.Lifetime);
            await _ctx.State.RefreshAllAsync(_ctx.Lifetime);
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
        if (_currentFile == null)
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
        if (_currentFile == null)
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
        _viewCts.Cancel();
        _viewCts.Dispose();
        _debounceCts?.Cancel();
        _debounceCts?.Dispose();
        UnregisterUnsavedWork();
    }
}
