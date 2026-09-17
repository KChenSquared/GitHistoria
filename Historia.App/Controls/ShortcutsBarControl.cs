namespace Historia.Controls;

/// <summary>
/// v8 M5: a single continuous bar between the app band and the three body panels — full window
/// width, independent of the panel splits, ~26 px (a little shorter than the 31 px app band).
/// Holds the repo network cluster (Fetch / Pull / Push + a repo-terminal button, centred) and
/// the network-mode button (right-aligned), all acting on the focused repository. The inline
/// network-progress box and push-rejection remedies stay in <c>RepositoryView</c>; this bar
/// drives them through <see cref="IRepoActionBus"/>.
/// </summary>
internal sealed class ShortcutsBarControl : UserControl, IDisposable
{
    private const double BarHeight = 24;

    private readonly IServiceProvider _sp;
    private readonly IWorkspaceService _workspace;
    private readonly IRepoLayoutService _repoLayout;
    private readonly IRepoActionBus _actions;
    private readonly IProfileService _profiles;
    private readonly ISettingsService _settings;
    private readonly INavigationBus _nav;
    private readonly Historia.Services.Notifications.INotificationService _notifications;
    private readonly Historia.Services.PowerTools.IStashService _stash;
    private readonly IDialogService _dialogs;

    private readonly Button _fetch;
    private readonly Button _pull;
    private readonly Button _push;
    private readonly Button _terminal;
    private readonly Button _branchBtn;
    private readonly Button _stashBtn;
    private readonly Button _profileBtn;      // §14.3 — per-repo profile dropdown (Local config + profiles)
    private readonly ToggleButton _sshOnly;   // §13 — per-repo "force SSH" toggle
    private readonly EventHandler<SettingsChangedEventArgs> _onSettings;
    private bool _syncingSshOnly;

