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
    private readonly IPatchService? _patches;
    private readonly INotificationService? _notifications;
    private readonly Button _toggleButton;
    private FileDiff? _diff;
    private DiffDisplayModel? _model;
    private bool _sideBySide;
    private readonly bool _wordWrap;
    private CancellationTokenSource? _loadingCts;
    private bool _disposed;

    private CombinedDiffPresenter? _combinedPresenter;
    private SideBySideDiffPresenter? _sideBySidePresenter;

    public Control? PresenterForTesting => _host.Content as Control;
    public FileDiff? DiffForTesting => _diff;
    public Button ToggleButtonForTesting => _toggleButton;
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

        var close = TextButton("Close", () => ctx.State.SetOpenDiff(null));
        Grid.SetColumn(close, 4);
        header.Children.Add(close);

        var root = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        root.Children.Add(_host);
        Content = root;

        _ = LoadAsync();
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
            _combinedPresenter = null;
            _sideBySidePresenter = null;
            _host.Content = new TextBlock
            {
                Text = "Binary file — no textual diff.",
                Margin = new Thickness(12),
            }.Dyn(TextBlock.ForegroundProperty, "ColorTextMuted");
            return;
        }

        if (_diff.IsTruncated)
        {
            _combinedPresenter = null;
            _sideBySidePresenter = null;
            _host.Content = new TextBlock
            {
                Text = "File too large to diff inline",
                Margin = new Thickness(12),
            }.Dyn(TextBlock.ForegroundProperty, "ColorTextMuted");
            return;
        }

        if (_diff.Hunks.Count == 0)
        {
            _combinedPresenter = null;
            _sideBySidePresenter = null;
            _host.Content = new TextBlock
            {
                Text = "No changes.",
                Margin = new Thickness(12),
            }.Dyn(TextBlock.ForegroundProperty, "ColorTextMuted");
            return;
        }

        _model ??= DiffDisplayModel.Create(_diff);
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
            sbs.LeftPresenter.WordWrap = _wordWrap;
            sbs.RightPresenter.WordWrap = _wordWrap;
            sbs.HunkActionRequested += OnHunkActionRequested;
            sbs.SelectionActionRequested += OnSelectionActionRequested;
            _sideBySidePresenter = sbs;
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
            _combinedPresenter = combined;
            _host.Content = combined;
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

    public async Task ApplyHunkAsync(int hunkIndex)
    {
        if (_patches is null || _diff is null || _disposed)
        {
            return;
        }

        var stage = _request.Source == DiffSource.UnstagedChanges;
        var reverse = !stage;
        try
        {
            var selected = new HashSet<DiffLineRef> { new(hunkIndex, 0) };
            var patch = _patches.GeneratePatch(_diff, PatchScope.SingleHunk, selected, reverse: reverse);
            if (string.IsNullOrEmpty(patch) || _disposed)
            {
                return;
            }

            var check = await _patches.CheckAsync(_ctx, patch, PatchTarget.StagingArea, reverse, _ctx.Lifetime);
            if (_disposed) return;
            if (!check.CanApply)
            {
                _notifications?.Publish(NotificationSeverity.Warning,
                    $"Cannot {(stage ? "stage" : "unstage")} this hunk: {check.FailureReason}");
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
    }

    public async Task ApplySelectionAsync(IReadOnlyList<DiffLineRef> refs)
    {
        if (_patches is null || _diff is null || _disposed)
        {
            return;
        }

        var stage = _request.Source == DiffSource.UnstagedChanges;
        var reverse = !stage;
        try
        {
            var selected = refs.ToHashSet();
            var patch = _patches.GeneratePatch(_diff, PatchScope.SelectedLines, selected, reverse: reverse);
            if (string.IsNullOrEmpty(patch) || _disposed)
            {
                return;
            }

            var check = await _patches.CheckAsync(_ctx, patch, PatchTarget.StagingArea, reverse, _ctx.Lifetime);
            if (_disposed) return;
            if (!check.CanApply)
            {
                _notifications?.Publish(NotificationSeverity.Warning,
                    $"Cannot {(stage ? "stage" : "unstage")} selected lines: {check.FailureReason}");
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
        _host.Content = null;
        _combinedPresenter = null;
        _sideBySidePresenter = null;
    }
}
