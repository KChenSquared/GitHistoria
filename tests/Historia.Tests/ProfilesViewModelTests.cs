namespace Historia.Tests;

/// <summary>
/// The Profiles panel's view model: the rows follow the profile service, the inline form validates
/// through the service and reports its failures, the global profile is guarded by confirmations,
/// and delete and set-default ask before they change anything and stay silent when declined.
/// </summary>
[Trait("tier", "unit")]
public class ProfilesViewModelTests : IDisposable
{
    private sealed class FakeSshKeys(string root) : ISshKeyService
    {
        public Dictionary<Guid, string> PublicKeys { get; } = [];
        public List<(Guid Id, string Comment)> Generated { get; } = [];

        public SshKeyInfo PathsFor(Guid profileId)
        {
            var priv = Path.Combine(root, profileId.ToString("N"), "id_ed25519");
            return new SshKeyInfo(priv, priv + ".pub", PublicKeys.GetValueOrDefault(profileId));
        }

        public Task<SshKeyInfo> GenerateEd25519Async(Guid profileId, string comment, CancellationToken ct = default)
        {
            Generated.Add((profileId, comment));
            PublicKeys[profileId] = $"ssh-ed25519 AAAA {comment}";
            var info = PathsFor(profileId);
            Directory.CreateDirectory(Path.GetDirectoryName(info.PrivatePath)!);
            File.WriteAllText(info.PrivatePath, "private");
            File.WriteAllText(info.PublicPath, info.PublicKey!);
            return Task.FromResult(info);
        }

        public Task<string?> ReadPublicKeyAsync(Guid profileId, CancellationToken ct = default)
            => Task.FromResult(PublicKeys.GetValueOrDefault(profileId));
    }

    private sealed class FakeLauncher : IExternalLauncher
    {
        public List<string> Urls { get; } = [];

        public void OpenUrl(string url) => Urls.Add(url);

        public void RevealInFileManager(string path)
        {
        }

        public void OpenFolder(string path)
        {
        }
    }

    /// <summary>A workspace with nothing open; the panel only asks whether a repository is focused.</summary>
    private sealed class EmptyWorkspace : IWorkspaceService
    {
        public IReadOnlyList<RepositoryContext> OpenRepositories => [];
        public RepositoryContext? Focused => null;