    public ShortcutsBarControl(IServiceProvider sp)
    {
        _sp = sp;
        _workspace = sp.GetRequiredService<IWorkspaceService>();
        _repoLayout = sp.GetRequiredService<IRepoLayoutService>();
        _actions = sp.GetRequiredService<IRepoActionBus>();
        _profiles = sp.GetRequiredService<IProfileService>();
        _settings = sp.GetRequiredService<ISettingsService>();
        _nav = sp.GetRequiredService<INavigationBus>();
        _notifications = sp.GetRequiredService<Historia.Services.Notifications.INotificationService>();
        _stash = sp.GetRequiredService<Historia.Services.PowerTools.IStashService>();
        _dialogs = sp.GetRequiredService<IDialogService>();

        // v8 M5 (follow-up): icon + label per shortcut so the bar reads at a glance and the
        // labels don't crowd the 26 px band. Terminal matches for consistency.
        _fetch = IconTextButton("pet_supplies", "Fetch", "Fetch from the remote",
            () => _actions.RequestNetworkAction(RepoNetworkAction.Fetch));
        _pull = IconTextButton("cloud_download", "Pull", "Pull from the remote",
            () => _actions.RequestNetworkAction(RepoNetworkAction.Pull));
        _push = IconTextButton("cloud_upload", "Push", "Push to the remote",
            () => _actions.RequestNetworkAction(RepoNetworkAction.Push));
        _terminal = IconTextButton("terminal", "Terminal", "Open terminal in this repository", () =>
        {
            if (_workspace.Focused is { } ctx)
                _ = _sp.GetRequiredService<ITerminalSessionService>()
                       .OpenOrFocusAsync(AppBandControl.RepoOrigin(ctx));
        });

        // Branch — create a branch at the currently selected commit (HEAD when nothing is
        // selected). Stash — "stash all", named "Stash of <branch>" or a prompted name. Both use
        // the same chrome as Fetch / Pull / Push.
        _branchBtn = IconTextButton("graph_8", "Branch", "Create a branch at the selected commit",
            () => _ = CreateBranchAsync());
        _stashBtn = IconTextButton("save", "Stash", "Stash all changes",
            () => _ = StashAllAsync());

        var centre = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        centre.Children.Add(_fetch);
        centre.Children.Add(_pull);
        centre.Children.Add(_push);
        centre.Children.Add(_terminal);
        centre.Children.Add(_branchBtn);
        centre.Children.Add(_stashBtn);

        // §13: per-repo "force SSH" toggle. Off by default; on = never fall back to HTTPS.
        _sshOnly = new ToggleButton
        {
            Content = new TextBlock { Text = "SSH only", FontSize = 11, VerticalAlignment = VerticalAlignment.Center },
            Padding = new Thickness(8, 1),
            Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
        };
        ToolTip.SetTip(_sshOnly, "Force SSH for fetch / push / pull; never fall back to HTTPS.");
        _sshOnly.IsCheckedChanged += (_, _) => _ = OnSshOnlyToggledAsync();

        // §14.3: per-repo profile dropdown — Local config + every profile (global first).
        _profileBtn = new Button
        {
            Padding = new Thickness(10, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
        };
        ToolTip.SetTip(_profileBtn, "Identity used for this repository");
        _profileBtn.Click += (_, _) => OpenProfileMenu();

        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right };
        right.Children.Add(_profileBtn);
        right.Children.Add(_sshOnly);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,*") };
        Grid.SetColumn(centre, 1);
        Grid.SetColumn(right, 2);
        grid.Children.Add(centre);
        grid.Children.Add(right);

        Content = new Border
        {
            Height = BarHeight,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = grid,
        }.Dyn(Border.BackgroundProperty, "ColorChrome").Dyn(Border.BorderBrushProperty, "ColorBorder");
        Height = BarHeight;

        _onSettings = (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(Sync);
        _workspace.FocusedRepositoryChanged += OnFocusChanged;
        _settings.Changed += _onSettings;
        _profiles.ProfilesChanged += OnProfilesChanged;
        Sync();
    }

    private void OnFocusChanged(object? s, FocusedRepositoryChangedEventArgs e)
        => Avalonia.Threading.Dispatcher.UIThread.Post(Sync);

    private void OnProfilesChanged(object? s, EventArgs e)
        => Avalonia.Threading.Dispatcher.UIThread.Post(Sync);

    private void OpenProfileMenu()
    {
        if (_workspace.Focused is not { } ctx)
            return;

        var key = ctx.IdentityKey;
        var localSelected = key is { } k && _profiles.IsLocalConfigSelected(k);
        var active = ctx.State.ActiveProfile;

        var menu = new MenuFlyout();
        var menuItems = new List<Control>();

        var local = new MenuItem
        {
            Header = "Local config",
            Icon = localSelected ? new TextBlock { Text = "✓" } : null,
        };
        local.Click += (_, _) => _ = SelectLocalConfigAsync(ctx);
        menuItems.Add(local);
        menuItems.Add(new Separator());

        foreach (var p in _profiles.GetAllProfiles())
        {
            var pp = p;
            var mi = new MenuItem
            {
                Header = p.IsGlobal ? "Global (~/.gitconfig)" : p.DisplayName,
                Icon = !localSelected && active is not null && active.Id == p.Id ? new TextBlock { Text = "✓" } : null,
            };
            mi.Click += (_, _) => _ = BindProfileAsync(ctx, pp);
            menuItems.Add(mi);
        }
        menuItems.Add(new Separator());
        var add = new MenuItem { Header = "Add profile…" };
        add.Click += (_, _) => _nav.RequestNavigate("profiles?intent=add");
        menuItems.Add(add);
        var manage = new MenuItem { Header = "Manage profiles…" };
        manage.Click += (_, _) => _nav.RequestNavigate("profiles");
        menuItems.Add(manage);

        menu.ItemsSource = menuItems;
        menu.ShowAt(_profileBtn);
    }

    private async Task<RepoIdentityKey> KeyForAsync(RepositoryContext ctx)
        => ctx.IdentityKey ?? await _sp.GetRequiredService<IRepoIdentityService>().ResolveAsync(ctx, ctx.Lifetime);

    private async Task BindProfileAsync(RepositoryContext ctx, GitProfile profile)
    {
        try
        {
            var key = await KeyForAsync(ctx);
            await _profiles.BindProfileToRepositoryAsync(key, ctx.RepositoryPath, profile.Id, ctx.Lifetime);
            ctx.State.SetActiveProfile(profile);
            Sync();
        }
        catch (Exception ex)
        {
            _notifications.Publish(Historia.Services.Notifications.NotificationSeverity.Error,
                $"Could not set profile: {ex.Message}", ex.NotificationDetails());
        }
    }

    private async Task SelectLocalConfigAsync(RepositoryContext ctx)
    {
        try
        {
            var key = await KeyForAsync(ctx);
            await _profiles.SetLocalConfigForRepositoryAsync(key, ctx.RepositoryPath, ctx.Lifetime);
            ctx.State.SetActiveProfile(null);
            Sync();
        }
        catch (Exception ex)
        {
            _notifications.Publish(Historia.Services.Notifications.NotificationSeverity.Error,
                $"Could not set profile: {ex.Message}", ex.NotificationDetails());
        }
    }

    private async Task OnSshOnlyToggledAsync()
    {
        if (_syncingSshOnly || _workspace.Focused is not { } ctx)
            return;
        var on = _sshOnly.IsChecked == true;
        await _repoLayout.UpdateAsync(ctx.RepositoryPath, l => l with { SshOnly = on });
    }

    private void Sync()
    {
        var ctx = _workspace.Focused;
        var hasRepo = ctx is not null;
        _fetch.IsEnabled = _pull.IsEnabled = _push.IsEnabled = _terminal.IsEnabled = hasRepo;
        _branchBtn.IsEnabled = _stashBtn.IsEnabled = hasRepo;
        _profileBtn.IsVisible = _sshOnly.IsVisible = hasRepo;

        if (ctx is null)
            return;

        _syncingSshOnly = true;
        _sshOnly.IsChecked = _repoLayout.Get(ctx.RepositoryPath).SshOnly;
        _syncingSshOnly = false;

        // §14.3 stale rendering: a row that names a missing profile shows "<name> (unavailable)".
        var key = ctx.IdentityKey;
        string label;
        if (key is { } k && _profiles.IsLocalConfigSelected(k))
        {
            label = "Local config";
        }
        else if (ctx.State.ActiveProfile is { } p)
        {
            // Stale: the resolved profile was deleted since this tab last re-resolved (§14.3).
            label = _profiles.GetProfileById(p.Id) is null
                ? $"{p.DisplayName} (unavailable)"
                : p.IsGlobal ? "Local config" : p.DisplayName;
        }
        else
        {
            label = "Local config";
        }
        _profileBtn.Content = label + "  ▾";
    }

    /// <summary>A compact icon + label button sized for the 26 px bar.</summary>
    private static Button IconTextButton(string icon, string label, string tip, Action onClick)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, VerticalAlignment = VerticalAlignment.Center };
        content.Children.Add(IconGlyphPath(icon, 13, "ColorTextMuted"));
        content.Children.Add(new TextBlock { Text = label, FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
        var b = new Button
        {
            Content = content,
            Padding = new Thickness(7, 1),
            Background = Brushes.Transparent,
            BorderThickness = default,
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
        };
        // "flat" chrome (App.axaml): transparent ground, quiet 0.28-opacity disabled state — a
        // disabled shortcut fades its glyph/label instead of showing Fluent's grey box, matching
        // the Discard-all pill and the app-band / terminal-dock icon buttons.
        b.Classes.Add("flat");
        ToolTip.SetTip(b, tip);
        b.Click += (_, e) => { e.Handled = true; onClick(); };
        return b;
    }

    private async Task CreateBranchAsync()
    {
        if (_workspace.Focused is not { } ctx)
            return;
        var name = await _dialogs.PromptAsync("New branch", "Branch name:");
        if (string.IsNullOrWhiteSpace(name))
            return;
        name = name.Trim();

        var selected = ctx.State.SelectedCommitId;
        var startPoint = selected?.Sha ?? ctx.State.CurrentBranch?.CanonicalName ?? "HEAD";
        try
        {
            await ctx.Gateway.CreateBranchAsync(name, startPoint, checkout: false, ctx.Lifetime);
            await ctx.State.RefreshAllAsync(ctx.Lifetime);
            _notifications.Publish(Historia.Services.Notifications.NotificationSeverity.Success,
                selected is { } id ? $"Created {name} at {id.Sha[..7]}." : $"Created {name}.");
        }
        catch (Exception ex)
        {
            _notifications.Publish(Historia.Services.Notifications.NotificationSeverity.Error,
                $"Could not create branch: {ex.Message}", ex.NotificationDetails());
        }
    }

    private async Task StashAllAsync()
    {
        if (_workspace.Focused is not { } ctx)
            return;
        _stashBtn.IsEnabled = false;
        try
        {
            await StashActions.StashAllAsync(ctx, _stash, _settings, _dialogs, _notifications);
        }
        finally
        {
            _stashBtn.IsEnabled = _workspace.Focused is not null;
        }
    }

    public void Dispose()
    {
        _workspace.FocusedRepositoryChanged -= OnFocusChanged;
        _settings.Changed -= _onSettings;
        _profiles.ProfilesChanged -= OnProfilesChanged;
    }
}
