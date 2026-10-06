namespace Historia.Views;

/// <summary>
/// The repository detail pane. A toolbar with the current branch
/// and Fetch / Pull / Push, then either the selected-commit detail or the working-copy staging
/// panel. The commit graph itself is the native <see cref="Controls.CommitGraphControl"/> in
/// the centre column.
/// </summary>
public sealed class RepositoryView : UserControl, IDisposable
{
    private readonly IServiceProvider _sp;
    private readonly IWorkspaceService _workspace;
    private readonly INotificationService _notifications;
    private readonly Historia.Services.UI.IDialogService _dialogs;
    private readonly Historia.Services.PowerTools.IStashService _stashes;
    private readonly Historia.Services.Settings.IRepoLayoutService _repoLayout;
    private readonly Historia.Services.Settings.ISettingsService _settings;

    private readonly TextBlock _branch = Text("", "ColorText", 16, FontWeight.Bold);
    private readonly TextBlock _ab = Text("", "ColorTextMuted", 12);
    private readonly Panel _topBar;
    private readonly Grid _branchBar;
    private readonly Button _workingCopy;
    private readonly TextBlock _workingCopyText = new()
    {
        VerticalAlignment = VerticalAlignment.Center,
        FontSize = 16,
        FontWeight = FontWeight.Bold,
    };
    private readonly ContentControl _body = new();
    private readonly ContentControl _bannerHost = new();
    private readonly TextBlock _opBanner = Text("", "ColorText", 12);
    private readonly Border _opBox;
    private readonly Button _opOpenResolverBtn;
    private readonly Button _opContinueBtn;
    private readonly Button _opAbortBtn;
    private readonly StackPanel _opActions;
    private readonly TextBlock _netProgress = Text("", "ColorTextMuted", 12);
    private readonly Border _netProgressBox = new();
    private readonly Historia.Services.UI.IRepoNetworkActionRunner _networkRunner;
    private RepositoryContext? _repo;
    private StagingPanelView? _stagingPanel;
    private CommitDetailPanelView? _detailPanel;
    private bool _opBusy;

    internal Panel TopBarForTesting => _topBar;
    internal Grid BranchBarForTesting => _branchBar;
    internal Button WorkingCopyButtonForTesting => _workingCopy;
    internal TextBlock BranchTextBlockForTesting => _branch;
    internal TextBlock WorkingCopyTextBlockForTesting => _workingCopyText;
    internal Border OpBoxForTesting => _opBox;
    internal Button OpContinueButtonForTesting => _opContinueBtn;
    internal Button OpOpenResolverButtonForTesting => _opOpenResolverBtn;
    internal TextBlock OpBannerTextBlockForTesting => _opBanner;
    internal Button OpAbortButtonForTesting => _opAbortBtn;

    public RepositoryView(IServiceProvider sp)
    {
        _sp = sp;
        _workspace = sp.GetRequiredService<IWorkspaceService>();
        _notifications = sp.GetRequiredService<INotificationService>();
        _dialogs = sp.GetRequiredService<Historia.Services.UI.IDialogService>();
        _stashes = sp.GetRequiredService<Historia.Services.PowerTools.IStashService>();
        _repoLayout = sp.GetRequiredService<Historia.Services.Settings.IRepoLayoutService>();
        _settings = sp.GetRequiredService<Historia.Services.Settings.ISettingsService>();
        _networkRunner = sp.GetRequiredService<Historia.Services.UI.IRepoNetworkActionRunner>();

        _branchBar = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*"),
            Margin = new Thickness(12, SectionFrame.HeaderGap),
            VerticalAlignment = VerticalAlignment.Center,
        };
        _branchBar.Children.Add(_branch);
        Grid.SetColumn(_ab, 1);
        _ab.Margin = new Thickness(8, 0, 0, 0);
        _branchBar.Children.Add(_ab);

        _workingCopy = new Button
        {
            Content = _workingCopyText,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            CornerRadius = new CornerRadius(0),
            Padding = new Thickness(12, 0),
            FontSize = 16,
            FontWeight = FontWeight.Bold,
            IsVisible = false,
        };
        _workingCopy.Click += (_, _) => _repo?.State.SetSelectedCommit(null);

        // Two-mode top bar (34 px tall): over staging/conflict pane, displays the branch
        // name and ahead/behind count; over commit detail, displays the full-width
        // Working Copy return button (highlighted primary when tracked changes exist).
        _topBar = new Panel
        {
            Height = 30 + 2 * SectionFrame.HeaderGap,
        };
        _topBar.Children.Add(_branchBar);
        _topBar.Children.Add(_workingCopy);

        _networkRunner.ProgressChanged += OnNetworkProgressChanged;

