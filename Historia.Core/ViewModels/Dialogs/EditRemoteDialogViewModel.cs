namespace Historia.ViewModels;

/// <summary>
/// The Edit remote dialog: the remote's name, its fetch URL and, only when a separate push URL is
/// configured, a choice to reset it. Save stays disabled until the input is valid.
/// </summary>
public sealed partial class EditRemoteDialogViewModel : RemoteDialogViewModel
{
    private readonly RemoteInfo _remote;

    /// <param name="remote">The remote being edited.</param>
    /// <param name="otherNames">The names of the repository's other remotes.</param>
    /// <param name="otherUrls">Maps each other remote's trimmed fetch URL to its name.</param>
    public EditRemoteDialogViewModel(RemoteInfo remote, IReadOnlyCollection<string> otherNames,
        IReadOnlyDictionary<string, string> otherUrls)
        : base(otherNames, otherUrls)
    {
        _remote = remote;
        Url = remote.Url;
        Name = remote.Name;
        Revalidate();
    }

    /// <summary>Whether the remote has a push URL different from its fetch URL, which makes the reset choice available.</summary>
    public bool HasDistinctPushUrl => _remote.HasDistinctPushUrl;

    public string ResetPushUrlLabel => $"Reset push URL (currently {_remote.PushUrl})";

    /// <summary>Whether saving also drops the separate push URL.</summary>
    [ObservableProperty]
    public partial bool ResetPushUrl { get; set; }

    /// <summary>What Save produced, or null when the dialog was cancelled, closed or saved unchanged.</summary>
    public EditRemoteResult? Result { get; private set; }

    protected override void OnValidated() => SaveCommand.NotifyCanExecuteChanged();

    [RelayCommand(CanExecute = nameof(IsValid))]
    private void Save()
    {
        if (!IsValid)
            return;

        var name = TrimmedName;
        var url = TrimmedUrl;
        var reset = HasDistinctPushUrl && ResetPushUrl;
        var unchanged = string.Equals(name, _remote.Name, StringComparison.Ordinal)
            && string.Equals(url, _remote.Url, StringComparison.Ordinal)
            && !reset;
        Result = unchanged ? null : new EditRemoteResult(name, url, reset);
        RequestClose();
    }

    [RelayCommand]
    private void Cancel()
    {
        Result = null;
        RequestClose();
    }
}
