namespace Historia.ViewModels;

/// <summary>
/// Per-repository settings: the focused repository's identity (local config or a bound profile),
/// its SSH-only switch, and the application-wide graph-column editor. Everything is re-read when
/// the focused repository, the layout store or the profile list changes. The column editor
/// reorders live through <see cref="PreviewMoveColumn"/> and writes once, on
/// <see cref="CommitColumnOrderAsync"/>.
/// </summary>
public sealed partial class RepoSettingsViewModel : ViewModelBase
{
    private const string NoRepositoryTitle = "Repository settings";
    private const string LocalConfigLabel = "Local config";

    private readonly IWorkspaceService _workspace;
    private readonly IRepoLayoutService _repoLayout;
    private readonly IProfileService _profiles;
    private readonly IUiDispatcher _ui;
    private readonly INavigationBus? _nav;
    private readonly INotificationService? _notifications;
    private readonly IRepoIdentityService? _identity;

    private RepositoryContext? _focused;
    private bool _applying;
    private bool _syncingSshOnly;

    public RepoSettingsViewModel(
        IWorkspaceService workspace,
        IRepoLayoutService repoLayout,
        IProfileService profiles,
        IUiDispatcher ui,
        INavigationBus? nav = null,
        INotificationService? notifications = null,
        IRepoIdentityService? identity = null)
    {
        _workspace = workspace;
        _repoLayout = repoLayout;
        _profiles = profiles;
        _ui = ui;
        _nav = nav;
        _notifications = notifications;
        _identity = identity;

        Title = NoRepositoryTitle;
        ProfileLabel = LocalConfigLabel;
        Columns = [];
        Refresh();

        Subscribe(() => _workspace.FocusedRepositoryChanged += OnFocusChanged, () => _workspace.FocusedRepositoryChanged -= OnFocusChanged);
        Subscribe(() => _repoLayout.Changed += OnLayoutChanged, () => _repoLayout.Changed -= OnLayoutChanged);
        Subscribe(() => _profiles.ProfilesChanged += OnProfilesChanged, () => _profiles.ProfilesChanged -= OnProfilesChanged);
    }

    /// <summary>The repository's name, or a generic heading when none is focused.</summary>
    [ObservableProperty]
    public partial string Title { get; private set; }

    /// <summary>The focused repository's path, or null when none is focused.</summary>
    [ObservableProperty]
    public partial string? RepositoryPath { get; private set; }

    /// <summary>True while a repository is focused; the identity and network sections need one.</summary>
    [ObservableProperty]
    public partial bool HasRepository { get; private set; }

    /// <summary>Name of the identity the focused repository commits as.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProfileButtonText))]
    public partial string ProfileLabel { get; private set; }

    /// <summary>The identity button's caption: the label and a drop-down marker.</summary>
    public string ProfileButtonText => ProfileLabel + "  ▾";

    /// <summary>Whether the focused repository fetches, pulls and pushes over SSH only. Setting it
    /// writes the repository's layout; a value read back from the store does not write.</summary>
    [ObservableProperty]
    public partial bool SshOnly { get; set; }

    /// <summary>The graph columns in display order. Replaced as a whole on every refresh.</summary>
    [ObservableProperty]
    public partial ObservableCollection<ColumnRowViewModel> Columns { get; private set; }

    partial void OnSshOnlyChanged(bool value)
    {
        if (!_applying && _focused is not null)
            _ = SetSshOnlyAsync(value);
    }

    // The services raise on the thread that changed them; the re-read hops to the UI thread
    // because it replaces bound state.
    private void OnFocusChanged(object? sender, FocusedRepositoryChangedEventArgs e)
        => _ui.Post(Refresh);

    private void OnLayoutChanged(object? sender, RepoLayoutChangedEventArgs e)
    {
        if (!_syncingSshOnly)
            _ui.Post(Refresh);
    }

    private void OnProfilesChanged(object? sender, EventArgs e)
        => _ui.Post(Refresh);

