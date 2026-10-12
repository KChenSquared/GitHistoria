namespace Historia.ViewModels;

/// <summary>
/// The inline form for one profile: the "new profile" form when <see cref="Existing"/> is null,
/// else the form for that profile. Editing the global profile writes <c>~/.gitconfig</c> behind a
/// confirmation. Every command reports a failure as an error notification instead of letting it
/// escape.
/// </summary>
public sealed partial class ProfileEditorViewModel : ObservableObject
{
    private const string TokenPageUrl = "https://github.com/settings/tokens/new?scopes=repo&description=Historia";
    private const string GitHubConnectedText = "Connected — a token is stored in the vault.";

    private readonly IProfileService _profiles;
    private readonly IDialogService _dialogs;
    private readonly INotificationService _notifications;
    private readonly ISshKeyService _ssh;
    private readonly IExternalLauncher _launcher;
    private readonly IGitCliService _git;
    private readonly IClipboardService _clipboard;
    private readonly string _homeDirectory;
    private readonly Action _closed;

    /// <param name="existing">The profile being edited, or null for a new one.</param>
    /// <param name="closed">Called when the form is finished: saved or cancelled.</param>
    public ProfileEditorViewModel(
        GitProfile? existing,
        IProfileService profiles,
        IDialogService dialogs,
        INotificationService notifications,
        ISshKeyService ssh,
        IExternalLauncher launcher,
        IGitCliService git,
        IClipboardService clipboard,
        string homeDirectory,
        Action closed)
    {
        Existing = existing;
        _profiles = profiles;
        _dialogs = dialogs;
        _notifications = notifications;
        _ssh = ssh;
        _launcher = launcher;
        _git = git;
        _clipboard = clipboard;
        _homeDirectory = homeDirectory;
        _closed = closed;

        Name = existing?.DisplayName ?? "";
        UserName = existing?.GitUserName ?? "";
        Email = existing?.GitUserEmail ?? "";
        SigningKeyId = existing?.GpgSigningKeyId ?? "";
        SignCommits = existing?.SignCommits ?? false;
        RefreshStatus();
    }

    /// <summary>The profile being edited, or null when the form creates one.</summary>
    public GitProfile? Existing { get; }

    public bool IsCreating => Existing is null;

    public string Title => Existing is null ? "New profile" : $"Edit {Existing.DisplayName}";

    public string SaveLabel => Existing is null ? "Create profile" : "Save";

    [ObservableProperty]
    public partial string Name { get; set; } = "";

    [ObservableProperty]
    public partial string UserName { get; set; } = "";

    [ObservableProperty]
    public partial string Email { get; set; } = "";

    [ObservableProperty]
    public partial string SigningKeyId { get; set; } = "";

    [ObservableProperty]
    public partial bool SignCommits { get; set; }

    /// <summary>Where the Historia-managed key lives, or why there is none yet.</summary>
    [ObservableProperty]
    public partial string KeyStatus { get; private set; } = "";

    /// <summary>Whether a token is stored for the edited profile.</summary>
    [ObservableProperty]
    public partial string GitHubStatus { get; private set; } = "";

    /// <summary>True when the edited profile has a Historia-managed key pair on disk.</summary>
    public bool HasKey { get; private set; }

    /// <summary>Re-reads the key and GitHub state of the edited profile from the services.</summary>
    public void RefreshStatus()
    {
        var info = Existing is null ? null : _ssh.PathsFor(Existing.Id);
        if (info is { Exists: true })
        {
            KeyStatus = $"Private: {info.PrivatePath}\nPublic:  {info.PublicPath}";
            HasKey = true;
        }
        else
        {
            KeyStatus = Existing is null
                ? "Save the profile first, then generate a key."
                : "No Historia-managed key yet.";
            HasKey = false;
        }

        var current = Existing is null ? null : _profiles.GetProfileById(Existing.Id) ?? Existing;
        GitHubStatus = string.IsNullOrWhiteSpace(current?.GitHubTokenRef) ? "Not connected." : GitHubConnectedText;

        GenerateKeyCommand.NotifyCanExecuteChanged();
        CopyPublicKeyCommand.NotifyCanExecuteChanged();
    }

    // A bound TextBox can hand back null when it is cleared.
    private static string Clean(string? text) => text?.Trim() ?? "";

    [RelayCommand]
    private void Cancel() => _closed();

