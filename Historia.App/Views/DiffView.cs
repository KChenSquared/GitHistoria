namespace Historia.Views;

/// <summary>
/// File diff view powered by AvaloniaEdit presenters (Combined and Side-by-Side).
/// Adapted from SourceGit (MIT) — see docs/third-party-licenses.md.
/// </summary>
public sealed class DiffView : UserControl, IDisposable
{
    private readonly RepositoryContext _ctx;
    private readonly DiffRequest _request;
    private readonly ISettingsService? _settings;
    private readonly IServiceProvider? _sp;
    private readonly ContentControl _host = new();
    private readonly DiffMinimap _minimap;
    private readonly IPatchService? _patches;
    private readonly INotificationService? _notifications;
    private readonly Button _toggleButton;
    private readonly Button _closeButton;
    private FileDiff? _diff;
    private DiffDisplayModel? _model;
    private bool _sideBySide;
    private readonly bool _wordWrap;
    private CancellationTokenSource? _loadingCts;
    private bool _disposed;
    private bool _patchInFlight;
    private bool _focusLinePending;
    private int _focusLineAttempts;

    private CombinedDiffPresenter? _combinedPresenter;
    private SideBySideDiffPresenter? _sideBySidePresenter;

    public Control? PresenterForTesting => _host.Content as Control;
    public DiffMinimap MinimapForTesting => _minimap;
    public FileDiff? DiffForTesting => _diff;
    internal DiffDisplayModel? ModelForTesting => _model;
    internal Button CloseButtonForTesting => _closeButton;
    public void ToggleModeForTesting() => OnToggleMode();
    internal int LastModelCreationThreadId { get; private set; }

    public DiffView(RepositoryContext ctx, DiffRequest request, ISettingsService? settings, IServiceProvider? sp)
    {
        _ctx = ctx;
        _request = request;
        _settings = settings;
        _sp = sp;
        _sideBySide = settings?.Current.DefaultDiffView == DiffViewMode.SideBySide;
        _wordWrap = settings?.Current.DiffWordWrap ?? false;
        _patches = sp?.GetService(typeof(IPatchService)) as IPatchService;
        _notifications = sp?.GetService(typeof(INotificationService)) as INotificationService;
        _focusLinePending = request.FocusLine is > 0;

        this.Dyn(BackgroundProperty, "ColorBg");

        var header = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto,Auto"),
            Height = 30,
            Margin = new Thickness(12, 4),
        };
        header.Children.Add(Text(request.Path, "ColorText", 12, FontWeight.SemiBold));

        var prevBtn = TextButton("Prev change", OnPreviousChange);
        Grid.SetColumn(prevBtn, 1);
        header.Children.Add(prevBtn);

        var nextBtn = TextButton("Next change", OnNextChange);
        Grid.SetColumn(nextBtn, 2);
        header.Children.Add(nextBtn);

        _toggleButton = TextButton(_sideBySide ? "Unified" : "Side-by-side", OnToggleMode);
        Grid.SetColumn(_toggleButton, 3);
        header.Children.Add(_toggleButton);

        _closeButton = TextButton("X", () => ctx.State.SetOpenDiff(null));
        ToolTip.SetTip(_closeButton, "Close");
        Avalonia.Automation.AutomationProperties.SetName(_closeButton, "Close");
        Grid.SetColumn(_closeButton, 4);
        header.Children.Add(_closeButton);

        _minimap = new DiffMinimap
        {
            Width = 12,
            IsVisible = false,
        };
        _minimap.LineNavigationRequested += OnMinimapLineNavigationRequested;

