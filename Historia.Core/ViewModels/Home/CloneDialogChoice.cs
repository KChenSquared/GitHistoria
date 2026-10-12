namespace Historia.ViewModels;

/// <summary>What the user entered in the clone dialog. <see cref="ProfileId"/> is the checkout
/// identity chosen there; null means local config.</summary>
public sealed record CloneDialogChoice(
    string Url,
    string TargetDirectory,
    bool Shallow = false,
    bool Sparse = false,
    Guid? ProfileId = null);

/// <summary>Shows the clone dialog and returns the user's choice, or null when it was cancelled.
/// The dialog is a window, so the view supplies it. <paramref name="guessFolderName"/> derives the
/// folder name from the URL as the user types.</summary>
public delegate Task<CloneDialogChoice?> CloneDialogPresenter(
    string defaultRoot,
    Func<string, string> guessFolderName,
    IReadOnlyList<GitProfile> profiles,
    Guid? defaultProfileId);
