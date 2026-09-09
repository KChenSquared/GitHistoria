namespace Historia.Views;

/// <summary>
/// Standalone "Profiles &amp; Accounts" panel, reached from the app menu's
/// <c>Profile ▸ Manage profiles…</c> entry (route <c>profiles</c>). Lists the configured Git
/// identity profiles (the reserved global profile first, §14.1) with edit / set-as-default /
/// delete / add actions. Editing the global profile writes <c>~/.gitconfig</c> behind two
/// warnings; it can never be deleted.
/// </summary>
public sealed class ProfilesView : UserControl, IDisposable
{
    private readonly IServiceProvider _sp;
    private readonly IProfileService _profiles;
    private readonly ISettingsService _settings;
    private readonly IDialogService _dialogs;
    private readonly INotificationService _notifications;
    private readonly ISshKeyService _ssh;
    private readonly IExternalLauncher _launcher;
    private readonly StackPanel _root = new() { Spacing = 12, Margin = new Thickness(32), MaxWidth = 640 };
    private readonly EventHandler _onChanged;
    private readonly EventHandler<SettingsChangedEventArgs> _onSettings;

    // null = not editing; Guid.Empty = the "add profile" form is open; else the row being edited.
    private Guid? _editing;

    public ProfilesView(IServiceProvider sp, string? intent = null)
    {
        _sp = sp;
        _profiles = sp.GetRequiredService<IProfileService>();
        _settings = sp.GetRequiredService<ISettingsService>();
        _dialogs = sp.GetRequiredService<IDialogService>();
        _notifications = sp.GetRequiredService<INotificationService>();
        _ssh = sp.GetRequiredService<ISshKeyService>();
        _launcher = sp.GetRequiredService<IExternalLauncher>();

        if (string.Equals(intent, "add", StringComparison.OrdinalIgnoreCase))
            _editing = Guid.Empty;

        _onChanged = (_, _) => Post();
        _onSettings = (_, _) => Post();

        Content = ClosablePane(new ScrollViewer { Content = _root }, sp);
        this.Dyn(BackgroundProperty, "ColorChrome");
        _profiles.ProfilesChanged += _onChanged;
        _settings.Changed += _onSettings;
        Rebuild();
    }

    private void Post() => Avalonia.Threading.Dispatcher.UIThread.Post(Rebuild);

