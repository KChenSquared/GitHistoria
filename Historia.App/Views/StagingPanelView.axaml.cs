namespace Historia.Views;

/// <summary>
/// Working tree, staging area and commit box. Changes (unstaged) and Staged are the two
/// resizable sections, with a checkbox column on each and a green ⬇ Stage / red ⬆ Unstage
/// arrow pair in the Staged header acting on the checked rows; both the Staged section's
/// top border and the rule under the Staged header resize the split. A downward split drag
/// past Staged's minimum shrinks the commit box down to its minimum. The commit-message strip
/// docks to the bottom; its top edge resizes the message box only, bounded by the box
/// minimum and the two lists' minimums. The markup is bound to a <see cref="StagingPanelViewModel"/>;
/// the code here is the drag and size handling of the splits and the box, and the pointer
/// presses and events that bindings cannot express.
/// </summary>
public sealed partial class StagingPanelView : UserControl, IDisposable
{
    private const double MessageMinHeight = 96;

    public StagingPanelView(IServiceProvider sp, RepositoryContext ctx)
    {
        InitializeComponent();
        ViewModel = ActivatorUtilities.CreateInstance<StagingPanelViewModel>(
            sp, ctx, ViewServices.Dispatcher(sp), ViewServices.Clipboard(sp));
        DataContext = ViewModel;

        MessageInput.Height = SectionFrame.GetBoxHeight(ctx.Id, MessageMinHeight);

        // A split drag past Staged's minimum borrows from the commit box. Only a borrowed height
        // becomes the remembered one; giving all of it back restores the height remembered before
        // the drag, which a short window may have clamped the box below.
        double lendStartHeight = 0;
        double lendStartRemembered = 0;
        SplitHost.Lender = new SectionLender(
            Begin: () =>
            {
                lendStartHeight = MessageInput.Height;
                lendStartRemembered = SectionFrame.GetBoxHeight(ctx.Id, MessageMinHeight);
                return (MessageInput.Height, MessageMinHeight);
            },
            Apply: h =>
            {
                MessageInput.Height = h;
                SectionFrame.SetBoxHeight(ctx.Id, h < lendStartHeight ? h : lendStartRemembered);
            });

        Action<double> applyMessageHeight = h =>
        {
            MessageInput.Height = h;
            SectionFrame.SetBoxHeight(ctx.Id, h);
        };
        SectionFrame.AttachBottomResize(
            MessageResizeHandle,
            LayoutRoot,
            begin: () => (
                MessageInput.Height,
                MessageMinHeight,
                SectionFrame.ClampGrowHeight(double.MaxValue, MessageMinHeight, MessageInput.Height, SplitHost.Bounds.Height, SectionFrame.SplitAreaMinHeight)
            ),
            apply: applyMessageHeight,
            committed: applyMessageHeight);

        AttachedToVisualTree += (_, _) => ApplyMessageHeight();
        SplitHost.SizeChanged += (_, _) => ApplyMessageHeight();
        // A split area that is laid out at zero height raises no SizeChanged of its own.
        LayoutRoot.SizeChanged += (_, _) =>
        {
            if (SplitHost.Bounds.Height <= 0)
                ApplyMessageHeight();
        };
    }

    internal StagingPanelViewModel ViewModel { get; }

    public void Dispose() => ViewModel.Dispose();

    private void ApplyMessageHeight()
    {
        if (LayoutRoot.Bounds.Height <= 0)
            return;

        // The split area gets zero height when the commit strip fills the whole panel; the room
        // it lost is unknown, so the box drops to its minimum and the next split-area resize
        // grows it back to what fits.
        var stored = SectionFrame.GetBoxHeight(ViewModel.RepoId, MessageMinHeight);
        var clamped = SplitHost.Bounds.Height <= 0
            ? MessageMinHeight
            : SectionFrame.ClampGrowHeight(
                stored,
                MessageMinHeight,
                MessageInput.Height,
                SplitHost.Bounds.Height,
                SectionFrame.SplitAreaMinHeight);

        if (Math.Abs(MessageInput.Height - clamped) > 0.5)
            MessageInput.Height = clamped;
    }

    private void OnSelectAllChangesClick(object? sender, RoutedEventArgs e)
        => ViewModel.SelectAllChangesCommand.Execute(ChangesSelectAll.IsChecked == true);

    private void OnSelectAllStagedClick(object? sender, RoutedEventArgs e)
        => ViewModel.SelectAllStagedCommand.Execute(StagedSelectAll.IsChecked == true);

    private void OnModelPickerOpened(object? sender, EventArgs e) => ViewModel.OpenModelPickerCommand.Execute(null);

    private void OnRowPressed(object? sender, PointerPressedEventArgs e)
    {
        // e.Source is the leaf visual under the pointer — a Border/Path inside the CheckBox
        // template, not the CheckBox itself — so walk up before deciding.
        if (e.Source is Visual v && (v is CheckBox || v.FindAncestorOfType<CheckBox>() is not null))
            return; // let the checkbox handle its own click

        if (sender is Control { DataContext: StagingPathRowViewModel row })
            row.ActivateCommand.Execute(null);
    }
}
