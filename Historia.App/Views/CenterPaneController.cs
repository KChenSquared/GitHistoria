namespace Historia.Views;

/// <summary>
/// Controls the content displayed in the center panel of the main window.
/// Manages content precedence: AI Integrations window while it is open; else OpenDiff -> DiffView; else conflict
/// session the user has not closed, or unsaved resolver work after the session ended -> ConflictResolverView; else
/// CommitGraphControl. A new diff or a selected conflict path closes the AI window so the click is not lost behind it.
/// Ensures proper lifecycle management, view disposal, and resolver session state retention. A closed resolver
/// stays cached with its edits, so reopening it shows the same view.
/// </summary>
public sealed class CenterPaneController : IDisposable
{
    private readonly ContentControl _centerHost;
    private readonly Control _graph;
    private readonly ConflictResolverHost _conflictHost;
    private readonly IWorkspaceService? _workspace;
    private readonly IServiceProvider _services;
    private readonly IAiIntegrationsPanel? _aiPanel;
    private AiIntegrationsView? _aiView;
    private RepositoryContext? _activeCtx;
    private DiffRequest? _activeDiffRequest;
    private DiffView? _activeDiffView;
    private bool _disposed;

    internal DiffView? ActiveDiffViewForTesting => _activeDiffView;
    internal AiIntegrationsView? ActiveAiViewForTesting => _aiView;
    internal RepositoryContext? ActiveContextForTesting => _activeCtx;

    public CenterPaneController(
        ContentControl centerHost,
        Control graph,
        ConflictResolverHost conflictHost,
        IWorkspaceService? workspace,
        IServiceProvider services)
    {
        _centerHost = centerHost ?? throw new ArgumentNullException(nameof(centerHost));
        _graph = graph ?? throw new ArgumentNullException(nameof(graph));
        _conflictHost = conflictHost ?? throw new ArgumentNullException(nameof(conflictHost));
        _workspace = workspace;
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _aiPanel = services.GetService<IAiIntegrationsPanel>();
        if (_aiPanel != null)
        {
            _aiPanel.OpenChanged += OnAiPanelChanged;
        }

        if (_workspace != null)
        {
            _workspace.FocusedRepositoryChanged += OnFocusedRepositoryChanged;
            Bind(_workspace.Focused);
        }
        else
        {
            UpdateContent();
        }
    }

    public CenterPaneController(
        ContentControl centerHost,
        Control graph,
        IServiceProvider services)
        : this(
            centerHost,
            graph,
            services.GetService(typeof(ConflictResolverHost)) as ConflictResolverHost ?? new ConflictResolverHost(services),
            services.GetService(typeof(IWorkspaceService)) as IWorkspaceService,
            services)
    {
    }

    private void OnFocusedRepositoryChanged(object? sender, FocusedRepositoryChangedEventArgs e)
    {
        Dispatcher.UIThread.Post(() => Bind(e.New));
    }

    public void Bind(RepositoryContext? ctx)
    {
        if (_disposed)
        {
            return;
        }

        if (ReferenceEquals(ctx, _activeCtx))
        {
            UpdateContent();
            return;
        }

        if (_activeCtx != null)
        {
            _activeCtx.State.OpenDiffChanged -= OnOpenDiffChanged;
            _activeCtx.State.StatusChanged -= OnStatusChanged;
            _activeCtx.State.ConflictSessionChanged -= OnConflictSessionChanged;
            _activeCtx.State.ConflictResolverClosedChanged -= OnConflictResolverClosedChanged;
            _activeCtx.State.SelectedConflictPathChanged -= OnSelectedConflictPathChanged;

            var existing = _conflictHost.TryGet(_activeCtx);
            if (!_activeCtx.State.IsConflictSessionActive && existing?.HasUnsavedWork != true)
            {
                _conflictHost.Release(_activeCtx);
            }
        }

        _activeCtx = ctx;

        if (_activeCtx != null)
        {
            _activeCtx.State.OpenDiffChanged += OnOpenDiffChanged;
            _activeCtx.State.StatusChanged += OnStatusChanged;
            _activeCtx.State.ConflictSessionChanged += OnConflictSessionChanged;
            _activeCtx.State.ConflictResolverClosedChanged += OnConflictResolverClosedChanged;
            _activeCtx.State.SelectedConflictPathChanged += OnSelectedConflictPathChanged;
        }

        UpdateContent();
    }

    private void OnAiPanelChanged(object? sender, EventArgs e)
        => Dispatcher.UIThread.Post(UpdateContent);

    private void OnOpenDiffChanged(object? sender, OpenDiffChangedEventArgs e)
        => Dispatcher.UIThread.Post(() =>
        {
            if (_activeCtx?.State.OpenDiff != null)
            {
                _aiPanel?.Close();
            }
            UpdateContent();
        });