    private void Rebuild()
    {
        var c = _settings.Current;
        _root.Children.Clear();
        _root.Children.Add(Text("Profiles & Accounts", "ColorText", 24, FontWeight.SemiBold));
        _root.Children.Add(Text(
            "Git identity profiles Historia can apply per repository.", "ColorTextMuted", 13));

        var defaultId = _profiles.GetDefaultProfile()?.Id;
        foreach (var p in _profiles.GetAllProfiles())
        {
            var pp = p;
            if (_editing == p.Id)
            {
                _root.Children.Add(Card(BuildEditor(p), 12));
                continue;
            }

            var tag = p.IsGlobal ? "  (global)" : p.Id == defaultId ? "  (default)" : "";
            var line = new StackPanel { Spacing = 2 };
            line.Children.Add(Text($"{p.DisplayName}{tag}", "ColorText", 13, FontWeight.SemiBold));
            var identity = string.IsNullOrWhiteSpace(p.GitUserName) && string.IsNullOrWhiteSpace(p.GitUserEmail)
                ? (p.IsGlobal ? "no ~/.gitconfig identity set" : "no identity")
                : $"{p.GitUserName} <{p.GitUserEmail}>";
            line.Children.Add(Text(identity, "ColorTextMuted", 11));
            var badges = new List<string>();
            if (_ssh.PathsFor(p.Id).Exists || !string.IsNullOrWhiteSpace(p.SshKeyPath)) badges.Add("SSH key");
            if (!string.IsNullOrWhiteSpace(p.GitHubTokenRef)) badges.Add("GitHub connected");
            if (p.SignCommits) badges.Add("signs commits");
            if (badges.Count > 0)
                line.Children.Add(Text(string.Join("  ·  ", badges), "ColorTextFaint", 11));

            var acts = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 2, 0, 0) };
            acts.Children.Add(TextButton("Edit", () => _ = BeginEditAsync(pp)));
            if (p.Id != defaultId)
                acts.Children.Add(TextButton("Set as default", () => _ = SetDefaultAsync(pp)));
            if (!p.IsGlobal)
                acts.Children.Add(TextButton("Delete", () => _ = DeleteProfileAsync(pp)));
            line.Children.Add(acts);
            _root.Children.Add(Card(line, 10));
        }

        if (_editing == Guid.Empty)
            _root.Children.Add(Card(BuildEditor(null), 12));
        else
            _root.Children.Add(TextButton("+ Add profile", () => { _editing = Guid.Empty; Rebuild(); }, primary: true));
    }

    // ── v8 M11: create / edit wizard (inline form) ──

    private static Control Field(string label, Control control)
    {
        var s = new StackPanel { Spacing = 2 };
        s.Children.Add(Text(label, "ColorTextMuted", 11));
        control.HorizontalAlignment = HorizontalAlignment.Stretch;
        s.Children.Add(control);
        return s;
    }

    private Control BuildEditor(GitProfile? existing)
    {
        var creating = existing is null;
        var box = new StackPanel { Spacing = 8 };
        box.Children.Add(Text(creating ? "New profile" : $"Edit {existing!.DisplayName}", "ColorText", 15, FontWeight.SemiBold));

        var name = new TextBox { Watermark = "Display name (e.g. Personal, Work)", Text = existing?.DisplayName ?? "" };
        var user = new TextBox { Watermark = "Git user.name", Text = existing?.GitUserName ?? "" };
        var email = new TextBox { Watermark = "Git user.email", Text = existing?.GitUserEmail ?? "" };
        box.Children.Add(Field("Display name", name));
        box.Children.Add(Field("user.name", user));
        box.Children.Add(Field("user.email", email));

        // ── SSH key ──
        box.Children.Add(Text("SSH key", "ColorTextMuted", 12, FontWeight.SemiBold));
        var keyStatus = Text("", "ColorTextFaint", 11);
        keyStatus.TextWrapping = TextWrapping.Wrap;
        var keyRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        Button? copyBtn = null;

        void RefreshKeyStatus()
        {
            SshKeyInfo? info = existing is null ? null : _ssh.PathsFor(existing.Id);
            if (info is { Exists: true })
            {
                keyStatus.Text = $"Private: {info.PrivatePath}\nPublic:  {info.PublicPath}";
                if (copyBtn is not null) copyBtn.IsEnabled = true;
            }
            else
            {
                keyStatus.Text = creating
                    ? "Save the profile first, then generate a key."
                    : "No Historia-managed key yet.";
                if (copyBtn is not null) copyBtn.IsEnabled = false;
            }
        }

        var genBtn = TextButton("Generate SSH key", () => _ = GenerateKeyAsync(existing, email.Text ?? "", RefreshKeyStatus));
        genBtn.IsEnabled = !creating;
        copyBtn = TextButton("Copy public key", () => _ = CopyPublicKeyAsync(existing));
        keyRow.Children.Add(genBtn);
        keyRow.Children.Add(copyBtn);
        box.Children.Add(keyRow);
        box.Children.Add(keyStatus);
        RefreshKeyStatus();

        // ── Signing ──
        box.Children.Add(Text("Commit signing", "ColorTextMuted", 12, FontWeight.SemiBold));
        var signKey = new TextBox { Watermark = "GPG key id — 8–40 hex characters (optional)", Text = existing?.GpgSigningKeyId ?? "" };
        var signCommits = new CheckBox { Content = "Sign commits made under this profile", IsChecked = existing?.SignCommits ?? false };
        box.Children.Add(Field("Signing key", signKey));
        box.Children.Add(signCommits);

        // ── GitHub account ──
        box.Children.Add(Text("GitHub", "ColorTextMuted", 12, FontWeight.SemiBold));
        var ghStatus = Text(
            string.IsNullOrWhiteSpace(existing?.GitHubTokenRef) ? "Not connected." : "Connected — a token is stored in the vault.",
            "ColorTextFaint", 11);
        var ghBtn = TextButton("Sign in with GitHub", () => _ = SignInGitHubAsync(existing, ghStatus));
        box.Children.Add(ghBtn);
        box.Children.Add(ghStatus);

        // ── Save / Cancel ──
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 4, 0, 0) };
        actions.Children.Add(TextButton(creating ? "Create profile" : "Save", () => _ = SaveEditorAsync(
            existing, name.Text ?? "", user.Text ?? "", email.Text ?? "", signKey.Text ?? "", signCommits.IsChecked == true),
            primary: true));
        actions.Children.Add(TextButton("Cancel", () => { _editing = null; Rebuild(); }));
        box.Children.Add(actions);

        return box;
    }

    /// <summary>§14.1: editing the global profile changes the machine-wide identity in
    /// <c>~/.gitconfig</c>, so it is gated by a warning before the editor even opens.</summary>
    private async Task BeginEditAsync(GitProfile p)
    {
        if (p.IsGlobal && !await _dialogs.ConfirmAsync(
            "Edit the global Git identity?",
            "Editing the global profile changes your machine-wide Git identity in ~/.gitconfig. "
            + "This affects every Git tool on this machine, not just Historia.",
            destructive: true, "Continue", defaultToCancel: true, icon: DialogIcon.Warning))
            return;
        _editing = p.Id;
        Rebuild();
    }

    private async Task SaveEditorAsync(GitProfile? existing, string name, string user, string email, string signKey, bool sign)
    {
        try
        {
            if (existing is { IsGlobal: true })
            {
                if (!await _dialogs.ConfirmAsync(
                    "Write to ~/.gitconfig?",
                    $"Write {user.Trim()} <{email.Trim()}> to ~/.gitconfig now? This affects all Git tools.",
                    destructive: true, "Write", defaultToCancel: true, icon: DialogIcon.Warning))
                    return;

                var cli = _sp.GetRequiredService<Historia.Services.Git.IGitCliService>();
                var home = HomeDirectory;
                await cli.RunAsync(new Historia.Models.GitCliRequest
                {
                    Arguments = ["config", "--global", "user.name", user.Trim()],
                    WorkingDirectory = home,
                });
                await cli.RunAsync(new Historia.Models.GitCliRequest
                {
                    Arguments = ["config", "--global", "user.email", email.Trim()],
                    WorkingDirectory = home,
                });
                await _profiles.SyncGlobalIdentityAsync();
                _notifications.Publish(NotificationSeverity.Success, "Global Git identity updated.");
                _editing = null;
                Rebuild();
                return;
            }

            if (existing is null)
            {
                await _profiles.CreateProfileAsync(new GitProfileCreateRequest
                {
                    DisplayName = name.Trim(),
                    GitUserName = user.Trim(),
                    GitUserEmail = email.Trim(),
                    GpgSigningKeyId = string.IsNullOrWhiteSpace(signKey) ? null : signKey.Trim(),
                    SignCommits = sign,
                });
                _notifications.Publish(NotificationSeverity.Success, $"Profile '{name.Trim()}' created.");
            }
            else
            {
                await _profiles.UpdateProfileAsync(existing.Id, new GitProfileUpdateRequest
                {
                    DisplayName = name.Trim(),
                    GitUserName = user.Trim(),
                    GitUserEmail = email.Trim(),
                    GpgSigningKeyId = string.IsNullOrWhiteSpace(signKey) ? "" : signKey.Trim(),
                    SignCommits = sign,
                });
                _notifications.Publish(NotificationSeverity.Success, "Profile saved.");
            }
            _editing = null;
            Rebuild();
        }
        catch (Exception ex) { _notifications.Publish(NotificationSeverity.Error, ex.Message); }
    }

    private async Task GenerateKeyAsync(GitProfile? existing, string email, Action refresh)
    {
        if (existing is null) return;
        if (!await _dialogs.ConfirmAsync("Generate SSH key",
                "Generate a new ed25519 key for this profile? Any existing Historia-managed key for it is replaced. The key has no passphrase.",
                destructive: false, "Generate"))
            return;
        try
        {
            var info = await _ssh.GenerateEd25519Async(existing.Id, string.IsNullOrWhiteSpace(email) ? existing.GitUserEmail : email);
            await _profiles.UpdateProfileAsync(existing.Id, new GitProfileUpdateRequest { SshKeyPath = info.PrivatePath });
            refresh();
            _notifications.Publish(NotificationSeverity.Success, "SSH key generated.");
        }
        catch (Exception ex) { _notifications.Publish(NotificationSeverity.Error, ex.Message); }
    }

    private async Task CopyPublicKeyAsync(GitProfile? existing)
    {
        if (existing is null) return;
        var pub = await _ssh.ReadPublicKeyAsync(existing.Id);
        if (string.IsNullOrWhiteSpace(pub))
        {
            _notifications.Publish(NotificationSeverity.Warning, "No public key to copy — generate one first.");
            return;
        }
        await (TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(pub) ?? Task.CompletedTask);
        _notifications.Publish(NotificationSeverity.Success, "Public key copied to the clipboard.");
    }

    private async Task SignInGitHubAsync(GitProfile? existing, TextBlock status)
    {
        // Device-flow OAuth needs a registered GitHub client id, which Historia does not ship;
        // until then this opens GitHub's token page and stores a pasted PAT in the vault (the
        // same fallback GitHub Desktop offers). All network is user-initiated (Rule 0.2).
        _launcher.OpenUrl("https://github.com/settings/tokens/new?scopes=repo&description=Historia");
        var pat = await _dialogs.PromptAsync("Sign in with GitHub",
            "Paste a GitHub personal access token (it is stored in the OS credential vault, never in profiles.json):");
        if (string.IsNullOrWhiteSpace(pat))
            return;
        try
        {
            if (existing is null)
            {
                _notifications.Publish(NotificationSeverity.Warning, "Create the profile first, then connect GitHub.");
                return;
            }
            await _profiles.UpdateProfileAsync(existing.Id, new GitProfileUpdateRequest { GitHubToken = pat.Trim() });
            status.Text = "Connected — a token is stored in the vault.";
            _notifications.Publish(NotificationSeverity.Success, "GitHub token stored.");
        }
        catch (Exception ex) { _notifications.Publish(NotificationSeverity.Error, ex.Message); }
    }

    private async Task SetDefaultAsync(GitProfile p)
    {
        if (p.IsGlobal)
        {
            await _profiles.SetDefaultProfileAsync(null);
            return;
        }

        // §14.2: choosing a non-global default asks whether row-less repos follow it.
        var choice = await _dialogs.ChooseAsync(
            "Set as default",
            $"Make repositories that don't have their own profile use '{p.DisplayName}'?",
            ["Follow the default", "Only new repositories"]);

        if (choice == 1)
            await PinExistingRowlessReposAsync();

        await _profiles.SetDefaultProfileAsync(p.Id);
        _notifications.Publish(NotificationSeverity.Info, $"'{p.DisplayName}' is now the default profile.");
    }

    /// <summary>§14.2 "Only new repositories": pin every known (recent) repo that has no explicit
    /// selection row to its current resolution, so it keeps that identity when the default moves.</summary>
    private async Task PinExistingRowlessReposAsync()
    {
        var recents = _sp.GetRequiredService<Historia.Services.Workspace.IRecentRepositoryService>();
        var identity = _sp.GetRequiredService<IRepoIdentityService>();
        foreach (var entry in recents.Items)
        {
            if (entry.IsStale)
                continue;
            try
            {
                var path = entry.Path;
                var (key, resolved, hasRow) = await Task.Run(async () =>
                {
                    await using var ctx = RepositoryContext.Open(path, _sp);
                    var k = await identity.ResolveAsync(ctx);
                    var row = _profiles.GetProfileForRepository(k) is not null || _profiles.IsLocalConfigSelected(k);
                    return (k, _profiles.ResolveActiveProfile(k), row);
                });

                if (hasRow)
                    continue;
                if (resolved is null)
                    await _profiles.SetLocalConfigForRepositoryAsync(key, path);
                else
                    await _profiles.BindProfileToRepositoryAsync(key, path, resolved.Id);
            }
            catch
            {
                // A repo that can't be opened is simply skipped.
            }
        }
    }

    private async Task DeleteProfileAsync(GitProfile p)
    {
        if (!await _dialogs.ConfirmAsync("Delete profile",
            $"Delete '{p.DisplayName}'? Its stored tokens and repository selections are removed.", destructive: true, "Delete"))
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

    public void Dispose()
    {
        _profiles.ProfilesChanged -= _onChanged;
        _settings.Changed -= _onSettings;
    }
}