    private void Refresh()
    {
        if (IsDisposed)
            return;

        var ctx = _workspace.Focused;
        _focused = ctx;
        Title = ctx?.DisplayName ?? NoRepositoryTitle;
        RepositoryPath = ctx?.RepositoryPath;
        HasRepository = ctx is not null;
        ProfileLabel = ctx is null ? LocalConfigLabel : ResolveProfileLabel(ctx);

        _applying = true;
        try { SshOnly = ctx is not null && _repoLayout.Get(ctx.RepositoryPath).SshOnly; }
        finally { _applying = false; }

        Columns = [.. _repoLayout.GlobalColumns.Select(c => new ColumnRowViewModel(this, c))];
    }

    // A row that names a profile that no longer exists reads "<name> (unavailable)".
    private string ResolveProfileLabel(RepositoryContext ctx)
    {
        if (ctx.IdentityKey is { } key && _profiles.IsLocalConfigSelected(key))
            return LocalConfigLabel;
        if (ctx.State.ActiveProfile is not { } profile)
            return LocalConfigLabel;
        if (_profiles.GetProfileById(profile.Id) is null)
            return $"{profile.DisplayName} (unavailable)";
        return profile.IsGlobal ? LocalConfigLabel : profile.DisplayName;
    }

    // ── identity ────────────────────────────────────────────────────────────────────────

    /// <summary>The entries of the identity menu for the focused repository: local config, every
    /// profile, then the add and manage shortcuts. The current identity is checked.</summary>
    public IReadOnlyList<ProfileMenuItemViewModel> BuildProfileMenu()
    {
        var ctx = _focused;
        var localSelected = ctx?.IdentityKey is { } key && _profiles.IsLocalConfigSelected(key);
        var active = ctx?.State.ActiveProfile;

        List<ProfileMenuItemViewModel> items =
        [
            ProfileMenuItemViewModel.Item(LocalConfigLabel, localSelected, SelectLocalConfigCommand),
            ProfileMenuItemViewModel.Separator(),
        ];
        foreach (var profile in _profiles.GetAllProfiles())
        {
            items.Add(ProfileMenuItemViewModel.Item(
                profile.IsGlobal ? "Global (~/.gitconfig)" : profile.DisplayName,
                !localSelected && active is not null && active.Id == profile.Id,
                BindProfileCommand,
                profile));
        }
        items.Add(ProfileMenuItemViewModel.Separator());
        items.Add(ProfileMenuItemViewModel.Item("Add profile…", false, AddProfileCommand));
        items.Add(ProfileMenuItemViewModel.Item("Manage profiles…", false, ManageProfilesCommand));
        return items;
    }

    [RelayCommand]
    private async Task BindProfileAsync(GitProfile profile)
    {
        if (_focused is not { } ctx)
            return;
        try
        {
            var key = await KeyForAsync(ctx);
            await _profiles.BindProfileToRepositoryAsync(key, ctx.RepositoryPath, profile.Id, ctx.Lifetime);
            ctx.State.SetActiveProfile(profile);
            Refresh();
        }
        catch (Exception ex)
        {
            ReportFailure("Could not set profile", ex);
        }
    }

    [RelayCommand]
    private async Task SelectLocalConfigAsync()
    {
        if (_focused is not { } ctx)
            return;
        try
        {
            var key = await KeyForAsync(ctx);
            await _profiles.SetLocalConfigForRepositoryAsync(key, ctx.RepositoryPath, ctx.Lifetime);
            ctx.State.SetActiveProfile(null);
            Refresh();
        }
        catch (Exception ex)
        {
            ReportFailure("Could not set profile", ex);
        }
    }

    [RelayCommand]
    private void AddProfile() => _nav?.RequestNavigate("profiles?intent=add");

    [RelayCommand]
    private void ManageProfiles() => _nav?.RequestNavigate("profiles");