        var body = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
        };
        Grid.SetColumn(_host, 0);
        body.Children.Add(_host);
        Grid.SetColumn(_minimap, 1);
        body.Children.Add(_minimap);

        var root = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        root.Children.Add(body);
        Content = root;

        _ = LoadAsync();
    }

    private void OnMinimapLineNavigationRequested(object? sender, int line)
    {
        if (_sideBySide)
        {
            _sideBySidePresenter?.ScrollToLine(line);
        }
        else
        {
            _combinedPresenter?.ScrollToLine(line);
        }
    }

    private void OnPresenterVisibleLineRangeChanged(object? sender, (int Start, int End)? range)
    {
        _minimap.DisplayRange = range;
    }

    private void OnToggleMode()
    {
        _sideBySide = !_sideBySide;
        _toggleButton.Content = _sideBySide ? "Unified" : "Side-by-side";
        var mode = _sideBySide ? DiffViewMode.SideBySide : DiffViewMode.Unified;
        SaveDiffViewModePreference(mode);
        Rebuild();
    }

    private async void SaveDiffViewModePreference(DiffViewMode mode)
    {
        if (_settings is null) return;
        try
        {
            await _settings.UpdateAsync(s => s with { DefaultDiffView = mode });
        }
        catch (Exception ex)
        {
            _notifications?.Publish(NotificationSeverity.Warning, "Failed to save diff view preference.", ex.NotificationDetails());
        }
    }

    private void OnPreviousChange()
    {
        if (_sideBySide)
        {
            _sideBySidePresenter?.GoToPreviousChange();
        }
        else
        {
            _combinedPresenter?.GoToPreviousChange();
        }
    }

    private void OnNextChange()
    {
        if (_sideBySide)
        {
            _sideBySidePresenter?.GoToNextChange();
        }
        else
        {
            _combinedPresenter?.GoToNextChange();
        }
    }

    private async Task LoadAsync()
    {
        _loadingCts?.Cancel();
        _loadingCts?.Dispose();
        var cts = new CancellationTokenSource();
        _loadingCts = cts;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(200, cts.Token);
                Dispatcher.UIThread.Post(() =>
                {
                    if (!_disposed && _diff is null && _host.Content is null)
                    {
                        _host.Content = new TextBlock
                        {
                            Text = "Loading diff...",
                            Margin = new Thickness(12),
                        }.Dyn(TextBlock.ForegroundProperty, "ColorTextMuted");
                    }
                });
            }
            catch (OperationCanceledException) { }
        }, cts.Token);

        try
        {
            var diff = await _ctx.Gateway.GetFileDiffAsync(_request, _ctx.Lifetime);
            if (_disposed) return;
            _diff = diff;
            cts.Cancel();
            var model = await Task.Run(() =>
            {
                LastModelCreationThreadId = Environment.CurrentManagedThreadId;
                return DiffDisplayModel.Create(diff);
            });
            if (_disposed) return;
            _model = model;
            Rebuild();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!_disposed)
            {
                _host.Content = Text($"Could not load diff: {ex.Message}", "ColorDanger", 12);
            }
        }
    }

    public void Rebuild()
    {
        if (_disposed || _diff is null)
        {
            return;
        }

        if (_diff.IsBinary)
        {
            _focusLinePending = false;
            _combinedPresenter = null;
            _sideBySidePresenter = null;
            _minimap.IsVisible = false;
            _minimap.Model = null;
            _host.Content = new TextBlock
            {
                Text = "Binary file — no textual diff.",
                Margin = new Thickness(12),
            }.Dyn(TextBlock.ForegroundProperty, "ColorTextMuted");
            return;
        }

        if (_diff.IsTruncated)
        {
            _focusLinePending = false;
            _combinedPresenter = null;
            _sideBySidePresenter = null;
            _minimap.IsVisible = false;
            _minimap.Model = null;
            _host.Content = new TextBlock
            {
                Text = "File too large to diff inline",
                Margin = new Thickness(12),
            }.Dyn(TextBlock.ForegroundProperty, "ColorTextMuted");
            return;
        }

        if (_diff.Hunks.Count == 0)
        {
            _focusLinePending = false;
            _combinedPresenter = null;
            _sideBySidePresenter = null;
            _minimap.IsVisible = false;
            _minimap.Model = null;
            _host.Content = new TextBlock
            {
                Text = "No changes.",
                Margin = new Thickness(12),
            }.Dyn(TextBlock.ForegroundProperty, "ColorTextMuted");
            return;
        }

        _model ??= DiffDisplayModel.Create(_diff);
        _minimap.IsVisible = true;
        _minimap.Model = _model;
        _minimap.IsSideBySide = _sideBySide;

        var canStage = _patches is not null && !_diff.IsBinary
            && _request.Source is DiffSource.UnstagedChanges or DiffSource.StagedChanges;
        var stageVerb = _request.Source == DiffSource.UnstagedChanges ? "Stage" : "Unstage";

        if (_sideBySide)
        {
            _combinedPresenter = null;
            var sbs = new SideBySideDiffPresenter
            {
                ServicesProvider = _sp,
                FilePath = _request.Path,
                Model = _model,
                AllowStaging = canStage,
                StageVerb = stageVerb,
            };
            sbs.LeftPresenter.WordWrap = false;
            sbs.RightPresenter.WordWrap = false;
            sbs.HunkActionRequested += OnHunkActionRequested;
            sbs.SelectionActionRequested += OnSelectionActionRequested;
            sbs.VisibleLineRangeChanged += OnPresenterVisibleLineRangeChanged;
            _sideBySidePresenter = sbs;
            _minimap.DisplayRange = sbs.VisibleLineRange;
            _host.Content = sbs;
        }
        else
        {
            _sideBySidePresenter = null;
            var combined = new CombinedDiffPresenter
            {
                ServicesProvider = _sp,
                FilePath = _request.Path,
                Model = _model,
                AllowStaging = canStage,
                StageVerb = stageVerb,
                WordWrap = _wordWrap,
            };
            combined.HunkActionRequested += OnHunkActionRequested;
            combined.SelectionActionRequested += OnSelectionActionRequested;
            combined.VisibleLineRangeChanged += OnPresenterVisibleLineRangeChanged;
            _combinedPresenter = combined;
            _minimap.DisplayRange = combined.VisibleLineRange;
            _host.Content = combined;
        }

        if (_focusLinePending)
        {
            Dispatcher.UIThread.Post(ApplyFocusLine, DispatcherPriority.Loaded);
        }
    }

    /// <summary>Opens at the top instead of the request's line: a view rebuilt for a request whose
    /// line was already shown keeps the user where they scrolled. A later
    /// <see cref="RefocusLine"/> still scrolls to it.</summary>
    internal void SkipInitialFocusLine() => _focusLinePending = false;

    /// <summary>Scrolls to the request's line again, for a repeated request for this diff. Before the
    /// diff has loaded, the load applies it.</summary>
    internal void RefocusLine()
    {
        if (_disposed || _request.FocusLine is not > 0)
        {
            return;
        }

        _focusLinePending = true;
        _focusLineAttempts = 0;
        if (_model is not null)
        {
            Dispatcher.UIThread.Post(ApplyFocusLine, DispatcherPriority.Loaded);
        }
    }

    /// <summary>Scrolls to the requested line once the presenter has been laid out; a view that is
    /// not on screen yet is retried a few times, and the request is spent either way so later
    /// mode switches never pull the user back.</summary>
    private void ApplyFocusLine()
    {
        if (_disposed || !_focusLinePending || _model is null || _request.FocusLine is not { } focus)
        {
            return;
        }

        var presenter = _host.Content as Control;
        if ((presenter is null || !presenter.IsEffectivelyVisible || presenter.Bounds.Height <= 0) && ++_focusLineAttempts < 20)
        {
            Dispatcher.UIThread.Post(ApplyFocusLine, DispatcherPriority.Background);
            return;
        }

        _focusLinePending = false;
        if (_model.FindDisplayLineForNewLine(focus, _sideBySide) is not { } line)
        {
            return;
        }

        if (_sideBySide)
        {
            _sideBySidePresenter?.ScrollToLine(line);
        }
        else
        {
            _combinedPresenter?.ScrollToLine(line);
        }
    }

    private void OnHunkActionRequested(object? sender, int hunkIndex)
    {
        _ = ApplyHunkAsync(hunkIndex);
    }

    private void OnSelectionActionRequested(object? sender, IReadOnlyList<DiffLineRef> refs)
    {
        _ = ApplySelectionAsync(refs);
    }

    public Task ApplyHunkAsync(int hunkIndex)
        => ApplyPatchAsync(PatchScope.SingleHunk, new HashSet<DiffLineRef> { new(hunkIndex, 0) }, "this hunk");

    public Task ApplySelectionAsync(IReadOnlyList<DiffLineRef> refs)
        => ApplyPatchAsync(PatchScope.SelectedLines, refs.ToHashSet(), "selected lines");

    private async Task ApplyPatchAsync(PatchScope scope, IReadOnlySet<DiffLineRef> selected, string what)
    {
        if (_patches is null || _diff is null || _disposed || _patchInFlight)
        {
            return;
        }

        _patchInFlight = true;
        var diffAtEntry = _diff;
        var stage = _request.Source == DiffSource.UnstagedChanges;
        var reverse = !stage;
        try
        {
            var patch = _patches.GeneratePatch(diffAtEntry, scope, selected, reverse: reverse);
            if (string.IsNullOrEmpty(patch) || _disposed)
            {
                return;
            }

            var check = await _patches.CheckAsync(_ctx, patch, PatchTarget.StagingArea, reverse, _ctx.Lifetime);
            if (_disposed) return;
            if (!check.CanApply)
            {
                _notifications?.Publish(NotificationSeverity.Warning,
                    $"Cannot {(stage ? "stage" : "unstage")} {what}: {check.FailureReason}");
                return;
            }

            await _patches.ApplyPatchAsync(_ctx, patch, PatchTarget.StagingArea, reverse, _ctx.Lifetime);
            if (_disposed) return;
            await _ctx.State.RefreshStatusAsync(_ctx.Lifetime);
            if (_disposed) return;
            var diff = await _ctx.Gateway.GetFileDiffAsync(_request, _ctx.Lifetime);
            if (_disposed) return;
            _diff = diff;
            var model = await Task.Run(() =>
            {
                LastModelCreationThreadId = Environment.CurrentManagedThreadId;
                return DiffDisplayModel.Create(diff);
            });
            if (_disposed) return;
            _model = model;
            Rebuild();
        }
        catch (HistoriaGitException ex)
        {
            if (!_disposed) _notifications?.Publish(NotificationSeverity.Warning, ex.Message);
        }
        catch (Exception ex)
        {
            if (!_disposed) _notifications?.Publish(NotificationSeverity.Error, ex.Message, ex.NotificationDetails());
        }
        finally
        {
            _patchInFlight = false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _loadingCts?.Cancel();
        _loadingCts?.Dispose();
        _loadingCts = null;
        _minimap.LineNavigationRequested -= OnMinimapLineNavigationRequested;
        if (_sideBySidePresenter != null)
        {
            _sideBySidePresenter.VisibleLineRangeChanged -= OnPresenterVisibleLineRangeChanged;
            _sideBySidePresenter.HunkActionRequested -= OnHunkActionRequested;
            _sideBySidePresenter.SelectionActionRequested -= OnSelectionActionRequested;
        }
        if (_combinedPresenter != null)
        {
            _combinedPresenter.VisibleLineRangeChanged -= OnPresenterVisibleLineRangeChanged;
            _combinedPresenter.HunkActionRequested -= OnHunkActionRequested;
            _combinedPresenter.SelectionActionRequested -= OnSelectionActionRequested;
        }
        _host.Content = null;
        _combinedPresenter = null;
        _sideBySidePresenter = null;
    }
}
