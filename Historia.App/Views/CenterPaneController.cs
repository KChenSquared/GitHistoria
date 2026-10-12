namespace Historia.Views;

/// <summary>
/// Controls the content displayed in the center panel of the main window. A
/// <see cref="CenterPaneViewModel"/> decides the mode (AI Integrations window while it is open; else
/// the open diff; else the conflict resolver; else the commit graph); this class maps that mode to
/// the long-lived instances: the graph control, the cached resolver of each repository, and the diff
/// and AI views it creates and disposes. A closed resolver stays cached with its edits, so reopening
/// it shows the same view.
/// </summary>
public sealed class CenterPaneController : IDisposable
{
    private readonly ContentControl _centerHost;
    private readonly Control _graph;
    private readonly ConflictResolverHost _conflictHost;
    private readonly IServiceProvider _services;
    private readonly CenterPaneViewModel _viewModel;
    private AiIntegrationsView? _aiView;
    private DiffRequest? _activeDiffRequest;
    private DiffView? _activeDiffView;
    private bool _disposed;

    internal DiffView? ActiveDiffViewForTesting => _activeDiffView;
    internal AiIntegrationsView? ActiveAiViewForTesting => _aiView;

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
        _services = services ?? throw new ArgumentNullException(nameof(services));

        _viewModel = new CenterPaneViewModel(
            workspace,
            services.GetService<IAiIntegrationsPanel>(),
            services.GetService<IUiDispatcher>() ?? new UiDispatcher(),
            ctx => _conflictHost.TryGet(ctx)?.HasUnsavedWork == true);
        _viewModel.RepositoryLeft += OnRepositoryLeft;
        _viewModel.Changed += OnViewModelChanged;
        ApplyContent(refocusDiffLine: false);
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

    public void Bind(RepositoryContext? ctx)
    {
        if (_disposed)
        {
            return;
        }

        _viewModel.Bind(ctx);
    }

    public void UpdateContent()
    {
        if (_disposed)
        {
            return;
        }

        _viewModel.Refresh();
    }

    private void OnRepositoryLeft(object? sender, RepositoryContext previous) => ReleaseResolverIfIdle(previous);

    private void OnViewModelChanged(object? sender, CenterPaneChangedEventArgs e) => ApplyContent(e.RefocusDiffLine);

    // A resolver is kept for a repository only while its session is active or it holds unsaved edits.
    private void ReleaseResolverIfIdle(RepositoryContext ctx)
    {
        var existing = _conflictHost.TryGet(ctx);
        if (!ctx.State.IsConflictSessionActive && existing?.HasUnsavedWork != true)
        {
            _conflictHost.Release(ctx);
        }
    }

    private void ApplyContent(bool refocusDiffLine)
    {
        if (_disposed)
        {
            return;
        }

        var ctx = _viewModel.ActiveRepo;
        switch (_viewModel.Mode)
        {
            case CenterPaneMode.AiIntegrations:
                // A diff is re-created from OpenDiff when the window closes; a resolver keeps its
                // own cached view, so its edits are untouched.
                DisposeActiveDiffView();
                _aiView ??= new AiIntegrationsView(_services);
                if (!ReferenceEquals(_centerHost.Content, _aiView))
                {
                    _centerHost.Content = _aiView;
                }
                return;

            case CenterPaneMode.Diff when ctx is not null && _viewModel.OpenDiff is { } openDiff:
                DisposeAiView();
                ShowDiff(ctx, openDiff);
                if (refocusDiffLine)
                {
                    _activeDiffView?.RefocusLine();
                }
                return;

            case CenterPaneMode.Conflict when ctx is not null:
                DisposeAiView();
                DisposeActiveDiffView();
                var resolver = _conflictHost.GetOrCreate(ctx);
                // A null resolver means the tab has closed; there is nothing to resolve in it.
                ShowCentre(resolver ?? _graph);
                return;
        }

        DisposeAiView();
        DisposeActiveDiffView();
        if (ctx is not null && !ctx.State.IsConflictSessionActive && _conflictHost.TryGet(ctx) is not null)
        {
            _conflictHost.Release(ctx);
        }

        ShowCentre(_graph);
    }

    private void ShowDiff(RepositoryContext ctx, DiffRequest openDiff)
    {
        if (_activeDiffView != null && _activeDiffRequest == openDiff)
        {
            return;
        }

        DisposeActiveDiffView();
        _activeDiffRequest = openDiff;
        _activeDiffView = new DiffView(ctx, openDiff, _services.GetService<ISettingsService>(), _services);
        if (_viewModel.ClaimFocusLine(openDiff))
        {
            _activeDiffView.SkipInitialFocusLine();
        }

        _centerHost.Content = _activeDiffView;
    }

    private void ShowCentre(Control content)
    {
        if (!ReferenceEquals(_centerHost.Content, content))
        {
            _centerHost.Content = content;
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

        _viewModel.RepositoryLeft -= OnRepositoryLeft;
        _viewModel.Changed -= OnViewModelChanged;
        if (_viewModel.ActiveRepo is { } active)
        {
            ReleaseResolverIfIdle(active);
        }
        _viewModel.Dispose();

        DisposeActiveDiffView();
        DisposeAiView();
    }
}