    private async Task<RepoIdentityKey> KeyForAsync(RepositoryContext ctx)
        => ctx.IdentityKey ?? await (_identity?.ResolveAsync(ctx, ctx.Lifetime) ?? Task.FromResult(new RepoIdentityKey("")));

    // ── network ─────────────────────────────────────────────────────────────────────────

    /// <summary>Writes the SSH-only switch to the focused repository's layout. The layout change it
    /// raises does not re-read this view model, so the toggle keeps its place.</summary>
    public async Task SetSshOnlyAsync(bool on)
    {
        if (_focused is not { } ctx)
            return;
        _syncingSshOnly = true;
        try
        {
            await _repoLayout.UpdateAsync(ctx.RepositoryPath, l => l with { SshOnly = on });
        }
        catch (Exception ex)
        {
            ReportFailure("Could not update SSH only", ex);
        }
        finally
        {
            _syncingSshOnly = false;
        }
    }

    // ── graph columns ───────────────────────────────────────────────────────────────────

    // A column can be hidden only while exactly one visible column still absorbs the leftover
    // width and at least one stays visible; a refused change puts the checkbox back.
    internal void ApplyColumnVisibility(ColumnRowViewModel row, bool visible)
    {
        var next = _repoLayout.GlobalColumns
            .Select(c => c.Kind == row.Kind ? c with { Visible = visible } : c)
            .ToList();
        if (next.Count(c => c.Visible) == 0 || next.Count(c => c.Visible && c.IsFlex) != 1)
        {
            _ui.Post(() =>
            {
                if (!IsDisposed)
                    row.RestoreVisible(!visible);
            });
            return;
        }

        _ = UpdateColumnsAsync(() => _repoLayout.UpdateGlobalAsync(_ => GraphColumnDefaults.Normalize(next)));
    }

    /// <summary>Moves <paramref name="row"/> to <paramref name="toIndex"/> in the editor without
    /// saving, for the live preview during a drag. Returns false when it is already there.</summary>
    public bool PreviewMoveColumn(ColumnRowViewModel row, int toIndex)
    {
        var from = Columns.IndexOf(row);
        toIndex = Math.Clamp(toIndex, 0, Columns.Count - 1);
        if (from < 0 || from == toIndex)
            return false;
        Columns.Move(from, toIndex);
        return true;
    }

    /// <summary>Saves the order the editor currently shows as the application-wide column order.</summary>
    public Task CommitColumnOrderAsync()
    {
        var order = Columns.Select(r => r.Kind).ToList();
        return UpdateColumnsAsync(() =>
        {
            var byKind = _repoLayout.GlobalColumns.ToDictionary(c => c.Kind);
            var reordered = order.Where(byKind.ContainsKey).Select(k => byKind[k]).ToList();
            return _repoLayout.UpdateGlobalAsync(_ => GraphColumnDefaults.Normalize(reordered));
        });
    }

    /// <summary>Drops a preview that was not saved and shows the stored order again.</summary>
    public void CancelColumnReorder()
    {
        foreach (var row in Columns)
            row.IsDragging = false;
        Columns = [.. _repoLayout.GlobalColumns.Select(c => new ColumnRowViewModel(this, c))];
    }

    [RelayCommand]
    private Task ResetColumnsAsync()
    {
        var path = _focused?.RepositoryPath ?? "";
        return UpdateColumnsAsync(async () =>
        {
            await _repoLayout.UpdateGlobalAsync(_ => GraphColumnDefaults.All);
            if (path.Length > 0)
            {
                await _repoLayout.UpdateAsync(path, l => l with
                {
                    Columns = GraphColumnDefaults.All,
                    GraphWidthPinned = false,
                });
            }
        });
    }

    private async Task UpdateColumnsAsync(Func<Task> write)
    {
        try
        {
            await write();
        }
        catch (Exception ex)
        {
            ReportFailure("Could not update graph columns", ex);
        }
    }

    private void ReportFailure(string what, Exception ex)
        => _notifications?.Publish(NotificationSeverity.Error, $"{what}: {ex.Message}", ex.NotificationDetails());
}