    private void OnSelectedConflictPathChanged(object? sender, SelectedConflictPathChangedEventArgs e)
        => Dispatcher.UIThread.Post(() =>
        {
            if (e.Path != null)
            {
                _aiPanel?.Close();
            }
            UpdateContent();
        });

    private void OnStatusChanged(object? sender, StatusChangedEventArgs e)
        => Dispatcher.UIThread.Post(UpdateContent);

    private void OnConflictSessionChanged(object? sender, EventArgs e)
        => Dispatcher.UIThread.Post(UpdateContent);

    private void OnConflictResolverClosedChanged(object? sender, EventArgs e)
        => Dispatcher.UIThread.Post(UpdateContent);

    public void UpdateContent()
    {
        if (_disposed)
        {
            return;
        }

        if (_aiPanel?.IsOpen == true)
        {
            // A diff is re-created from OpenDiff when the window closes; a resolver keeps its
            // own cached view, so its edits are untouched.
            DisposeActiveDiffView();
            _aiView ??= new AiIntegrationsView(_services);
            if (!ReferenceEquals(_centerHost.Content, _aiView))
            {
                _centerHost.Content = _aiView;
            }
            return;
        }

        DisposeAiView();

        if (_activeCtx == null)
        {
            DisposeActiveDiffView();
            _centerHost.Content = _graph;
            return;
        }

        var openDiff = _activeCtx.State.OpenDiff;
        if (openDiff != null)
        {
            if (_activeDiffView == null || _activeDiffRequest != openDiff)
            {
                DisposeActiveDiffView();
                _activeDiffRequest = openDiff;
                _activeDiffView = new DiffView(_activeCtx, openDiff, _services.GetService<ISettingsService>(), _services);
                _centerHost.Content = _activeDiffView;
            }
            return;
        }

        DisposeActiveDiffView();

        var existing = _conflictHost.TryGet(_activeCtx);
        bool isSessionActive = _activeCtx.State.IsConflictSessionActive;
        bool hasUnsaved = existing?.HasUnsavedWork == true;

        // Only a live session can be closed: with no conflicts or operation left, nothing would
        // bring back a hidden resolver holding edits.
        if (isSessionActive && _activeCtx.State.IsConflictResolverClosed)
        {
            if (!ReferenceEquals(_centerHost.Content, _graph))
            {
                _centerHost.Content = _graph;
            }
        }
        else if (isSessionActive || hasUnsaved)
        {
            var resolver = _conflictHost.GetOrCreate(_activeCtx);
            if (resolver is null)
            {
                // The tab has closed; there is nothing to resolve in it.
                if (!ReferenceEquals(_centerHost.Content, _graph))
                {
                    _centerHost.Content = _graph;
                }
            }
            else if (!ReferenceEquals(_centerHost.Content, resolver))
            {
                _centerHost.Content = resolver;
            }
        }
        else
        {
            if (existing != null)
            {
                _conflictHost.Release(_activeCtx);
            }
            if (!ReferenceEquals(_centerHost.Content, _graph))
            {
                _centerHost.Content = _graph;
            }
        }
    }

    private void DisposeAiView()
    {
        if (_aiView == null)
        {
            return;
        }

        if (ReferenceEquals(_centerHost.Content, _aiView))
        {
            _centerHost.Content = null;
        }
        _aiView.Dispose();
        _aiView = null;
    }

    private void DisposeActiveDiffView()
    {
        _activeDiffRequest = null;
        if (_activeDiffView != null)
        {
            if (ReferenceEquals(_centerHost.Content, _activeDiffView))
            {
                _centerHost.Content = null;
            }
            _activeDiffView.Dispose();
            _activeDiffView = null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_aiPanel != null)
        {
            _aiPanel.OpenChanged -= OnAiPanelChanged;
        }

        if (_workspace != null)
        {
            _workspace.FocusedRepositoryChanged -= OnFocusedRepositoryChanged;
        }

        if (_activeCtx != null)
        {
            _activeCtx.State.OpenDiffChanged -= OnOpenDiffChanged;
            _activeCtx.State.StatusChanged -= OnStatusChanged;
            _activeCtx.State.ConflictSessionChanged -= OnConflictSessionChanged;
            _activeCtx.State.ConflictResolverClosedChanged -= OnConflictResolverClosedChanged;
            _activeCtx.State.SelectedConflictPathChanged -= OnSelectedConflictPathChanged;

            var existing = _conflictHost.TryGet(_activeCtx);
            if (!_activeCtx.State.IsConflictSessionActive && existing?.HasUnsavedWork != true)
            {
                _conflictHost.Release(_activeCtx);
            }
            _activeCtx = null;
        }

        DisposeActiveDiffView();
        DisposeAiView();
    }
}
