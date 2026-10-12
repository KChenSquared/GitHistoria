namespace Historia.ViewModels;

/// <summary>
/// One profile in the Profiles list: its summary lines and the actions on it, or its inline editor
/// while it is being edited. The list replaces its rows wholesale on every refresh, so a row is
/// immutable.
/// </summary>
public sealed partial class ProfileRowViewModel : ObservableObject
{
    private readonly ProfilesViewModel _owner;

    internal ProfileRowViewModel(GitProfile profile, bool isDefault, bool hasSshKey, ProfileEditorViewModel? editor, ProfilesViewModel owner)
    {
        _owner = owner;
        Profile = profile;
        Editor = editor;
        CanSetDefault = !isDefault;
        CanDelete = !profile.IsGlobal;

        var tag = profile.IsGlobal ? "  (global)" : isDefault ? "  (default)" : "";
        Title = $"{profile.DisplayName}{tag}";
        Identity = string.IsNullOrWhiteSpace(profile.GitUserName) && string.IsNullOrWhiteSpace(profile.GitUserEmail)
            ? (profile.IsGlobal ? "no ~/.gitconfig identity set" : "no identity")
            : $"{profile.GitUserName} <{profile.GitUserEmail}>";

        var badges = new List<string>();
        if (hasSshKey || !string.IsNullOrWhiteSpace(profile.SshKeyPath)) badges.Add("SSH key");
        if (!string.IsNullOrWhiteSpace(profile.GitHubTokenRef)) badges.Add("GitHub connected");
        if (profile.SignCommits) badges.Add("signs commits");
        Badges = string.Join("  ·  ", badges);
    }

    public GitProfile Profile { get; }

    /// <summary>Display name, tagged "(global)" or "(default)" where it applies.</summary>
    public string Title { get; }

    /// <summary><c>name &lt;email&gt;</c>, or a note when no identity is set.</summary>
    public string Identity { get; }

    /// <summary>The profile's SSH, GitHub and signing facts, joined for one line; empty when it has none.</summary>
    public string Badges { get; }

    public bool HasBadges => Badges.Length > 0;

    /// <summary>False for the profile that already is the default.</summary>
    public bool CanSetDefault { get; }

    /// <summary>False for the global profile, which can never be deleted.</summary>
    public bool CanDelete { get; }

    /// <summary>The inline form while this row is being edited, else null.</summary>
    public ProfileEditorViewModel? Editor { get; }

    public bool IsEditing => Editor is not null;

    public bool IsNotEditing => Editor is null;

    [RelayCommand]
    private Task EditAsync() => _owner.BeginEditAsync(Profile);

    [RelayCommand]
    private Task SetDefaultAsync() => _owner.SetDefaultAsync(Profile);

    [RelayCommand]
    private Task DeleteAsync() => _owner.DeleteAsync(Profile);
}