        public Task<RepositoryContext> OpenAsync(string path, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<RepositoryContext> InitAsync(string path, string? initialBranch, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<RepositoryContext> CloneAsync(string url, string targetDirectory, Historia.Services.Workspace.CloneOptions options, IProgress<GitProgress>? progress, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<RepositoryContext> OpenSubmoduleAsync(RepositoryContext owner, SubmoduleInfo submodule, CancellationToken ct = default) => throw new NotSupportedException();
        public Task CloseAsync(RepositoryContext context) => throw new NotSupportedException();
        public void Focus(RepositoryContext context) => throw new NotSupportedException();
        public void EnsureInitialized(RepositoryContext context) => throw new NotSupportedException();
        public void Move(RepositoryContext context, int newIndex) => throw new NotSupportedException();
        public Task RestoreSessionAsync(CancellationToken ct = default) => throw new NotSupportedException();

#pragma warning disable CS0067
        public event EventHandler<FocusedRepositoryChangedEventArgs>? FocusedRepositoryChanged;
        public event EventHandler<OpenRepositoriesChangedEventArgs>? OpenRepositoriesChanged;
#pragma warning restore CS0067
    }

    private readonly StateDir _state = new();
    private readonly FakeVault _vault = new();
    private readonly SettingsService _settings = SettingsService.Load();
    private readonly ProfileService _profiles;
    private readonly FakeSshKeys _ssh;
    private readonly FakeLauncher _launcher = new();
    private readonly FakeDialogService _dialogs = new();
    private readonly FakeNotificationService _notes = new();
    private readonly FakeClipboardService _clipboard = new();
    private readonly RecordingGitCliService _git = new();
    private readonly NavigationBus _nav = new();
    private readonly ServiceProvider _sp;

    public ProfilesViewModelTests()
    {
        _profiles = new ProfileService(_settings, _vault, new NoGitCli());
        _ssh = new FakeSshKeys(Path.Combine(_state.Path, "keys"));
        var services = new ServiceCollection();
        services.AddSingleton<IProfileService>(_profiles);
        services.AddSingleton<IDialogService>(_dialogs);
        services.AddSingleton<INotificationService>(_notes);
        _sp = services.BuildServiceProvider();
    }

    public void Dispose()
    {
        _sp.Dispose();
        _state.Dispose();
    }

    private ProfilesViewModel Create(string? intent = null, IUiDispatcher? ui = null)
        => new(_profiles, _settings, _dialogs, _notes, _ssh, _launcher, _git, _nav, new EmptyWorkspace(),
            ui ?? new ImmediateUiDispatcher(), _sp, intent, _clipboard, homeDirectory: "/home/test");

    private Task<GitProfile> AddProfile(string name = "Work", string email = "work@example.com")
        => _profiles.CreateProfileAsync(new GitProfileCreateRequest
        {
            DisplayName = name,
            GitUserName = name + " User",
            GitUserEmail = email,
        });

    private static ProfileRowViewModel RowFor(ProfilesViewModel vm, Guid id) => vm.Rows.Single(r => r.Profile.Id == id);

    private static void Fill(ProfileEditorViewModel editor, string name, string user, string email, string signKey = "")
    {
        editor.Name = name;
        editor.UserName = user;
        editor.Email = email;
        editor.SigningKeyId = signKey;
    }

    // ── Rows ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Rows_start_with_the_global_profile_alone_and_no_form()
    {
        using var vm = Create();

        var row = Assert.Single(vm.Rows);
        Assert.True(row.Profile.IsGlobal);
        Assert.False(row.CanDelete);
        Assert.False(row.CanSetDefault);
        Assert.Contains("(global)", row.Title);
        Assert.Null(vm.NewEditor);
        Assert.True(vm.ShowAddButton);
    }

    [Fact]
    public async Task A_row_summarises_identity_badges_and_default_state()
    {
        var work = await AddProfile();
        _ssh.PublicKeys[work.Id] = "ssh-ed25519 AAAA";
        await _profiles.UpdateProfileAsync(work.Id, new GitProfileUpdateRequest { SignCommits = true });
        await _profiles.SetDefaultProfileAsync(work.Id);

        using var vm = Create();

        var row = RowFor(vm, work.Id);
        Assert.Equal("Work  (default)", row.Title);
        Assert.Equal("Work User <work@example.com>", row.Identity);
        Assert.Equal("SSH key  ·  signs commits", row.Badges);
        Assert.True(row.HasBadges);
        Assert.False(row.CanSetDefault);
        Assert.True(row.CanDelete);
        Assert.True(RowFor(vm, _profiles.GetGlobalProfile().Id).CanSetDefault);
    }

    [Fact]
    public async Task A_profile_without_badges_hides_the_badge_line()
    {
        var work = await AddProfile();

        using var vm = Create();

        Assert.False(RowFor(vm, work.Id).HasBadges);
        Assert.Equal("Work", RowFor(vm, work.Id).Title);
    }

    [Fact]
    public async Task Rows_follow_profile_changes_after_the_post_runs()
    {
        var ui = new RecordingUiDispatcher();
        using var vm = Create(ui: ui);

        await AddProfile();
        Assert.Single(vm.Rows);
        Assert.True(ui.PostCount > 0);

        ui.DrainPosted();

        Assert.Equal(2, vm.Rows.Count);
    }

    [Fact]
    public async Task Rows_follow_settings_changes_after_the_post_runs()
    {
        var work = await AddProfile();
        var ui = new RecordingUiDispatcher();
        using var vm = Create(ui: ui);
        Assert.True(RowFor(vm, work.Id).CanSetDefault);

        await _settings.UpdateAsync(s => s with { DefaultProfileId = work.Id });
        Assert.True(ui.PostCount > 0);
        Assert.True(RowFor(vm, work.Id).CanSetDefault);
        ui.DrainPosted();

        Assert.False(RowFor(vm, work.Id).CanSetDefault);
    }

    [Fact]
    public async Task A_disposed_view_model_stops_listening()
    {
        var ui = new RecordingUiDispatcher();
        var vm = Create(ui: ui);
        vm.Dispose();

        await AddProfile();
        await _settings.UpdateAsync(s => s with { AutoHideNotices = false });

        Assert.Equal(0, ui.PostCount);
    }

    [Fact]
    public async Task A_post_that_runs_after_dispose_changes_nothing()
    {
        var ui = new RecordingUiDispatcher();
        var vm = Create(ui: ui);
        await AddProfile();
        vm.Dispose();

        ui.DrainPosted();

        Assert.Single(vm.Rows);
    }

    // ── Adding ───────────────────────────────────────────────────────────────

    [Fact]
    public void The_add_intent_opens_the_add_form()
    {
        using var vm = Create("add");

        var editor = Assert.IsType<ProfileEditorViewModel>(vm.NewEditor);
        Assert.True(editor.IsCreating);
        Assert.Equal("New profile", editor.Title);
        Assert.Equal("Create profile", editor.SaveLabel);
        Assert.False(vm.ShowAddButton);
    }

    [Fact]
    public void Another_intent_leaves_the_form_closed()
    {
        using var vm = Create("other");

        Assert.Null(vm.NewEditor);
    }

    [Fact]
    public void Begin_add_opens_the_form_and_cancel_closes_it()
    {
        using var vm = Create();

        vm.BeginAddCommand.Execute(null);
        var editor = Assert.IsType<ProfileEditorViewModel>(vm.NewEditor);
        Assert.False(vm.ShowAddButton);

        editor.CancelCommand.Execute(null);

        Assert.Null(vm.NewEditor);
        Assert.True(vm.ShowAddButton);
        Assert.Empty(_notes.Published);
    }

    [Fact]
    public async Task Creating_a_profile_trims_the_fields_saves_it_and_closes_the_form()
    {
        using var vm = Create("add");
        var editor = vm.NewEditor!;
        Fill(editor, "  Personal ", " Pat ", " pat@example.com ", "  ");
        editor.SignCommits = true;

        await editor.SaveCommand.ExecuteAsync(null);

        var created = _profiles.GetAllProfiles().Single(p => !p.IsGlobal);
        Assert.Equal("Personal", created.DisplayName);
        Assert.Equal("Pat", created.GitUserName);
        Assert.Equal("pat@example.com", created.GitUserEmail);
        Assert.Null(created.GpgSigningKeyId);
        Assert.True(created.SignCommits);
        var note = Assert.Single(_notes.Published);
        Assert.Equal(NotificationSeverity.Success, note.Severity);
        Assert.Equal("Profile 'Personal' created.", note.Message);
        Assert.Null(vm.NewEditor);
        Assert.Equal(2, vm.Rows.Count);
    }

    [Theory]
    [InlineData("", "Pat", "pat@example.com", "Display name is required")]
    [InlineData("Personal", "", "pat@example.com", "Git user name is required")]
    [InlineData("Personal", "Pat", "not-an-email", "A valid email address is required")]
    public async Task A_form_the_service_rejects_reports_the_error_and_stays_open(string name, string user, string email, string expected)
    {
        using var vm = Create("add");
        var editor = vm.NewEditor!;
        Fill(editor, name, user, email);

        await editor.SaveCommand.ExecuteAsync(null);

        var note = Assert.Single(_notes.Published);
        Assert.Equal(NotificationSeverity.Error, note.Severity);
        Assert.Contains(expected, note.Message);
        Assert.Same(editor, vm.NewEditor);
        Assert.Single(_profiles.GetAllProfiles());
    }

    [Fact]
    public async Task A_bad_signing_key_is_rejected_and_a_duplicate_name_too()
    {
        await AddProfile("Work");
        using var vm = Create("add");
        var editor = vm.NewEditor!;

        Fill(editor, "Other", "Pat", "pat@example.com", "xyz");
        await editor.SaveCommand.ExecuteAsync(null);
        Assert.Contains("GPG key id", Assert.Single(_notes.Published).Message);

        Fill(editor, "work", "Pat", "pat@example.com");
        await editor.SaveCommand.ExecuteAsync(null);
        Assert.Contains("already exists", _notes.Published[^1].Message);
        Assert.Equal(2, _profiles.GetAllProfiles().Count);
    }

    [Fact]
    public async Task The_add_form_can_not_generate_a_key_or_copy_one_yet()
    {
        using var vm = Create("add");
        var editor = vm.NewEditor!;

        Assert.False(editor.GenerateKeyCommand.CanExecute(null));
        Assert.False(editor.CopyPublicKeyCommand.CanExecute(null));
        Assert.Equal("Save the profile first, then generate a key.", editor.KeyStatus);
        Assert.Equal("Not connected.", editor.GitHubStatus);

        await editor.SignInGitHubCommand.ExecuteAsync(null);
        Assert.Empty(_notes.Published);
    }

    // ── Editing ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Editing_a_row_opens_its_form_with_the_saved_values()
    {
        var work = await AddProfile();
        using var vm = Create();

        await RowFor(vm, work.Id).EditCommand.ExecuteAsync(null);

        var editor = Assert.IsType<ProfileEditorViewModel>(RowFor(vm, work.Id).Editor);
        Assert.True(RowFor(vm, work.Id).IsEditing);
        Assert.False(RowFor(vm, work.Id).IsNotEditing);
        Assert.Equal("Edit Work", editor.Title);
        Assert.Equal("Save", editor.SaveLabel);
        Assert.Equal("Work", editor.Name);
        Assert.Equal("Work User", editor.UserName);
        Assert.Equal("work@example.com", editor.Email);
        Assert.Null(vm.NewEditor);
        Assert.Empty(_dialogs.ConfirmDestructiveCalls);
    }

    [Fact]
    public async Task Saving_an_edit_updates_the_profile_and_closes_the_form()
    {
        var work = await AddProfile();
        using var vm = Create();
        await RowFor(vm, work.Id).EditCommand.ExecuteAsync(null);
        var editor = RowFor(vm, work.Id).Editor!;
        editor.Name = "Work 2";
        editor.SigningKeyId = "ABCDEF1234567890";
        editor.SignCommits = true;

        await editor.SaveCommand.ExecuteAsync(null);

        var saved = _profiles.GetProfileById(work.Id)!;
        Assert.Equal("Work 2", saved.DisplayName);
        Assert.Equal("ABCDEF1234567890", saved.GpgSigningKeyId);
        Assert.True(saved.SignCommits);
        Assert.Equal("Profile saved.", Assert.Single(_notes.Published).Message);
        Assert.False(RowFor(vm, work.Id).IsEditing);
        Assert.Equal("Work 2", RowFor(vm, work.Id).Title);
    }

    [Fact]
    public async Task Opening_the_add_form_closes_an_open_row_form()
    {
        var work = await AddProfile();
        using var vm = Create();
        await RowFor(vm, work.Id).EditCommand.ExecuteAsync(null);

        vm.BeginAddCommand.Execute(null);

        Assert.False(RowFor(vm, work.Id).IsEditing);
        Assert.NotNull(vm.NewEditor);
    }

    [Fact]
    public async Task What_the_user_typed_survives_a_profile_change()
    {
        var work = await AddProfile();
        using var vm = Create();
        await RowFor(vm, work.Id).EditCommand.ExecuteAsync(null);
        var editor = RowFor(vm, work.Id).Editor!;
        editor.Name = "half typed";

        await AddProfile("Other", "other@example.com");

        Assert.Same(editor, RowFor(vm, work.Id).Editor);
        Assert.Equal("half typed", editor.Name);
    }

    [Fact]
    public async Task A_form_for_a_profile_that_was_deleted_closes()
    {
        var work = await AddProfile();
        using var vm = Create();
        await RowFor(vm, work.Id).EditCommand.ExecuteAsync(null);

        await _profiles.DeleteProfileAsync(work.Id);

        Assert.DoesNotContain(vm.Rows, r => r.Profile.Id == work.Id);
        Assert.Null(vm.NewEditor);
        Assert.True(vm.ShowAddButton);
    }

    // ── Global profile ───────────────────────────────────────────────────────

    [Fact]
    public async Task Editing_the_global_profile_asks_first_and_declining_opens_nothing()
    {
        using var vm = Create();
        var global = RowFor(vm, _profiles.GetGlobalProfile().Id);
        _dialogs.EnqueueConfirmDestructive(false);

        await global.EditCommand.ExecuteAsync(null);

        var call = Assert.Single(_dialogs.ConfirmDestructiveCalls);
        Assert.Equal("Edit the global Git identity?", call.Title);
        Assert.Equal("Continue", call.ConfirmLabel);
        Assert.False(RowFor(vm, global.Profile.Id).IsEditing);
    }

    [Fact]
    public async Task Writing_the_global_identity_confirms_again_and_declining_runs_no_git()
    {
        using var vm = Create();
        var global = RowFor(vm, _profiles.GetGlobalProfile().Id);
        _dialogs.EnqueueConfirmDestructive(true).EnqueueConfirmDestructive(false);
        await global.EditCommand.ExecuteAsync(null);
        var editor = RowFor(vm, global.Profile.Id).Editor!;
        Fill(editor, "Global", "Gina", "gina@example.com");

        await editor.SaveCommand.ExecuteAsync(null);

        Assert.Equal(2, _dialogs.ConfirmDestructiveCalls.Count);
        Assert.Equal("Write to ~/.gitconfig?", _dialogs.ConfirmDestructiveCalls[1].Title);
        Assert.Empty(_git.Requests);
        Assert.Empty(_notes.Published);
        Assert.True(RowFor(vm, global.Profile.Id).IsEditing);
    }

    [Fact]
    public async Task Writing_the_global_identity_sets_both_keys_in_the_home_directory()
    {
        using var vm = Create();
        var global = RowFor(vm, _profiles.GetGlobalProfile().Id);
        _dialogs.EnqueueConfirmDestructive(true).EnqueueConfirmDestructive(true);
        await global.EditCommand.ExecuteAsync(null);
        var editor = RowFor(vm, global.Profile.Id).Editor!;
        Fill(editor, "Global", " Gina ", " gina@example.com ");

        await editor.SaveCommand.ExecuteAsync(null);

        Assert.Equal(2, _git.Requests.Count);
        Assert.Equal(["config", "--global", "user.name", "Gina"], _git.Requests[0].Arguments);
        Assert.Equal(["config", "--global", "user.email", "gina@example.com"], _git.Requests[1].Arguments);
        Assert.All(_git.Requests, r => Assert.Equal("/home/test", r.WorkingDirectory));
        Assert.Equal("Global Git identity updated.", Assert.Single(_notes.Published).Message);
        Assert.False(RowFor(vm, global.Profile.Id).IsEditing);
    }

    // ── SSH key ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Generating_a_key_asks_first_and_declining_changes_nothing()
    {
        var work = await AddProfile();
        using var vm = Create();
        await RowFor(vm, work.Id).EditCommand.ExecuteAsync(null);
        var editor = RowFor(vm, work.Id).Editor!;
        _dialogs.EnqueueConfirmDestructive(false);

        await editor.GenerateKeyCommand.ExecuteAsync(null);

        Assert.Equal("Generate SSH key", Assert.Single(_dialogs.ConfirmDestructiveCalls).Title);
        Assert.Empty(_ssh.Generated);
        Assert.Empty(_notes.Published);
        Assert.False(editor.HasKey);
    }

    [Fact]
    public async Task Generating_a_key_stores_the_path_and_enables_copy()
    {
        var work = await AddProfile();
        using var vm = Create();
        await RowFor(vm, work.Id).EditCommand.ExecuteAsync(null);
        var editor = RowFor(vm, work.Id).Editor!;
        Assert.Equal("No Historia-managed key yet.", editor.KeyStatus);
        Assert.False(editor.CopyPublicKeyCommand.CanExecute(null));
        _dialogs.EnqueueConfirmDestructive(true);

        await editor.GenerateKeyCommand.ExecuteAsync(null);

        Assert.Equal((work.Id, "work@example.com"), Assert.Single(_ssh.Generated));
        Assert.Equal(_ssh.PathsFor(work.Id).PrivatePath, _profiles.GetProfileById(work.Id)!.SshKeyPath);
        Assert.True(editor.HasKey);
        Assert.StartsWith("Private: ", editor.KeyStatus);
        Assert.True(editor.CopyPublicKeyCommand.CanExecute(null));
        Assert.Equal("SSH key generated.", Assert.Single(_notes.Published).Message);
        Assert.Same(editor, RowFor(vm, work.Id).Editor);
    }

    [Fact]
    public async Task Generating_a_key_prefers_the_email_typed_in_the_form()
    {
        var work = await AddProfile();
        using var vm = Create();
        await RowFor(vm, work.Id).EditCommand.ExecuteAsync(null);
        var editor = RowFor(vm, work.Id).Editor!;
        editor.Email = "typed@example.com";
        _dialogs.EnqueueConfirmDestructive(true);

        await editor.GenerateKeyCommand.ExecuteAsync(null);

        Assert.Equal("typed@example.com", Assert.Single(_ssh.Generated).Comment);
    }

    [Fact]
    public async Task Copying_the_public_key_puts_it_on_the_clipboard()
    {
        var work = await AddProfile();
        _ssh.PublicKeys[work.Id] = "ssh-ed25519 AAAA work";
        using var vm = Create();
        await RowFor(vm, work.Id).EditCommand.ExecuteAsync(null);
        var editor = RowFor(vm, work.Id).Editor!;
        Assert.True(editor.CopyPublicKeyCommand.CanExecute(null));

        await editor.CopyPublicKeyCommand.ExecuteAsync(null);

        Assert.Equal("ssh-ed25519 AAAA work", _clipboard.Last);
        var note = Assert.Single(_notes.Published);
        Assert.Equal(NotificationSeverity.Success, note.Severity);
        Assert.Equal("Public key copied to the clipboard.", note.Message);
    }

    [Fact]
    public async Task A_key_that_vanished_warns_instead_of_copying()
    {
        var work = await AddProfile();
        _ssh.PublicKeys[work.Id] = "ssh-ed25519 AAAA work";
        using var vm = Create();
        await RowFor(vm, work.Id).EditCommand.ExecuteAsync(null);
        var editor = RowFor(vm, work.Id).Editor!;
        _ssh.PublicKeys.Remove(work.Id);

        await editor.CopyPublicKeyCommand.ExecuteAsync(null);

        Assert.Empty(_clipboard.Copied);
        var note = Assert.Single(_notes.Published);
        Assert.Equal(NotificationSeverity.Warning, note.Severity);
    }

    // ── GitHub ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Signing_in_with_github_opens_the_token_page_and_stores_the_pasted_token()
    {
        var work = await AddProfile();
        using var vm = Create();
        await RowFor(vm, work.Id).EditCommand.ExecuteAsync(null);
        var editor = RowFor(vm, work.Id).Editor!;
        Assert.Equal("Not connected.", editor.GitHubStatus);
        _dialogs.EnqueuePrompt("  ghp_secret  ");

        await editor.SignInGitHubCommand.ExecuteAsync(null);

        var url = Assert.Single(_launcher.Urls);
        Assert.StartsWith("https://github.com/settings/tokens/new", url);
        Assert.Equal("ghp_secret", await _profiles.GetTokenAsync(work.Id, RemoteHost.GitHub));
        Assert.StartsWith("Connected", editor.GitHubStatus);
        Assert.Equal("GitHub token stored.", Assert.Single(_notes.Published).Message);
    }

    [Fact]
    public async Task Cancelling_the_token_prompt_stores_nothing_and_stays_silent()
    {
        var work = await AddProfile();
        using var vm = Create();
        await RowFor(vm, work.Id).EditCommand.ExecuteAsync(null);
        var editor = RowFor(vm, work.Id).Editor!;
        _dialogs.EnqueuePrompt(null);

        await editor.SignInGitHubCommand.ExecuteAsync(null);

        Assert.Equal(0, _vault.Count);
        Assert.Empty(_notes.Published);
        Assert.Equal("Not connected.", editor.GitHubStatus);
    }

    // ── Delete ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Deleting_asks_first_and_declining_keeps_the_profile_silently()
    {
        var work = await AddProfile();
        using var vm = Create();
        _dialogs.EnqueueConfirmDestructive(false);

        await RowFor(vm, work.Id).DeleteCommand.ExecuteAsync(null);

        var call = Assert.Single(_dialogs.ConfirmDestructiveCalls);
        Assert.Equal("Delete profile", call.Title);
        Assert.Contains("'Work'", call.Effect);
        Assert.Equal("Delete", call.ConfirmLabel);
        Assert.NotNull(_profiles.GetProfileById(work.Id));
        Assert.Empty(_notes.Published);
    }

    [Fact]
    public async Task Deleting_removes_the_profile_and_says_so()
    {
        var work = await AddProfile();
        using var vm = Create();
        _dialogs.EnqueueConfirmDestructive(true);

        await RowFor(vm, work.Id).DeleteCommand.ExecuteAsync(null);

        Assert.Null(_profiles.GetProfileById(work.Id));
        var note = Assert.Single(_notes.Published);
        Assert.Equal(NotificationSeverity.Info, note.Severity);
        Assert.Equal("Profile deleted.", note.Message);
        Assert.DoesNotContain(vm.Rows, r => r.Profile.Id == work.Id);
    }

    [Fact]
    public async Task Deleting_the_default_says_repositories_fall_back_to_local_config()
    {
        var work = await AddProfile();
        await _profiles.SetDefaultProfileAsync(work.Id);
        using var vm = Create();
        _dialogs.EnqueueConfirmDestructive(true);

        await RowFor(vm, work.Id).DeleteCommand.ExecuteAsync(null);

        Assert.Contains("local Git config", Assert.Single(_notes.Published).Message);
        Assert.NotEqual(work.Id, _profiles.GetDefaultProfile()?.Id);
    }

    [Fact]
    public async Task The_global_profile_is_never_deleted()
    {
        using var vm = Create();
        var global = RowFor(vm, _profiles.GetGlobalProfile().Id);
        _dialogs.EnqueueConfirmDestructive(true);

        await vm.DeleteAsync(global.Profile);

        Assert.Equal("The global profile can't be deleted.", Assert.Single(_notes.Published).Message);
        Assert.Single(_profiles.GetAllProfiles());
    }

    // ── Set as default ───────────────────────────────────────────────────────

    [Fact]
    public async Task Cancelling_the_default_chooser_changes_nothing_and_stays_silent()
    {
        var work = await AddProfile();
        var before = _profiles.GetDefaultProfile()?.Id;
        using var vm = Create();
        _dialogs.EnqueueChoice(-1);

        await RowFor(vm, work.Id).SetDefaultCommand.ExecuteAsync(null);

        Assert.Equal("Set as default", Assert.Single(_dialogs.Calls).Title);
        Assert.Equal(before, _profiles.GetDefaultProfile()?.Id);
        Assert.Empty(_notes.Published);
    }

    [Fact]
    public async Task Following_the_default_sets_it_and_says_so()
    {
        var work = await AddProfile();
        using var vm = Create();
        _dialogs.EnqueueChoice(0);

        await RowFor(vm, work.Id).SetDefaultCommand.ExecuteAsync(null);

        Assert.Equal(work.Id, _profiles.GetDefaultProfile()?.Id);
        var note = Assert.Single(_notes.Published);
        Assert.Equal(NotificationSeverity.Info, note.Severity);
        Assert.Equal("'Work' is now the default profile.", note.Message);
        Assert.False(RowFor(vm, work.Id).CanSetDefault);
    }

    [Fact]
    public async Task Making_the_global_profile_the_default_needs_no_question()
    {
        var work = await AddProfile();
        await _profiles.SetDefaultProfileAsync(work.Id);
        using var vm = Create();

        await RowFor(vm, _profiles.GetGlobalProfile().Id).SetDefaultCommand.ExecuteAsync(null);

        Assert.Empty(_dialogs.Calls);
        Assert.NotEqual(work.Id, _profiles.GetDefaultProfile()?.Id);
    }

    // ── Close ────────────────────────────────────────────────────────────────

    [Fact]
    public void Close_goes_home_when_no_repository_is_focused()
    {
        using var vm = Create();
        var routes = new List<string>();
        _nav.NavigationRequested += routes.Add;

        vm.CloseCommand.Execute(null);

        Assert.Equal([""], routes);
    }
}