        _opBanner.TextWrapping = TextWrapping.Wrap;
        _opOpenResolverBtn = TextButton("Open resolver", () => _repo?.State.SetConflictResolverClosed(false));
        _opOpenResolverBtn.IsVisible = false;
        _opContinueBtn = TextButton("Continue", () => _ = OnOpBannerContinueAsync(), primary: true);
        _opAbortBtn = TextButton("Abort", () => _ = OnOpBannerAbortAsync());
        _opActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        _opActions.Children.Add(_opOpenResolverBtn);
        _opActions.Children.Add(_opContinueBtn);
        _opActions.Children.Add(_opAbortBtn);

        var opGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        opGrid.Children.Add(_opBanner);
        Grid.SetColumn(_opActions, 1);
        opGrid.Children.Add(_opActions);

        _opBox = new Border { Child = opGrid, IsVisible = false, Padding = new Thickness(12, 6), BorderThickness = new Thickness(0, 0, 0, 1) }
            .Dyn(Border.BackgroundProperty, "ColorSurface2").Dyn(Border.BorderBrushProperty, "ColorBorder");

        _netProgressBox.IsVisible = false;
        _netProgressBox.Padding = new Thickness(12, 6);
        _netProgressBox.BorderThickness = new Thickness(0, 0, 0, 1);
        _netProgressBox.Dyn(Border.BackgroundProperty, "ColorSurface").Dyn(Border.BorderBrushProperty, "ColorBorder");
        var pg = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        pg.Children.Add(_netProgress);
        pg.Children.Add(TextButton("Cancel", () =>
        {
            if (_repo is not null)
                _networkRunner.Cancel(_repo);
        }));
        _netProgressBox.Child = pg;

        var root = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(_topBar, Dock.Top);
        DockPanel.SetDock(_bannerHost, Dock.Top);
        DockPanel.SetDock(_opBox, Dock.Top);
        DockPanel.SetDock(_netProgressBox, Dock.Top);
        root.Children.Add(_topBar);
        root.Children.Add(_bannerHost);
        root.Children.Add(_opBox);
        root.Children.Add(_netProgressBox);
        root.Children.Add(_body);
        Content = root;
        this.Dyn(BackgroundProperty, "ColorChrome");

