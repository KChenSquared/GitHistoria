namespace Historia.Tests;

/// <summary>
/// The repository settings view model: identity label and menu, the SSH-only switch, and the
/// graph-column editor (visibility, live reorder, save, reset). The services are fakes and the
/// repository context is a real, tiny repository.
/// </summary>
[Trait("tier", "git")]
public sealed class RepoSettingsViewModelTests : IAsyncDisposable
{
    private readonly MiniHost _host = new();
    private readonly List<TestRepositoryBuilder> _repos = [];

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
        foreach (var repo in _repos)
            repo.Dispose();
    }

    private RepositoryContext OpenRepo()
    {
        var repo = TestRepositoryBuilder.FromTemplate("repo-settings-seed", b =>
        {
            b.WriteFile("a.txt", "x\n").Stage();
            b.Commit("c1");
        });
        _repos.Add(repo);
        var ctx = _host.Open(repo.Path);
        ctx.IdentityKey = new RepoIdentityKey("repo-settings-key");
        return ctx;
    }

    private static GitProfile Profile(string name, bool isGlobal = false) => new()
    {
        Id = Guid.NewGuid(),
        DisplayName = name,
        GitUserName = name.ToLowerInvariant(),
        GitUserEmail = name.ToLowerInvariant() + "@test.com",
        IsGlobal = isGlobal,
    };

    private sealed class Rig
    {
        public Rig(IUiDispatcher? ui = null)
        {
            Ui = ui ?? new ImmediateUiDispatcher();
            Vm = new RepoSettingsViewModel(Workspace, Layout, Profiles, Ui, Nav, Notifications);
        }

        public FakeWorkspace Workspace { get; } = new();
        public FakeLayout Layout { get; } = new();
        public FakeProfiles Profiles { get; } = new();
        public NavigationBus Nav { get; } = new();
        public FakeNotificationService Notifications { get; } = new();
        public IUiDispatcher Ui { get; }
        public RepoSettingsViewModel Vm { get; }
    }

    // ── identity ─────────────────────────────────────────────────────────────

    [Fact]
    public void With_no_repository_only_the_column_editor_has_content()
    {
        var rig = new Rig();

        Assert.False(rig.Vm.HasRepository);
        Assert.Equal("Repository settings", rig.Vm.Title);
        Assert.Null(rig.Vm.RepositoryPath);
        Assert.Equal(GraphColumnDefaults.All.Select(c => c.Kind), rig.Vm.Columns.Select(c => c.Kind));
    }

    [Fact]
    public void A_focused_repository_shows_its_name_path_and_local_config()
    {
        var ctx = OpenRepo();
        var rig = new Rig();
        rig.Workspace.ChangeFocus(ctx);

        Assert.True(rig.Vm.HasRepository);
        Assert.Equal(ctx.DisplayName, rig.Vm.Title);
        Assert.Equal(ctx.RepositoryPath, rig.Vm.RepositoryPath);
        Assert.Equal("Local config", rig.Vm.ProfileLabel);
        Assert.Equal("Local config  ▾", rig.Vm.ProfileButtonText);
        Assert.False(rig.Vm.SshOnly);
    }

    [Fact]
    public void The_label_names_the_bound_profile_or_marks_a_missing_one_unavailable()
    {
        var ctx = OpenRepo();
        var rig = new Rig();
        var known = Profile("Dev");
        rig.Profiles.Add(known);
        ctx.State.SetActiveProfile(known);
        rig.Workspace.ChangeFocus(ctx);
        Assert.Equal("Dev", rig.Vm.ProfileLabel);

        ctx.State.SetActiveProfile(Profile("Gone"));
        rig.Workspace.ChangeFocus(ctx);
        Assert.Equal("Gone (unavailable)", rig.Vm.ProfileLabel);
    }

    [Fact]
    public void A_global_profile_and_a_selected_local_config_both_read_local_config()
    {
        var ctx = OpenRepo();
        var rig = new Rig();
        var global = Profile("Global", isGlobal: true);
        rig.Profiles.Add(global);
        ctx.State.SetActiveProfile(global);
        rig.Workspace.ChangeFocus(ctx);
        Assert.Equal("Local config", rig.Vm.ProfileLabel);

        var other = Profile("Dev");
        rig.Profiles.Add(other);
        ctx.State.SetActiveProfile(other);
        rig.Profiles.LocalConfigSelected = true;
        rig.Workspace.ChangeFocus(ctx);
        Assert.Equal("Local config", rig.Vm.ProfileLabel);
    }

    [Fact]
    public void Profiles_changing_refreshes_the_label()
    {
        var ctx = OpenRepo();
        var rig = new Rig();
        var dev = Profile("Dev");
        rig.Profiles.Add(dev);
        ctx.State.SetActiveProfile(dev);
        rig.Workspace.ChangeFocus(ctx);
        Assert.Equal("Dev", rig.Vm.ProfileLabel);

        rig.Profiles.Remove(dev);

        Assert.Equal("Dev (unavailable)", rig.Vm.ProfileLabel);
    }

    [Fact]
    public async Task Binding_a_profile_saves_the_binding_and_shows_the_profile()
    {
        var ctx = OpenRepo();
        var rig = new Rig();
        var dev = Profile("Dev");
        rig.Profiles.Add(dev);
        rig.Workspace.ChangeFocus(ctx);

        await rig.Vm.BindProfileCommand.ExecuteAsync(dev);

        var bound = Assert.Single(rig.Profiles.Bound);
        Assert.Equal((new RepoIdentityKey("repo-settings-key"), ctx.RepositoryPath, dev.Id), bound);
        Assert.Equal(dev.Id, ctx.State.ActiveProfile?.Id);
        Assert.Equal("Dev", rig.Vm.ProfileLabel);
    }

    [Fact]
    public async Task Choosing_local_config_saves_it_and_clears_the_active_profile()
    {
        var ctx = OpenRepo();
        var rig = new Rig();
        var dev = Profile("Dev");
        rig.Profiles.Add(dev);
        ctx.State.SetActiveProfile(dev);
        rig.Workspace.ChangeFocus(ctx);

        await rig.Vm.SelectLocalConfigCommand.ExecuteAsync(null);

        Assert.Equal(ctx.RepositoryPath, Assert.Single(rig.Profiles.LocalConfigFor));
        Assert.Null(ctx.State.ActiveProfile);
        Assert.Equal("Local config", rig.Vm.ProfileLabel);
    }

    [Fact]
    public async Task A_failing_bind_is_reported_and_leaves_the_identity_alone()
    {
        var ctx = OpenRepo();
        var rig = new Rig();
        var dev = Profile("Dev");
        rig.Profiles.Add(dev);
        rig.Profiles.FailBinding = new InvalidOperationException("disk full");
        rig.Workspace.ChangeFocus(ctx);

        await rig.Vm.BindProfileCommand.ExecuteAsync(dev);

        var note = Assert.Single(rig.Notifications.Published);
        Assert.Equal(NotificationSeverity.Error, note.Severity);
        Assert.Equal("Could not set profile: disk full", note.Message);
        Assert.Null(ctx.State.ActiveProfile);
    }

    [Fact]
    public void Binding_with_no_focused_repository_does_nothing()
    {
        var rig = new Rig();

        rig.Vm.BindProfileCommand.Execute(Profile("Dev"));

        Assert.Empty(rig.Profiles.Bound);
        Assert.Empty(rig.Notifications.Published);
    }

    [Fact]
    public void The_menu_lists_local_config_every_profile_and_the_shortcuts_with_the_current_one_checked()
    {
        var ctx = OpenRepo();
        var rig = new Rig();
        var dev = Profile("Dev");
        var global = Profile("Global", isGlobal: true);
        rig.Profiles.Add(dev);
        rig.Profiles.Add(global);
        ctx.State.SetActiveProfile(dev);
        rig.Workspace.ChangeFocus(ctx);

        var menu = rig.Vm.BuildProfileMenu();

        Assert.Equal(
            ["Local config", "", "Dev", "Global (~/.gitconfig)", "", "Add profile…", "Manage profiles…"],
            menu.Select(i => i.Header));
        Assert.Equal([false, false, true, false, false, false, false], menu.Select(i => i.IsChecked));
        Assert.Equal([false, true, false, false, true, false, false], menu.Select(i => i.IsSeparator));
        Assert.Same(dev, menu[2].CommandParameter);
    }

    [Fact]
    public void The_menu_checks_local_config_when_it_is_selected()
    {
        var ctx = OpenRepo();
        var rig = new Rig();
        var dev = Profile("Dev");
        rig.Profiles.Add(dev);
        ctx.State.SetActiveProfile(dev);
        rig.Profiles.LocalConfigSelected = true;
        rig.Workspace.ChangeFocus(ctx);

        var menu = rig.Vm.BuildProfileMenu();

        Assert.True(menu[0].IsChecked);
        Assert.False(menu[2].IsChecked);
    }

    [Fact]
    public void The_menu_shortcuts_navigate_to_the_profiles_page()
    {
        var rig = new Rig();
        var routes = new List<string>();
        rig.Nav.NavigationRequested += routes.Add;
        var menu = rig.Vm.BuildProfileMenu();

        menu[^2].Command!.Execute(menu[^2].CommandParameter);
        menu[^1].Command!.Execute(menu[^1].CommandParameter);

        Assert.Equal(["profiles?intent=add", "profiles"], routes);
    }

    // ── network ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_switch_reads_the_stored_value_without_writing_it_back()
    {
        var ctx = OpenRepo();
        var rig = new Rig();
        await rig.Layout.UpdateAsync(ctx.RepositoryPath, l => l with { SshOnly = true });
        rig.Layout.Writes = 0;

        rig.Workspace.ChangeFocus(ctx);

        Assert.True(rig.Vm.SshOnly);
        Assert.Equal(0, rig.Layout.Writes);
    }

    [Fact]
    public void Turning_the_switch_on_writes_the_repository_layout_and_keeps_the_toggle()
    {
        var ctx = OpenRepo();
        var ui = new RecordingUiDispatcher();
        var rig = new Rig(ui);
        rig.Workspace.ChangeFocus(ctx);
        ui.DrainPosted();

        rig.Vm.SshOnly = true;
        ui.DrainPosted();

        Assert.True(rig.Layout.Get(ctx.RepositoryPath).SshOnly);
        Assert.Equal(1, rig.Layout.Writes);
        Assert.True(rig.Vm.SshOnly);
    }

    [Fact]
    public void The_write_the_switch_causes_does_not_refresh_the_view_model()
    {
        var ctx = OpenRepo();
        var ui = new RecordingUiDispatcher();
        var rig = new Rig(ui);
        rig.Workspace.ChangeFocus(ctx);
        ui.DrainPosted();
        var columns = rig.Vm.Columns;
        var posts = ui.PostCount;

        rig.Vm.SshOnly = true;

        Assert.Equal(posts, ui.PostCount);
        Assert.Same(columns, rig.Vm.Columns);
    }

    [Fact]
    public async Task A_layout_change_from_elsewhere_refreshes_the_switch()
    {
        var ctx = OpenRepo();
        var rig = new Rig();
        rig.Workspace.ChangeFocus(ctx);
        Assert.False(rig.Vm.SshOnly);

        await rig.Layout.UpdateAsync(ctx.RepositoryPath, l => l with { SshOnly = true });

        Assert.True(rig.Vm.SshOnly);
    }

    [Fact]
    public void A_failing_ssh_write_is_reported()
    {
        var ctx = OpenRepo();
        var rig = new Rig();
        rig.Workspace.ChangeFocus(ctx);
        rig.Layout.Fail = new IOException("read-only");

        rig.Vm.SshOnly = true;

        var note = Assert.Single(rig.Notifications.Published);
        Assert.Equal("Could not update SSH only: read-only", note.Message);
    }

    // ── threading ────────────────────────────────────────────────────────────

    [Fact]
    public void Service_events_reach_the_view_model_through_the_ui_dispatcher()
    {
        var ctx = OpenRepo();
        var ui = new RecordingUiDispatcher();
        var rig = new Rig(ui);

        rig.Workspace.ChangeFocus(ctx);
        Assert.False(rig.Vm.HasRepository);
        ui.DrainPosted();
        Assert.True(rig.Vm.HasRepository);

        var dev = Profile("Dev");
        rig.Profiles.Add(dev);
        ctx.State.SetActiveProfile(dev);
        Assert.Equal("Local config", rig.Vm.ProfileLabel);
        ui.DrainPosted();
        Assert.Equal("Dev", rig.Vm.ProfileLabel);

        rig.Layout.RaiseGlobalChanged();
        var before = rig.Vm.Columns;
        ui.DrainPosted();
        Assert.NotSame(before, rig.Vm.Columns);
    }

    [Fact]
    public void A_disposed_view_model_ignores_the_services_and_releases_them()
    {
        var ctx = OpenRepo();
        var ui = new RecordingUiDispatcher();
        var rig = new Rig(ui);
        rig.Workspace.ChangeFocus(ctx);
        ui.DrainPosted();

        rig.Vm.Dispose();
        var posts = ui.PostCount;
        rig.Workspace.ChangeFocus(null);
        rig.Layout.RaiseGlobalChanged();
        rig.Profiles.Add(Profile("Dev"));

        Assert.Equal(posts, ui.PostCount);
        Assert.Equal(0, rig.Workspace.Subscribers);
        Assert.Equal(0, rig.Layout.Subscribers);
        Assert.Equal(0, rig.Profiles.Subscribers);
        ui.DrainPosted();
        Assert.True(rig.Vm.HasRepository);
    }

    [Fact]
    public void A_post_that_runs_after_dispose_changes_nothing()
    {
        var ctx = OpenRepo();
        var ui = new RecordingUiDispatcher();
        var rig = new Rig(ui);
        rig.Workspace.ChangeFocus(ctx);

        rig.Vm.Dispose();
        ui.DrainPosted();

        Assert.False(rig.Vm.HasRepository);
    }

    // ── graph columns ────────────────────────────────────────────────────────

    private static IEnumerable<GraphColumnKind> Kinds(IEnumerable<GraphColumn> columns) => columns.Select(c => c.Kind);

    [Fact]
    public async Task Moving_a_column_reorders_the_editor_and_saves_the_order()
    {
        var rig = new Rig();

        await rig.Vm.MoveColumnAsync(0, 2);

        GraphColumnKind[] expected =
        [
            GraphColumnKind.Graph, GraphColumnKind.Message, GraphColumnKind.Refs,
            GraphColumnKind.Author, GraphColumnKind.Date, GraphColumnKind.Sha,
        ];
        Assert.Equal(expected, Kinds(rig.Layout.GlobalColumns));
        Assert.Equal(expected, rig.Vm.Columns.Select(c => c.Kind));
        Assert.Equal(1, rig.Layout.GlobalWrites);
    }

    [Fact]
    public async Task Moving_keeps_the_stored_widths_and_visibility()
    {
        var rig = new Rig();
        var before = rig.Layout.GlobalColumns.ToDictionary(c => c.Kind);

        await rig.Vm.MoveColumnAsync(5, 0);

        Assert.All(rig.Layout.GlobalColumns, c => Assert.Equal(before[c.Kind], c));
    }

    [Fact]
    public void Previewing_a_move_changes_the_editor_but_saves_nothing()
    {
        var rig = new Rig();
        var row = rig.Vm.Columns[0];

        Assert.True(rig.Vm.PreviewMoveColumn(row, 3));

        Assert.Same(row, rig.Vm.Columns[3]);
        Assert.Equal(0, rig.Layout.GlobalWrites);
        Assert.False(rig.Vm.PreviewMoveColumn(row, 3));
        Assert.False(rig.Vm.PreviewMoveColumn(new Rig().Vm.Columns[0], 1));
    }

    [Fact]
    public void A_preview_target_past_either_end_is_clamped()
    {
        var rig = new Rig();
        var row = rig.Vm.Columns[2];

        rig.Vm.PreviewMoveColumn(row, 99);
        Assert.Same(row, rig.Vm.Columns[^1]);

        rig.Vm.PreviewMoveColumn(row, -4);
        Assert.Same(row, rig.Vm.Columns[0]);
    }

    [Fact]
    public async Task Committing_a_preview_saves_the_order_it_shows()
    {
        var rig = new Rig();
        rig.Vm.PreviewMoveColumn(rig.Vm.Columns[0], 2);

        await rig.Vm.CommitColumnOrderAsync();

        Assert.Equal(rig.Vm.Columns.Select(c => c.Kind), Kinds(rig.Layout.GlobalColumns));
    }

    [Fact]
    public void Cancelling_a_preview_shows_the_stored_order_again()
    {
        var rig = new Rig();
        var row = rig.Vm.Columns[0];
        row.IsDragging = true;
        rig.Vm.PreviewMoveColumn(row, 4);

        rig.Vm.CancelColumnReorder();

        Assert.Equal(Kinds(rig.Layout.GlobalColumns), rig.Vm.Columns.Select(c => c.Kind));
        Assert.DoesNotContain(rig.Vm.Columns, c => c.IsDragging);
        Assert.Equal(0, rig.Layout.GlobalWrites);
    }

    [Fact]
    public async Task Moving_an_unknown_row_is_ignored()
    {
        var rig = new Rig();

        await rig.Vm.MoveColumnAsync(-1, 2);
        await rig.Vm.MoveColumnAsync(40, 2);

        Assert.Equal(0, rig.Layout.GlobalWrites);
    }

    [Fact]
    public void Showing_a_column_saves_its_visibility()
    {
        var rig = new Rig();
        var author = rig.Vm.Columns.Single(c => c.Kind == GraphColumnKind.Author);

        author.Visible = true;

        Assert.True(rig.Layout.GlobalColumns.Single(c => c.Kind == GraphColumnKind.Author).Visible);
        Assert.Equal(1, rig.Layout.GlobalWrites);
    }

    [Fact]
    public void Hiding_the_flex_column_is_refused_and_the_row_goes_back_to_shown()
    {
        var ui = new RecordingUiDispatcher();
        var rig = new Rig(ui);
        var message = rig.Vm.Columns.Single(c => c.Kind == GraphColumnKind.Message);

        message.Visible = false;

        Assert.Equal(0, rig.Layout.GlobalWrites);
        ui.DrainPosted();
        Assert.True(message.Visible);
        Assert.Equal(0, rig.Layout.GlobalWrites);
    }

    [Fact]
    public void Hiding_the_last_visible_column_is_refused()
    {
        var rig = new Rig();
        rig.Layout.SetGlobal(
        [
            .. GraphColumnDefaults.All.Select(c => c.Kind == GraphColumnKind.Message ? c : c with { Visible = false }),
        ]);
        rig.Layout.RaiseGlobalChanged();
        rig.Layout.GlobalWrites = 0;
        var message = rig.Vm.Columns.Single(c => c.Kind == GraphColumnKind.Message);

        message.Visible = false;

        Assert.Equal(0, rig.Layout.GlobalWrites);
        Assert.True(message.Visible);
    }

    [Fact]
    public async Task A_failing_column_write_is_reported()
    {
        var rig = new Rig();
        rig.Layout.Fail = new IOException("read-only");

        await rig.Vm.MoveColumnAsync(0, 1);

        Assert.Equal("Could not update graph columns: read-only", Assert.Single(rig.Notifications.Published).Message);
    }

    [Fact]
    public async Task Reset_restores_the_stock_columns_and_unpins_the_focused_repository()
    {
        var ctx = OpenRepo();
        var rig = new Rig();
        rig.Workspace.ChangeFocus(ctx);
        await rig.Vm.MoveColumnAsync(0, 3);
        await rig.Layout.UpdateAsync(ctx.RepositoryPath, l => l with { GraphWidthPinned = true });

        await rig.Vm.ResetColumnsCommand.ExecuteAsync(null);

        Assert.Equal(Kinds(GraphColumnDefaults.All), Kinds(rig.Layout.GlobalColumns));
        Assert.Equal(Kinds(GraphColumnDefaults.All), rig.Vm.Columns.Select(c => c.Kind));
        Assert.False(rig.Layout.Get(ctx.RepositoryPath).GraphWidthPinned);
        Assert.Equal(GraphColumnDefaults.All, rig.Layout.Get(ctx.RepositoryPath).Columns);
    }

    [Fact]
    public async Task Reset_with_no_repository_only_resets_the_global_columns()
    {
        var rig = new Rig();
        await rig.Vm.MoveColumnAsync(0, 3);
        var repoWrites = rig.Layout.Writes;

        await rig.Vm.ResetColumnsCommand.ExecuteAsync(null);

        Assert.Equal(Kinds(GraphColumnDefaults.All), Kinds(rig.Layout.GlobalColumns));
        Assert.Equal(repoWrites, rig.Layout.Writes);
    }

    // ── fakes ────────────────────────────────────────────────────────────────

    private sealed class FakeWorkspace : IWorkspaceService
    {
        private EventHandler<FocusedRepositoryChangedEventArgs>? _focusedChanged;

        public int Subscribers => _focusedChanged?.GetInvocationList().Length ?? 0;

        public RepositoryContext? Focused { get; private set; }
        public IReadOnlyList<RepositoryContext> OpenRepositories => Focused is not null ? [Focused] : [];

        public event EventHandler<FocusedRepositoryChangedEventArgs>? FocusedRepositoryChanged
        {
            add => _focusedChanged += value;
            remove => _focusedChanged -= value;
        }

#pragma warning disable CS0067
        public event EventHandler<OpenRepositoriesChangedEventArgs>? OpenRepositoriesChanged;
#pragma warning restore CS0067

        public void ChangeFocus(RepositoryContext? ctx)
        {
            var old = Focused;
            Focused = ctx;
            _focusedChanged?.Invoke(this, new FocusedRepositoryChangedEventArgs(old, ctx));
        }

        public void Focus(RepositoryContext context) => ChangeFocus(context);
        public Task<RepositoryContext> OpenAsync(string repositoryPath, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<RepositoryContext> InitAsync(string path, string? initialBranch, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<RepositoryContext> CloneAsync(string url, string targetDirectory, Historia.Services.Workspace.CloneOptions options, IProgress<GitProgress>? progress, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<RepositoryContext> OpenSubmoduleAsync(RepositoryContext owner, SubmoduleInfo submodule, CancellationToken ct = default) => throw new NotImplementedException();
        public Task CloseAsync(RepositoryContext context) => Task.CompletedTask;
        public void EnsureInitialized(RepositoryContext context) { }
        public void Move(RepositoryContext context, int newIndex) { }
        public Task RestoreSessionAsync(CancellationToken ct = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeLayout : IRepoLayoutService
    {
        private readonly Dictionary<string, RepoLayout> _layouts = new(StringComparer.OrdinalIgnoreCase);
        private EventHandler<RepoLayoutChangedEventArgs>? _changed;

        public int Subscribers => _changed?.GetInvocationList().Length ?? 0;

        /// <summary>Per-repository writes.</summary>
        public int Writes { get; set; }

        /// <summary>Application-wide column writes.</summary>
        public int GlobalWrites { get; set; }

        /// <summary>When set, every write throws it.</summary>
        public Exception? Fail { get; set; }

        public IReadOnlyList<GraphColumn> GlobalColumns { get; private set; } = GraphColumnDefaults.All;

        public event EventHandler<RepoLayoutChangedEventArgs>? Changed
        {
            add => _changed += value;
            remove => _changed -= value;
        }

        public void SetGlobal(IReadOnlyList<GraphColumn> columns) => GlobalColumns = columns;

        public void RaiseGlobalChanged()
            => _changed?.Invoke(this, new RepoLayoutChangedEventArgs(IRepoLayoutService.GlobalKey, new RepoLayout()));

        public RepoLayout Get(string repoPath)
            => _layouts.TryGetValue(IRepoLayoutService.KeyFor(repoPath), out var l) ? l : new RepoLayout();

        public Task UpdateAsync(string repoPath, Func<RepoLayout, RepoLayout> mutate)
        {
            if (Fail is not null)
                return Task.FromException(Fail);
            Writes++;
            var next = mutate(Get(repoPath));
            _layouts[IRepoLayoutService.KeyFor(repoPath)] = next;
            _changed?.Invoke(this, new RepoLayoutChangedEventArgs(repoPath, next));
            return Task.CompletedTask;
        }

        public Task UpdateGlobalAsync(Func<IReadOnlyList<GraphColumn>, IReadOnlyList<GraphColumn>> mutate)
        {
            if (Fail is not null)
                return Task.FromException(Fail);
            GlobalWrites++;
            GlobalColumns = mutate(GlobalColumns);
            RaiseGlobalChanged();
            return Task.CompletedTask;
        }

        public Task FlushAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FakeProfiles : IProfileService
    {
        private readonly List<GitProfile> _profiles = [];
        private EventHandler? _changed;

        public int Subscribers => _changed?.GetInvocationList().Length ?? 0;

        public bool LocalConfigSelected { get; set; }

        public Exception? FailBinding { get; set; }

        public List<(RepoIdentityKey Key, string Path, Guid ProfileId)> Bound { get; } = [];

        public List<string> LocalConfigFor { get; } = [];

        public void Add(GitProfile profile)
        {
            _profiles.Add(profile);
            _changed?.Invoke(this, EventArgs.Empty);
        }

        public void Remove(GitProfile profile)
        {
            _profiles.Remove(profile);
            _changed?.Invoke(this, EventArgs.Empty);
        }

        public event EventHandler? ProfilesChanged
        {
            add => _changed += value;
            remove => _changed -= value;
        }

        public IReadOnlyList<GitProfile> GetAllProfiles() => _profiles;
        public GitProfile? GetProfileById(Guid id) => _profiles.FirstOrDefault(p => p.Id == id);
        public bool IsLocalConfigSelected(RepoIdentityKey repoKey) => LocalConfigSelected;

        public Task BindProfileToRepositoryAsync(RepoIdentityKey repoKey, string displayPath, Guid profileId, CancellationToken ct = default)
        {
            if (FailBinding is not null)
                return Task.FromException(FailBinding);
            Bound.Add((repoKey, displayPath, profileId));
            return Task.CompletedTask;
        }

        public Task SetLocalConfigForRepositoryAsync(RepoIdentityKey repoKey, string displayPath, CancellationToken ct = default)
        {
            if (FailBinding is not null)
                return Task.FromException(FailBinding);
            LocalConfigFor.Add(displayPath);
            return Task.CompletedTask;
        }

        public ProfileUsageState? GetUsage(Guid id) => null;
        public Task<GitProfile> CreateProfileAsync(GitProfileCreateRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<GitProfile> UpdateProfileAsync(Guid id, GitProfileUpdateRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<ProfileDeleteResult> DeleteProfileAsync(Guid id, CancellationToken ct = default) => throw new NotImplementedException();
        public GitProfile GetGlobalProfile() => throw new NotImplementedException();
        public Task SyncGlobalIdentityAsync(CancellationToken ct = default) => Task.CompletedTask;
        public GitProfile? GetDefaultProfile() => null;
        public Task SetDefaultProfileAsync(Guid? profileId, CancellationToken ct = default) => Task.CompletedTask;
        public GitProfile? GetProfileForRepository(RepoIdentityKey repoKey) => null;
        public Task UnbindRepositoryAsync(RepoIdentityKey repoKey, CancellationToken ct = default) => Task.CompletedTask;
        public IReadOnlyDictionary<RepoIdentityKey, RepoProfileBinding> GetAllRepoBindings() => new Dictionary<RepoIdentityKey, RepoProfileBinding>();
        public IReadOnlyList<RepoProfileBinding> GetStaleBindings() => [];
        public Task<int> CleanStaleBindingsAsync(CancellationToken ct = default) => Task.FromResult(0);
        public GitProfile? ResolveActiveProfile(RepoIdentityKey? repoKey = null) => null;
        public Task ApplyProfileToGitConfigAsync(GitProfile profile, string repoPath, GitConfigScope scope, CancellationToken ct = default) => Task.CompletedTask;
        public Task TouchProfileAsync(Guid profileId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> GetTokenAsync(Guid profileId, RemoteHost host, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task ExportProfilesAsync(string exportFilePath, bool includeTokens, string? password, CancellationToken ct = default) => Task.CompletedTask;
        public Task ImportProfilesAsync(string importFilePath, string? password, CancellationToken ct = default) => Task.CompletedTask;
    }
}
