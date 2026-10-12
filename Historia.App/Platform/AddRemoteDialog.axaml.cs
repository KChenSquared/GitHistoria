namespace Historia.Platform;

/// <summary>
/// Modal "Add remote" window: a name, a fetch URL and a "Fetch after adding" checkbox (on by
/// default). Validation is shared with the Edit remote dialog; Add stays disabled until the
/// input is valid. Cancel is always the rightmost button.
/// </summary>
internal sealed partial class AddRemoteDialog : Window
{
    private readonly TaskCompletionSource<AddRemoteResult?> _closed = new();

    internal AddRemoteDialog(IReadOnlyList<RemoteInfo> existing)
    {
        InitializeComponent();
        ViewModel = new AddRemoteDialogViewModel(existing);
        DataContext = ViewModel;
        ViewModel.CloseRequested += (_, _) => Close();
        Opened += (_, _) => (ViewModel.FocusUrlFirst ? UrlBox : NameBox).Focus();
        Closed += (_, _) =>
        {
            _closed.TrySetResult(ViewModel.Result);
            ViewModel.Dispose();
        };
    }

    internal AddRemoteDialogViewModel ViewModel { get; }

    /// <summary>Completes with the entered values once the window closes, or null when it was cancelled.</summary>
    internal Task<AddRemoteResult?> Completion => _closed.Task;

    /// <summary>Shows the dialog. <paramref name="existing"/> are the repository's remotes: their
    /// names and URLs may not be reused, and the name starts as <c>origin</c> when there are none.
    /// Returns <see langword="null"/> when the user cancels.</summary>
    public static async Task<AddRemoteResult?> ShowAsync(IReadOnlyList<RemoteInfo> existing)
    {
        if (!Dispatcher.UIThread.CheckAccess())
            return await Dispatcher.UIThread.InvokeAsync(() => ShowAsync(existing));

        var dialog = new AddRemoteDialog(existing);
        await SimpleDialog.ShowOwnedOrFree(dialog, AvaloniaDialogService.Owner);
        return await dialog.Completion;
    }
}