    [RelayCommand]
    private async Task SaveAsync()
    {
        var name = Clean(Name);
        var user = Clean(UserName);
        var email = Clean(Email);
        var signKey = Clean(SigningKeyId);
        try
        {
            if (Existing is { IsGlobal: true })
            {
                if (!await _dialogs.ConfirmDestructiveAsync(
                    "Write to ~/.gitconfig?",
                    $"Write {user} <{email}> to ~/.gitconfig now? This affects all Git tools.",
                    "Write"))
                    return;

                await _git.RunAsync(new GitCliRequest
                {
                    Arguments = ["config", "--global", "user.name", user],
                    WorkingDirectory = _homeDirectory,
                });
                await _git.RunAsync(new GitCliRequest
                {
                    Arguments = ["config", "--global", "user.email", email],
                    WorkingDirectory = _homeDirectory,
                });
                await _profiles.SyncGlobalIdentityAsync();
                _notifications.Publish(NotificationSeverity.Success, "Global Git identity updated.");
                _closed();
                return;
            }

            if (Existing is null)
            {
                await _profiles.CreateProfileAsync(new GitProfileCreateRequest
                {
                    DisplayName = name,
                    GitUserName = user,
                    GitUserEmail = email,
                    GpgSigningKeyId = signKey.Length == 0 ? null : signKey,
                    SignCommits = SignCommits,
                });
                _notifications.Publish(NotificationSeverity.Success, $"Profile '{name}' created.");
            }
            else
            {
                await _profiles.UpdateProfileAsync(Existing.Id, new GitProfileUpdateRequest
                {
                    DisplayName = name,
                    GitUserName = user,
                    GitUserEmail = email,
                    GpgSigningKeyId = signKey,
                    SignCommits = SignCommits,
                });
                _notifications.Publish(NotificationSeverity.Success, "Profile saved.");
            }

            _closed();
        }
        catch (Exception ex)
        {
            _notifications.Publish(NotificationSeverity.Error, ex.Message);
        }
    }

    [RelayCommand(CanExecute = nameof(CanGenerateKey))]
    private async Task GenerateKeyAsync()
    {
        if (Existing is null)
            return;

        try
        {
            if (!await _dialogs.ConfirmDestructiveAsync("Generate SSH key",
                    "Generate a new ed25519 key for this profile? Any existing Historia-managed key for it is replaced and cannot be recovered. The key has no passphrase.",
                    "Generate"))
                return;

            var info = await _ssh.GenerateEd25519Async(Existing.Id, string.IsNullOrWhiteSpace(Email) ? Existing.GitUserEmail : Email);
            await _profiles.UpdateProfileAsync(Existing.Id, new GitProfileUpdateRequest { SshKeyPath = info.PrivatePath });
            RefreshStatus();
            _notifications.Publish(NotificationSeverity.Success, "SSH key generated.");
        }
        catch (Exception ex)
        {
            _notifications.Publish(NotificationSeverity.Error, ex.Message);
        }
    }

    private bool CanGenerateKey() => Existing is not null;

    [RelayCommand(CanExecute = nameof(CanCopyPublicKey))]
    private async Task CopyPublicKeyAsync()
    {
        if (Existing is null)
            return;

        try
        {
            var pub = await _ssh.ReadPublicKeyAsync(Existing.Id);
            if (string.IsNullOrWhiteSpace(pub))
            {
                _notifications.Publish(NotificationSeverity.Warning, "No public key to copy — generate one first.");
                return;
            }

            await _clipboard.SetTextAsync(pub);
            _notifications.Publish(NotificationSeverity.Success, "Public key copied to the clipboard.");
        }
        catch (Exception ex)
        {
            _notifications.Publish(NotificationSeverity.Error, ex.Message);
        }
    }

    private bool CanCopyPublicKey() => HasKey;

    [RelayCommand]
    private async Task SignInGitHubAsync()
    {
        // Device-flow OAuth needs a registered GitHub client id, which Historia does not ship;
        // until then this opens GitHub's token page and stores a pasted PAT in the vault (the
        // same fallback GitHub Desktop offers). All network is user-initiated.
        try
        {
            _launcher.OpenUrl(TokenPageUrl);
            var pat = await _dialogs.PromptAsync("Sign in with GitHub",
                "Paste a GitHub personal access token (it is stored in the OS credential vault, never in profiles.json):");
            if (string.IsNullOrWhiteSpace(pat))
                return;

            if (Existing is null)
            {
                _notifications.Publish(NotificationSeverity.Warning, "Create the profile first, then connect GitHub.");
                return;
            }

            await _profiles.UpdateProfileAsync(Existing.Id, new GitProfileUpdateRequest { GitHubToken = pat.Trim() });
            GitHubStatus = GitHubConnectedText;
            _notifications.Publish(NotificationSeverity.Success, "GitHub token stored.");
        }
        catch (Exception ex)
        {
            _notifications.Publish(NotificationSeverity.Error, ex.Message);
        }
    }
}
