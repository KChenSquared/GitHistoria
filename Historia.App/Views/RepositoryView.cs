namespace Historia.Views;

/// <summary>
/// The repository detail pane (V4 port of Repository.razor). A toolbar with the current branch
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

    private readonly TextBlock _branch = Text("", "ColorText", 13, FontWeight.Bold);
    private readonly TextBlock _ab = Text("", "ColorTextMuted", 12);
    private readonly ContentControl _body = new();
    private readonly ContentControl _bannerHost = new();
    private readonly TextBlock _opBanner = Text("", "ColorText", 12);
    private readonly Border _opBox;
    private readonly TextBlock _netProgress = Text("", "ColorTextMuted", 12);
    private readonly Border _netProgressBox = new();
    private readonly Historia.Services.UI.IRepoNetworkActionRunner _networkRunner;
    private RepositoryContext? _repo;
    private StagingPanelView? _stagingPanel;
    private CommitDetailPanelView? _detailPanel;
    private ConflictResolverView? _conflictPanel;

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
        _branch.FontSize = Math.Clamp(_settings.Current.CommitDetailFontSize, 10, 24) + 1;

        // v8 M5: Fetch / Pull / Push, the network-mode button and the repo-terminal button moved
        // to the window-wide shortcuts bar. This toolbar keeps only the branch name + ahead/behind;
        // the network flow itself runs in the app-lifetime IRepoNetworkActionRunner (so it still
        // works when this view isn't the mounted one) — this view just mirrors its progress into
        // the inline box below while it's showing the repo that's running.
        var toolbar = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*"), Height = 30, Margin = new Thickness(12, 6) };
        toolbar.Children.Add(_branch);
        Grid.SetColumn(_ab, 1);
        _ab.Margin = new Thickness(8, 0, 0, 0);
        toolbar.Children.Add(_ab);

        _networkRunner.ProgressChanged += OnNetworkProgressChanged;

        _opBanner.TextWrapping = TextWrapping.Wrap;
        _opBox = new Border { Child = _opBanner, IsVisible = false, Padding = new Thickness(12, 6), BorderThickness = new Thickness(0, 0, 0, 1) }
            .Dyn(Border.BackgroundProperty, "ColorSurface2").Dyn(Border.BorderBrushProperty, "ColorBorder");

        _netProgressBox.IsVisible = false;
        _netProgressBox.Padding = new Thickness(12, 6);
        _netProgressBox.BorderThickness = new Thickness(0, 0, 0, 1);
        _netProgressBox.Dyn(Border.BackgroundProperty, "ColorSurface").Dyn(Border.BorderBrushProperty, "ColorBorder");
        var pg = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        pg.Children.Add(_netProgress);
        pg.Children.Add(TextButton("Cancel", () => _networkRunner.CancelCurrent()));
        _netProgressBox.Child = pg;

        var root = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(toolbar, Dock.Top);
        DockPanel.SetDock(_bannerHost, Dock.Top);
        DockPanel.SetDock(_opBox, Dock.Top);
        DockPanel.SetDock(_netProgressBox, Dock.Top);
        root.Children.Add(toolbar);
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
            _repo.State.SelectedCommitChanged -= OnSelChanged;
            _repo.State.RefsChanged -= OnStateChanged2;
            _stagingPanel?.Dispose();
            _stagingPanel = null;
            _detailPanel?.Dispose();
            _detailPanel = null;
            _conflictPanel?.Dispose();
            _conflictPanel = null;
        }
        _repo = ctx;
        if (_repo is not null)
        {
            _repo.State.StatusChanged += OnStateChanged;
            _repo.State.SelectedCommitChanged += OnSelChanged;
            _repo.State.RefsChanged += OnStateChanged2;
        }
        (_bannerHost.Content as IDisposable)?.Dispose();
        _bannerHost.Content = null;
        Refresh();
    }

    private void OnStateChanged(object? s, StatusChangedEventArgs e) => Avalonia.Threading.Dispatcher.UIThread.Post(Refresh);
    private void OnStateChanged2(object? s, RefsChangedEventArgs e) => Avalonia.Threading.Dispatcher.UIThread.Post(Refresh);
    private void OnSelChanged(object? s, SelectionChangedEventArgs e) => Avalonia.Threading.Dispatcher.UIThread.Post(Refresh);

    private void Refresh()
    {
        if (_repo is null)
        {
            _branch.Text = "No repository open";
            _ab.Text = "";
            _body.Content = null;
            return;
        }

        var info = _repo.State.Repository;
        _branch.Text = info.IsHeadDetached ? $"HEAD detached {info.CurrentBranchName}"
            : info.IsHeadUnborn ? "Unborn HEAD — make the first commit"
            : info.CurrentBranchName;
        var st = _repo.State.Status;
        _ab.Text = (st.AheadBy > 0 ? $"↑{st.AheadBy} " : "") + (st.BehindBy > 0 ? $"↓{st.BehindBy}" : "");

        // Operation-state banner (mid rebase/cherry-pick/merge with no conflicts).
        var op = _repo.State.PendingOperation;
        if (op != OperationState.None && st.Conflicted.Count == 0)
        {
            _opBanner.Text = $"Repository is in {op} state with no conflicts — continue it from a terminal, or it may have completed.";
            _opBox.IsVisible = true;
        }
        else
        {
            _opBox.IsVisible = false;
        }

        if (op != OperationState.None && st.Conflicted.Count > 0)
        {
            _conflictPanel ??= new ConflictResolverView(_sp, _repo);
            if (!ReferenceEquals(_body.Content, _conflictPanel))
                _body.Content = _conflictPanel;
        }
        else
        {
            if (_conflictPanel is not null)
            {
                _conflictPanel.Dispose();
                _conflictPanel = null;
            }

            if (_repo.State.SelectedCommitId is { } id)
            {
                if (_detailPanel is null)
                    _detailPanel = new CommitDetailPanelView(_sp, _repo, id);
                else
                    _detailPanel.SetCommit(id);

                if (!ReferenceEquals(_body.Content, _detailPanel))
                    _body.Content = _detailPanel;
            }
            else
            {
                _stagingPanel ??= new StagingPanelView(_sp, _repo);
                if (!ReferenceEquals(_body.Content, _stagingPanel))
                    _body.Content = _stagingPanel;
            }
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
            if (e.Progress is not { } p)
            {
                _netProgressBox.IsVisible = false;
                return;
            }
            _netProgress.Text = $"{p.Phase} {(p.Percent is { } pct ? $"{pct:0}%" : "")}".Trim();
            _netProgressBox.IsVisible = true;
        });

    public void Dispose()
    {
        _workspace.FocusedRepositoryChanged -= OnFocusChanged;
        _networkRunner.ProgressChanged -= OnNetworkProgressChanged;
        Bind(null);
    }
}
