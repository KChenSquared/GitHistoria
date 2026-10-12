namespace Historia.ViewModels;

/// <summary>
/// The Add remote dialog: a name, a fetch URL and a "Fetch after adding" choice (on by default).
/// Add stays disabled until the input is valid. The name starts as <c>origin</c> when the
/// repository has no remotes.
/// </summary>
public sealed partial class AddRemoteDialogViewModel : RemoteDialogViewModel
{
    private readonly bool _hadRemotes;

    /// <param name="existing">The repository's remotes: their names and URLs may not be reused.</param>
    public AddRemoteDialogViewModel(IReadOnlyList<RemoteInfo> existing)
        : base(existing.Select(r => r.Name).ToList(), UrlOwners(existing))
    {
        _hadRemotes = existing.Count > 0;
        Fetch = true;
        Url = "";
        Name = _hadRemotes ? "" : "origin";
        Revalidate();
    }

    /// <summary>Whether the new remote is fetched right after it is added.</summary>
    [ObservableProperty]
    public partial bool Fetch { get; set; }

    /// <summary>True when the URL box, not the name box, is the sensible place to start typing.</summary>
    public bool FocusUrlFirst => !_hadRemotes;

    /// <summary>What Add produced, or null when the dialog was cancelled or closed.</summary>
    public AddRemoteResult? Result { get; private set; }

    // An empty form is not an error yet: Add stays disabled, but no hint shows until the user has
    // typed something.
    protected override bool IsUntouched() => TrimmedUrl.Length == 0 && (TrimmedName.Length == 0 || !_hadRemotes);

    protected override void OnValidated() => AddCommand.NotifyCanExecuteChanged();

    [RelayCommand(CanExecute = nameof(IsValid))]
    private void Add()
    {
        if (!IsValid)
            return;

        Result = new AddRemoteResult(TrimmedName, TrimmedUrl, Fetch);
        RequestClose();
    }

    [RelayCommand]
    private void Cancel()
    {
        Result = null;
        RequestClose();
    }
}
