namespace Historia.Views;

/// <summary>
/// The repository detail pane. A toolbar with the current branch
/// and Fetch / Pull / Push, then either the selected-commit detail or the working-copy staging
/// panel. The markup is bound to a <see cref="RepositoryViewModel"/>; the code here builds the
/// staging and detail panels for the repository and keeps the body on the one the view model
/// names. The commit graph itself is the native <see cref="Controls.CommitGraphControl"/> in
/// the centre column.
/// </summary>
public sealed partial class RepositoryView : UserControl, IDisposable
{
    private readonly IServiceProvider _sp;
    private RepositoryContext? _panelsRepo;
    private StagingPanelView? _stagingPanel;
    private CommitDetailPanelView? _detailPanel;

    internal Panel TopBarForTesting => TopBar;
    internal Grid BranchBarForTesting => BranchBar;
    internal Button WorkingCopyButtonForTesting => WorkingCopyButton;
    internal TextBlock BranchTextBlockForTesting => BranchLabel;
    internal TextBlock WorkingCopyTextBlockForTesting => WorkingCopyLabel;
    internal Border OpBoxForTesting => OpBox;
    internal Button OpContinueButtonForTesting => OpContinueButton;
    internal TextBlock OpBannerTextBlockForTesting => OpBannerLabel;
    internal Button OpAbortButtonForTesting => OpAbortButton;

    public RepositoryView(IServiceProvider sp)
    {
        _sp = sp;
        InitializeComponent();
        ViewModel = ActivatorUtilities.CreateInstance<RepositoryViewModel>(sp, new UiDispatcher());
        ViewModel.DetailChanged += OnDetailChanged;
        DataContext = ViewModel;
        ApplyDetail();
    }

    internal RepositoryViewModel ViewModel { get; }

    private void OnDetailChanged(object? sender, EventArgs e) => ApplyDetail();

    // The panels belong to one repository; showing another disposes them.
    private void ApplyDetail()
    {
        var repo = ViewModel.Repo;
        if (!ReferenceEquals(repo, _panelsRepo))
        {
            DisposePanels();
            _panelsRepo = repo;
        }

        switch (ViewModel.DetailMode)
        {
            case RepositoryDetailMode.CommitDetail when repo is not null && ViewModel.SelectedCommitId is { } id:
                if (_detailPanel is null)
                    _detailPanel = new CommitDetailPanelView(_sp, repo, id);
                else
                    _detailPanel.SetCommit(id);

                if (!ReferenceEquals(Body.Content, _detailPanel))
                    Body.Content = _detailPanel;
                break;

            case RepositoryDetailMode.Staging when repo is not null:
                _stagingPanel ??= new StagingPanelView(_sp, repo);
                if (!ReferenceEquals(Body.Content, _stagingPanel))
                    Body.Content = _stagingPanel;
                break;

            default:
                Body.Content = null;
                break;
        }
    }

    private void DisposePanels()
    {
        _stagingPanel?.Dispose();
        _stagingPanel = null;
        _detailPanel?.Dispose();
        _detailPanel = null;
    }

    public void Dispose()
    {
        ViewModel.DetailChanged -= OnDetailChanged;
        ViewModel.Dispose();
        DisposePanels();
        _panelsRepo = null;
        Body.Content = null;
    }
}
