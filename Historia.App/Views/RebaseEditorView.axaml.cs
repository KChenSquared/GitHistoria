namespace Historia.Views;

/// <summary>
/// Interactive-rebase todo editor. Reached via the
/// <c>rebase?onto=&lt;ref&gt;</c> route. Reorder + set an action per commit, then Start. The view is
/// markup bound to a <see cref="RebaseEditorViewModel"/>.
/// </summary>
public sealed partial class RebaseEditorView : UserControl, IDisposable
{
    public RebaseEditorView(IServiceProvider sp, string onto)
    {
        InitializeComponent();
        ViewModel = ActivatorUtilities.CreateInstance<RebaseEditorViewModel>(sp, onto);
        DataContext = ViewModel;
        _ = ViewModel.LoadCommand.ExecuteAsync(null);
    }

    internal RebaseEditorViewModel ViewModel { get; }

    public void Dispose() => ViewModel.Dispose();
}
