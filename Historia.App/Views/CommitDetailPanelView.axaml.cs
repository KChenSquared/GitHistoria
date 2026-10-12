namespace Historia.Views;

/// <summary>
/// Selected-commit detail: metadata, parent commits, full raw commit message, and changed files.
/// The markup is bound to a <see cref="CommitDetailViewModel"/>; the code here builds the message's
/// inlines (links need runs) and turns pointer presses on rows, parents and the hash into commands.
/// The working copy return button resides in the repository top bar.
/// </summary>
public sealed partial class CommitDetailPanelView : UserControl, IDisposable
{
    public CommitDetailPanelView(IServiceProvider sp, RepositoryContext ctx, GitObjectId id)
    {
        InitializeComponent();
        ViewModel = ActivatorUtilities.CreateInstance<CommitDetailViewModel>(
            sp, ctx.Gateway, ctx.State, ctx.Id, id, new UiDispatcher(), new ClipboardService());
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        DataContext = ViewModel;
        ApplyMessage();
    }

    internal CommitDetailViewModel ViewModel { get; }

    /// <summary>The split between the header section and the files section.</summary>
    internal SectionFrameHost Split => SplitHost;

    public void SetCommit(GitObjectId id) => ViewModel.SetCommit(id);

    public void Dispose()
    {
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        ViewModel.Dispose();
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CommitDetailViewModel.MessageRuns))
            ApplyMessage();
    }

    // Plain runs, and underlined accent runs for the links the autolink rules found.
    private void ApplyMessage()
    {
        var inlines = MessageText.Inlines!;
        inlines.Clear();
        foreach (var run in ViewModel.MessageRuns)
        {
            var inline = new Run(run.Text);
            if (run.Url is not null)
            {
                inline.TextDecorations = TextDecorations.Underline;
                inline.Bind(TextElement.ForegroundProperty, new DynamicResourceExtension("ColorAccent"));
            }
            inlines.Add(inline);
        }
    }

    // Runs take no pointer input, so a press anywhere in the message opens every link in it.
    private void OnMessagePressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(MessageText).Properties.IsLeftButtonPressed)
            return;

        foreach (var run in ViewModel.MessageRuns)
        {
            if (run.Url is not null)
                ViewModel.OpenUrlCommand.Execute(run.Url);
        }
    }

    private void OnHashPressed(object? sender, PointerPressedEventArgs e) => ViewModel.CopyHashCommand.Execute(null);

    private void OnParentPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: CommitParentViewModel parent } control
            || !e.GetCurrentPoint(control).Properties.IsLeftButtonPressed)
            return;

        e.Handled = true;
        ViewModel.GoToParentCommand.Execute(parent);
    }

    private void OnRowPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control { DataContext: CommitRowViewModel row })
            row.ActivateCommand.Execute(null);
    }
}
