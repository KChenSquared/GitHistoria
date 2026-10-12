namespace Historia.ViewModels;

/// <summary>
/// The "Profiles &amp; Accounts" panel: the configured Git identity profiles (the reserved global
/// profile first) with edit / set-as-default / delete actions, and one inline form that either
/// edits a row or adds a profile. Editing the global profile writes <c>~/.gitconfig</c> behind two
/// warnings; it can never be deleted. The list follows the profile and settings services.
/// </summary>
public sealed partial class ProfilesViewModel : ViewModelBase
{
    private readonly IProfileService _profiles;
    private readonly ISettingsService _settings;
    private readonly IDialogService _dialogs;
    private readonly INotificationService _notifications;
    private readonly ISshKeyService _ssh;
    private readonly IExternalLauncher _launcher;
    private readonly IGitCliService _git;
    private readonly INavigationBus _navigation;
    private readonly IWorkspaceService _workspace;
    private readonly IUiDispatcher _ui;
    private readonly IServiceProvider _sp;
    private readonly IClipboardService _clipboard;
    private readonly string _homeDirectory;

    // null = not editing; Guid.Empty = the "add profile" form is open; else the row being edited.
    private Guid? _editingId;
    private ProfileEditorViewModel? _editor;

    /// <param name="intent">"add" opens the add-profile form straight away.</param>
    /// <param name="sp">Handed to the shared default-profile flow, which resolves its own services.</param>
    public ProfilesViewModel(
        IProfileService profiles,
        ISettingsService settings,
        IDialogService dialogs,
        INotificationService notifications,
        ISshKeyService ssh,
        IExternalLauncher launcher,
        IGitCliService git,
        INavigationBus navigation,
        IWorkspaceService workspace,
        IUiDispatcher ui,
        IServiceProvider sp,
        string? intent = null,
        IClipboardService? clipboard = null,
        string? homeDirectory = null)
    {
        _profiles = profiles;
        _settings = settings;
        _dialogs = dialogs;
        _notifications = notifications;
        _ssh = ssh;
        _launcher = launcher;
        _git = git;
        _navigation = navigation;
        _workspace = workspace;
        _ui = ui;
        _sp = sp;
        _clipboard = clipboard ?? NullClipboardService.Instance;
        _homeDirectory = homeDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (string.Equals(intent, "add", StringComparison.OrdinalIgnoreCase))
            OpenEditor(null);

        Rows = BuildRows();

        // Both services raise on the thread that changed them; the rebuild touches bound state.
        Subscribe(() => _profiles.ProfilesChanged += OnServiceChanged, () => _profiles.ProfilesChanged -= OnServiceChanged);
        Subscribe(() => _settings.Changed += OnSettingsChanged, () => _settings.Changed -= OnSettingsChanged);
    }

    /// <summary>The profiles, global first. Replaced as a whole on every refresh.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<ProfileRowViewModel> Rows { get; private set; } = [];

    /// <summary>The add-profile form while it is open, else null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowAddButton))]
    public partial ProfileEditorViewModel? NewEditor { get; private set; }

    /// <summary>The "+ Add profile" button shows while the add form is closed.</summary>
    public bool ShowAddButton => NewEditor is null;

    private void OnServiceChanged(object? sender, EventArgs e) => _ui.Post(Refresh);

    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs e) => _ui.Post(Refresh);

    private void Refresh()
    {
        if (IsDisposed)
            return;

        if (_editingId is { } id && id != Guid.Empty && _profiles.GetProfileById(id) is null)
            CloseEditor();

        _editor?.RefreshStatus();
        Rows = BuildRows();
    }

    private IReadOnlyList<ProfileRowViewModel> BuildRows()
    {
        var defaultId = _profiles.GetDefaultProfile()?.Id;
        return
        [
            .. _profiles.GetAllProfiles().Select(p => new ProfileRowViewModel(
                p,
                p.Id == defaultId,
                _ssh.PathsFor(p.Id).Exists,
                _editingId == p.Id ? _editor : null,
                this)),
        ];
    }

    private void OpenEditor(GitProfile? profile)
    {
        _editingId = profile?.Id ?? Guid.Empty;
        _editor = new ProfileEditorViewModel(
            profile, _profiles, _dialogs, _notifications, _ssh, _launcher, _git, _clipboard, _homeDirectory, OnEditorFinished);
        NewEditor = profile is null ? _editor : null;
    }

    private void CloseEditor()
    {
        _editingId = null;
        _editor = null;
        NewEditor = null;
    }

    private void OnEditorFinished()
    {
        CloseEditor();
        Refresh();
    }

    [RelayCommand]
    private void BeginAdd()
    {
        OpenEditor(null);
        Refresh();
    }

    [RelayCommand]
    private void Close()
        => _navigation.RequestNavigate(_workspace.Focused is not null ? "repo" : "");

    /// <summary>Opens the inline form for <paramref name="p"/>. Editing the global profile changes
    /// the machine-wide identity in <c>~/.gitconfig</c>, so it is gated by a warning before the
    /// editor even opens.</summary>
    public async Task BeginEditAsync(GitProfile p)
    {
        try
        {
            if (p.IsGlobal && !await _dialogs.ConfirmDestructiveAsync(
                "Edit the global Git identity?",
                "Editing the global profile changes your machine-wide Git identity in ~/.gitconfig. "
                + "This affects every Git tool on this machine, not just Historia.",
                "Continue"))
                return;

            OpenEditor(p);
            Refresh();
        }
        catch (Exception ex)
        {
            _notifications.Publish(NotificationSeverity.Error, ex.Message);
        }
    }

    /// <summary>Makes <paramref name="p"/> the default profile; a non-global one first asks whether
    /// repositories without their own profile follow it. Declining changes and publishes nothing.</summary>
    public async Task SetDefaultAsync(GitProfile p)
    {
        try
        {
            await DefaultProfileFlow.SetAsync(_sp, p);
        }
        catch (Exception ex)
        {
            _notifications.Publish(NotificationSeverity.Error, ex.Message);
        }
    }

    /// <summary>Deletes <paramref name="p"/> after a confirmation.</summary>
    public async Task DeleteAsync(GitProfile p)
    {
        try
        {
            if (!await _dialogs.ConfirmDestructiveAsync("Delete profile",
                $"Delete '{p.DisplayName}'? Its stored tokens and repository selections are removed.", "Delete"))
                return;

            var result = await _profiles.DeleteProfileAsync(p.Id);
            _notifications.Publish(NotificationSeverity.Info, result switch
            {
                ProfileDeleteResult.DeletedClearedDefault => "Profile deleted — repositories without their own profile now use local Git config.",
                ProfileDeleteResult.Deleted => "Profile deleted.",
                ProfileDeleteResult.CannotDeleteGlobal => "The global profile can't be deleted.",
                _ => "Profile not found.",
            });
        }
        catch (Exception ex)
        {
            _notifications.Publish(NotificationSeverity.Error, ex.Message);
        }
    }
}
