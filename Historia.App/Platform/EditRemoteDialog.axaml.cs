namespace Historia.Platform;

/// <summary>
/// Modal "Edit remote" window: the remote's name, its fetch URL and, only when a separate push
/// URL is configured, a checkbox to reset it. Save stays disabled until the input is valid;
/// Cancel is always the rightmost button.
/// </summary>
internal sealed partial class EditRemoteDialog : Window
{
    private readonly TaskCompletionSource<EditRemoteResult?> _closed = new();

    internal EditRemoteDialog(RemoteInfo remote, IReadOnlyCollection<string> otherNames,
        IReadOnlyDictionary<string, string> otherUrls)
    {
        InitializeComponent();
        ViewModel = new EditRemoteDialogViewModel(remote, otherNames, otherUrls);
        DataContext = ViewModel;
        ViewModel.CloseRequested += (_, _) => Close();
        Opened += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        };
        Closed += (_, _) =>
        {
            _closed.TrySetResult(ViewModel.Result);
            ViewModel.Dispose();
        };
    }

    internal EditRemoteDialogViewModel ViewModel { get; }

    /// <summary>Completes with the chosen changes once the window closes, or null when it was
    /// cancelled or nothing changed.</summary>
    internal Task<EditRemoteResult?> Completion => _closed.Task;

    internal static string? Validate(string name, string url, IReadOnlyCollection<string> otherNames,
        IReadOnlyDictionary<string, string> otherUrls) =>
        RemoteDialogViewModel.Validate(name, url, otherNames, otherUrls);

    internal static Dictionary<string, string> UrlOwners(IEnumerable<RemoteInfo> remotes) =>
        RemoteDialogViewModel.UrlOwners(remotes);

    /// <summary>Shows the dialog for <paramref name="remote"/>. Returns <see langword="null"/>
    /// when the user cancels or saves without changing anything.</summary>
    public static async Task<EditRemoteResult?> ShowAsync(RemoteInfo remote, IReadOnlyCollection<string> otherNames,
        IReadOnlyDictionary<string, string> otherUrls)
    {
        if (!Dispatcher.UIThread.CheckAccess())
            return await Dispatcher.UIThread.InvokeAsync(() => ShowAsync(remote, otherNames, otherUrls));

        var dialog = new EditRemoteDialog(remote, otherNames, otherUrls);
        await SimpleDialog.ShowOwnedOrFree(dialog, AvaloniaDialogService.Owner);
        return await dialog.Completion;
    }
}