        _workspace.FocusedRepositoryChanged += OnFocusChanged;
        Bind(_workspace.Focused);
    }

    private void OnFocusChanged(object? s, FocusedRepositoryChangedEventArgs e)
        => Avalonia.Threading.Dispatcher.UIThread.Post(() => Bind(e.New));

    private void Bind(RepositoryContext? ctx)
    {
        if (_repo is not null)
        {
            _repo.State.StatusChanged -= OnStateChanged;
            _repo.State.ConflictResolverClosedChanged -= OnResolverClosedChanged;
            _repo.State.SelectedCommitChanged -= OnSelChanged;
            _repo.State.RefsChanged -= OnStateChanged2;
            _stagingPanel?.Dispose();
            _stagingPanel = null;
            _detailPanel?.Dispose();
            _detailPanel = null;
        }
        _repo = ctx;
        if (_repo is not null)
        {
            _repo.State.StatusChanged += OnStateChanged;
            _repo.State.ConflictResolverClosedChanged += OnResolverClosedChanged;
            _repo.State.SelectedCommitChanged += OnSelChanged;
            _repo.State.RefsChanged += OnStateChanged2;
        }
        (_bannerHost.Content as IDisposable)?.Dispose();
        _bannerHost.Content = null;
        ShowNetworkProgress(_repo is null ? null : _networkRunner.CurrentProgress(_repo));
        Refresh();
    }

    private void ShowNetworkProgress(GitProgress? p)
    {
        if (p is null)
        {
            _netProgressBox.IsVisible = false;
            return;
        }
        _netProgress.Text = $"{p.Phase} {(p.Percent is { } pct ? $"{pct:0}%" : "")}".Trim();
        _netProgressBox.IsVisible = true;
    }

    private void OnStateChanged(object? s, StatusChangedEventArgs e) => Avalonia.Threading.Dispatcher.UIThread.Post(Refresh);
    private void OnResolverClosedChanged(object? s, EventArgs e) => Avalonia.Threading.Dispatcher.UIThread.Post(Refresh);
    private void OnStateChanged2(object? s, RefsChangedEventArgs e) => Avalonia.Threading.Dispatcher.UIThread.Post(Refresh);
    private void OnSelChanged(object? s, SelectionChangedEventArgs e) => Avalonia.Threading.Dispatcher.UIThread.Post(Refresh);

    internal static int WorkingCopyChangeCount(RepositoryStatus s)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in s.Staged) set.Add(f.Path);
        foreach (var f in s.Unstaged) set.Add(f.Path);
        foreach (var f in s.Untracked) set.Add(f.Path);
        foreach (var f in s.Conflicted) set.Add(f.Path);
        return set.Count;
    }

    private void Refresh()
    {
        if (_repo is null)
        {
            _branch.Text = "No repository open";
            _ab.Text = "";
            _body.Content = null;
            _branchBar.IsVisible = true;
            _workingCopy.IsVisible = false;
            _opBox.IsVisible = false;
            return;
        }

        var info = _repo.State.Repository;
        _branch.Text = info.IsHeadDetached ? $"HEAD detached {info.CurrentBranchName}"
            : info.IsHeadUnborn ? "Unborn HEAD — make the first commit"
            : info.CurrentBranchName;
        var st = _repo.State.Status;
        _ab.Text = (st.AheadBy > 0 ? $"↑{st.AheadBy} " : "") + (st.BehindBy > 0 ? $"↓{st.BehindBy}" : "");

        var op = _repo.State.PendingOperation;
        bool sessionActive = _repo.State.IsConflictSessionActive;
        bool resolverClosed = sessionActive && _repo.State.IsConflictResolverClosed;
        if (op != OperationState.None && (!sessionActive || resolverClosed))
        {
            _opBanner.Text = resolverClosed
                ? $"{op} in progress — the conflict resolver is closed."
                : $"Repository is in {op} state with no conflicts.";
            _opOpenResolverBtn.IsVisible = resolverClosed;
            bool canContinue = op is OperationState.Merging or OperationState.Rebasing or OperationState.CherryPicking or OperationState.Reverting;
            _opActions.IsVisible = canContinue;
            _opBox.IsVisible = true;
        }
        else
        {
            _opBox.IsVisible = false;
        }

        if (_repo.State.SelectedCommitId is { } id)
        {
            if (_detailPanel is null)
                _detailPanel = new CommitDetailPanelView(_sp, _repo, id);
            else
                _detailPanel.SetCommit(id);

            if (!ReferenceEquals(_body.Content, _detailPanel))
                _body.Content = _detailPanel;

            var n = WorkingCopyChangeCount(st);
            _workingCopyText.Text = $"← Working Copy (Changes: {n})";
            if (n > 0)
            {
                _workingCopy.Classes.Remove("flat");
                _workingCopy.Classes.Add("primary");
                _workingCopy.ClearValue(Button.ForegroundProperty);
            }
            else
            {
                _workingCopy.Classes.Remove("primary");
                _workingCopy.Classes.Add("flat");
                _workingCopy.Dyn(Button.ForegroundProperty, "ColorText");
            }

            _workingCopy.IsVisible = true;
            _branchBar.IsVisible = false;
        }
        else
        {
            _stagingPanel ??= new StagingPanelView(_sp, _repo);
            if (!ReferenceEquals(_body.Content, _stagingPanel))
                _body.Content = _stagingPanel;

            _workingCopy.IsVisible = false;
            _branchBar.IsVisible = true;
        }
    }

    private async Task OnOpBannerContinueAsync()
    {
        if (_repo == null || _opBusy) return;
        _opBusy = true;
        _opContinueBtn.IsEnabled = _opAbortBtn.IsEnabled = false;
        try
        {
            var conflicts = _sp.GetRequiredService<IConflictService>();
            await conflicts.ContinueOperationAsync(_repo);
        }
        catch (HistoriaGitException ex) when (ex.Kind == GitErrorKind.MergeConflict)
        {
            _notifications.Publish(NotificationSeverity.Warning, ex.Message);
        }
        catch (Exception ex)
        {
            _notifications.Publish(NotificationSeverity.Error, ex.NotificationMessage("Continue"), ex.NotificationDetails());
        }
        finally
        {
            _opBusy = false;
            _opContinueBtn.IsEnabled = _opAbortBtn.IsEnabled = true;
        }
    }

    private async Task OnOpBannerAbortAsync()
    {
        if (_repo == null || _opBusy) return;
        if (!await _dialogs.ConfirmDestructiveAsync(
                "Abort operation",
                "Abort the in-progress merge / rebase / cherry-pick? Conflict resolutions made so far will be discarded.",
                "Abort"))
        {
            return;
        }

        if (_opBusy) return;
        _opBusy = true;
        _opContinueBtn.IsEnabled = _opAbortBtn.IsEnabled = false;
        try
        {
            var conflicts = _sp.GetRequiredService<IConflictService>();
            await conflicts.AbortOperationAsync(_repo);
        }
        catch (Exception ex)
        {
            _notifications.Publish(NotificationSeverity.Error, ex.NotificationMessage("Abort"), ex.NotificationDetails());
        }
        finally
        {
            _opBusy = false;
            _opContinueBtn.IsEnabled = _opAbortBtn.IsEnabled = true;
        }
    }

    // Mirrors IRepoNetworkActionRunner's progress into this view's inline box, but only while
    // it's reporting for the repo this view is currently showing — the runner itself keeps
    // running Fetch/Pull/Push regardless of which view (if any) is mounted.
    private void OnNetworkProgressChanged(object? sender, Historia.Services.UI.RepoNetworkProgress e)
        => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (_repo is null || !ReferenceEquals(e.Repo, _repo))
                return;
            ShowNetworkProgress(e.Progress);
        });

    public void Dispose()
    {
        _workspace.FocusedRepositoryChanged -= OnFocusChanged;
        _networkRunner.ProgressChanged -= OnNetworkProgressChanged;
        Bind(null);
    }
}
