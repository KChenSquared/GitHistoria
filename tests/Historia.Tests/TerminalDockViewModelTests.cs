namespace Historia.Tests;

/// <summary>
/// The terminal dock's view model: the tab strip over the live sessions, open / close and the
/// automatic hide, the height clamp and its debounced save, restart, close-all and the
/// follow-the-focused-repository behaviour, all driven through a real session service over a fake
/// pty backend.
/// </summary>
[Trait("tier", "git")]
public sealed class TerminalDockViewModelTests : IAsyncDisposable
{
    private readonly MiniHost _host = new();
    private readonly List<TestRepositoryBuilder> _repos = [];
    private readonly List<RepositoryContext> _contexts = [];

    public async ValueTask DisposeAsync()
    {
        foreach (var context in _contexts)
            await context.DisposeAsync();
        foreach (var repo in _repos)
            repo.Dispose();
        await _host.DisposeAsync();
    }

    private static TerminalHostEnvironment Host() => new()
    {
        Platform = OSPlatformKind.Linux,
        FileExists = p => p == "/bin/bash",
        DirectoryExists = _ => true,
        GetEnvironmentVariable = _ => null,
        FindOnPath = _ => null,
        GetPasswdShell = () => "/bin/bash",
        GetInheritedEnvironment = () => new Dictionary<string, string> { ["PATH"] = "/usr/bin" },
        HomeDirectory = "/home/me",
        CurrentDirectory = "/tmp",
        ExecutablePath = "/opt/historia/Historia",
        Version = "5.1.0",
    };

    private sealed class Rig : IDisposable
    {
        public Rig(AppSettings? settings = null, IUiDispatcher? ui = null, double hostHeight = 0)
        {
            Backend = new FakeTerminalBackend();
            Settings = new FakeSettingsService(settings);
            Sessions = new TerminalSessionService(Backend, Settings, new ImmediateUiDispatcher(), Host());
            Workspace = new FakeWorkspace();
            Keyboard = new KeyboardCommandRouter();
            Ui = ui ?? new ImmediateUiDispatcher();
            ViewModel = new TerminalDockViewModel(Ui, Sessions, Workspace, Settings, Keyboard, "/home/me")
            {
                HostHeightProvider = () => HostHeight,
            };
            HostHeight = hostHeight;
        }

        public FakeTerminalBackend Backend { get; }
        public FakeSettingsService Settings { get; }
        public TerminalSessionService Sessions { get; }
        public FakeWorkspace Workspace { get; }
        public KeyboardCommandRouter Keyboard { get; }
        public IUiDispatcher Ui { get; }
        public TerminalDockViewModel ViewModel { get; }
        public double HostHeight { get; set; }

        public void Dispose() => ViewModel.Dispose();
    }

    private static TerminalOrigin Repo(string name)
        => TerminalOrigin.ForRepository(
            RepoIdentityKey.FromExplicitId(Guid.NewGuid()), tabId: name, $"/src/{name}", name);

    private RepositoryContext OpenRepo(string seed)
    {
        var repo = TestRepositoryBuilder.FromTemplate(seed, b => b.WriteFile("f.txt", "base\n").Stage().Commit("c1"));
        _repos.Add(repo);
        var context = _host.Open(repo.Path);
        _contexts.Add(context);
        return context;
    }

    [Fact]
    public void ShouldAutoHide_is_true_only_for_an_open_dock_with_no_sessions()
    {
        Assert.True(TerminalDockViewModel.ShouldAutoHide(open: true, sessionCount: 0));
        Assert.False(TerminalDockViewModel.ShouldAutoHide(open: false, sessionCount: 0));
        Assert.False(TerminalDockViewModel.ShouldAutoHide(open: true, sessionCount: 1));
        Assert.False(TerminalDockViewModel.ShouldAutoHide(open: false, sessionCount: 2));
    }

    [Fact]
    public void The_dock_starts_closed_with_no_height_and_no_tabs()
    {
        using var rig = new Rig();

        Assert.False(rig.ViewModel.IsOpen);
        Assert.Equal(0, rig.ViewModel.Height);
        Assert.Empty(rig.ViewModel.Tabs);
        Assert.Null(rig.ViewModel.ActiveSession);
    }

    [Fact]
    public async Task Opening_a_session_adds_an_active_tab_and_activation_opens_the_dock()
    {
        using var rig = new Rig();

        var session = await rig.Sessions.OpenOrFocusAsync(Repo("alpha"));
        rig.Sessions.RequestActivation();

        var tab = Assert.Single(rig.ViewModel.Tabs);
        Assert.Same(session, tab.Session);
        Assert.Equal("alpha", tab.Label);
        Assert.True(tab.IsActive);
        Assert.Same(session, rig.ViewModel.ActiveSession);
        Assert.True(rig.ViewModel.IsOpen);
    }

    [Fact]
    public async Task Tabs_are_replaced_wholesale_and_only_the_focused_one_is_active()
    {
        using var rig = new Rig();
        var a = await rig.Sessions.OpenOrFocusAsync(Repo("alpha"));
        var b = await rig.Sessions.OpenOrFocusAsync(Repo("beta"));
        var before = rig.ViewModel.Tabs;

        rig.Sessions.Focus(a);

        Assert.NotSame(before, rig.ViewModel.Tabs);
        Assert.Equal(["alpha", "beta"], rig.ViewModel.Tabs.Select(t => t.Label));
        Assert.Equal([true, false], rig.ViewModel.Tabs.Select(t => t.IsActive));
        Assert.Same(a, rig.ViewModel.ActiveSession);
        Assert.NotSame(b, rig.ViewModel.ActiveSession);
    }

    [Fact]
    public async Task A_tab_whose_shell_exited_is_labelled_and_the_terminal_keeps_showing_it()
    {
        using var rig = new Rig();
        var session = await rig.Sessions.OpenOrFocusAsync(Repo("alpha"));

        rig.Backend.Connections[0].EmitExit(3);

        Assert.Equal(TerminalSessionState.Exited, session.State);
        Assert.Equal("alpha  [exited]", Assert.Single(rig.ViewModel.Tabs).Label);
    }

    [Fact]
    public async Task Activating_a_tab_focuses_its_session_and_asks_for_terminal_focus()
    {
        using var rig = new Rig();
        var a = await rig.Sessions.OpenOrFocusAsync(Repo("alpha"));
        await rig.Sessions.OpenOrFocusAsync(Repo("beta"));
        var focusRequests = 0;
        rig.ViewModel.FocusTerminalRequested += (_, _) => focusRequests++;

        rig.ViewModel.Tabs[0].ActivateCommand.Execute(null);

        Assert.Same(a, rig.Sessions.Focused);
        Assert.Same(a, rig.ViewModel.ActiveSession);
        Assert.True(rig.ViewModel.Tabs[0].IsActive);
        Assert.True(focusRequests >= 1);
    }

    [Fact]
    public async Task Closing_a_tab_closes_only_that_session()
    {
        using var rig = new Rig();
        await rig.Sessions.OpenOrFocusAsync(Repo("alpha"));
        var b = await rig.Sessions.OpenOrFocusAsync(Repo("beta"));

        await ((IAsyncRelayCommand)rig.ViewModel.Tabs[0].CloseCommand).ExecuteAsync(null);

        Assert.Same(b, Assert.Single(rig.Sessions.Sessions));
        Assert.Equal(["beta"], rig.ViewModel.Tabs.Select(t => t.Label));
    }

    [Fact]
    public async Task Closing_the_last_session_hides_an_open_dock()
    {
        using var rig = new Rig();
        var session = await rig.Sessions.OpenOrFocusAsync(Repo("alpha"));
        rig.Sessions.RequestActivation();
        Assert.True(rig.ViewModel.IsOpen);

        await rig.Sessions.CloseAsync(session.Id);

        Assert.False(rig.ViewModel.IsOpen);
        Assert.Equal(0, rig.ViewModel.Height);
        Assert.Empty(rig.ViewModel.Tabs);
    }

    [Fact]
    public async Task Close_all_closes_every_session_but_the_close_button_only_hides_the_dock()
    {
        using var rig = new Rig();
        await rig.Sessions.OpenOrFocusAsync(Repo("alpha"));
        await rig.Sessions.OpenOrFocusAsync(Repo("beta"));
        rig.Sessions.RequestActivation();

        rig.ViewModel.CloseDockCommand.Execute(null);
        Assert.False(rig.ViewModel.IsOpen);
        Assert.Equal(2, rig.Sessions.Sessions.Count);

        await rig.ViewModel.CloseAllCommand.ExecuteAsync(null);
        Assert.Empty(rig.Sessions.Sessions);
        Assert.Empty(rig.ViewModel.Tabs);
    }

    [Fact]
    public async Task Restart_replaces_an_exited_session_with_a_new_one_for_the_same_origin()
    {
        using var rig = new Rig();
        var dead = await rig.Sessions.OpenOrFocusAsync(Repo("alpha"));
        rig.Backend.Connections[0].EmitExit(0);

        await rig.ViewModel.RestartCommand.ExecuteAsync(null);

        var fresh = Assert.Single(rig.Sessions.Sessions);
        Assert.NotSame(dead, fresh);
        Assert.Equal(TerminalSessionState.Running, fresh.State);
        Assert.Equal(2, rig.Backend.Starts.Count);
        Assert.Same(fresh, rig.ViewModel.ActiveSession);
    }

    [Fact]
    public async Task Restart_leaves_a_running_session_alone()
    {
        using var rig = new Rig();
        var live = await rig.Sessions.OpenOrFocusAsync(Repo("alpha"));

        await rig.ViewModel.RestartCommand.ExecuteAsync(null);

        Assert.Same(live, Assert.Single(rig.Sessions.Sessions));
        Assert.Single(rig.Backend.Starts);
    }

    [Fact]
    public async Task The_plus_button_opens_a_new_home_terminal_every_time()
    {
        using var rig = new Rig();

        await rig.ViewModel.NewHomeTerminalCommand.ExecuteAsync(null);
        await rig.ViewModel.NewHomeTerminalCommand.ExecuteAsync(null);

        Assert.Equal(2, rig.Sessions.Sessions.Count);
        Assert.All(rig.Sessions.Sessions, s => Assert.Equal(TerminalOriginKind.Home, s.Origin.Kind));
        Assert.All(rig.Backend.Starts, s => Assert.Equal("/home/me", s.WorkingDirectory));
    }

    [Fact]
    public async Task The_shortcut_starts_a_home_terminal_when_nothing_is_open_and_no_repo_is_focused()
    {
        using var rig = new Rig();

        rig.ViewModel.ToggleFromKeyboard();
        await WaitForAsync(() => rig.Sessions.Sessions.Count == 1);

        var session = Assert.Single(rig.Sessions.Sessions);
        Assert.Equal(TerminalOriginKind.Home, session.Origin.Kind);
    }

    [Fact]
    public async Task The_shortcut_starts_the_focused_repositorys_terminal_when_no_session_exists()
    {
        using var rig = new Rig();
        var context = OpenRepo("dock-shortcut-seed");
        rig.Workspace.SetFocused(context);

        rig.ViewModel.ToggleFromKeyboard();
        await WaitForAsync(() => rig.Sessions.Sessions.Count == 1);

        var session = Assert.Single(rig.Sessions.Sessions);
        Assert.Equal(TerminalOriginKind.Repository, session.Origin.Kind);
        Assert.Equal(context.Id, session.Origin.TabId);
    }

    [Fact]
    public async Task The_shortcut_toggles_an_existing_dock_and_asks_for_terminal_focus()
    {
        using var rig = new Rig();
        await rig.Sessions.OpenOrFocusAsync(Repo("alpha"));
        rig.ViewModel.CloseDockCommand.Execute(null);
        var focusRequests = 0;
        rig.ViewModel.FocusTerminalRequested += (_, _) => focusRequests++;

        rig.ViewModel.ToggleFromKeyboard();
        Assert.True(rig.ViewModel.IsOpen);
        Assert.True(focusRequests >= 1);

        rig.ViewModel.ToggleFromKeyboard();
        Assert.False(rig.ViewModel.IsOpen);
    }

    [Fact]
    public async Task The_chord_toggles_through_the_ui_dispatcher_and_is_released_on_dispose()
    {
        var ui = new RecordingUiDispatcher();
        using var rig = new Rig(ui: ui);
        await rig.Sessions.OpenOrFocusAsync(Repo("alpha"));
        ui.DrainPosted();
        Assert.True(rig.ViewModel.IsOpen);

        Assert.True(rig.Keyboard.IsRegistered("Ctrl+OemTilde"));
        await rig.Keyboard.DispatchAsync("Ctrl+OemTilde");
        Assert.True(rig.ViewModel.IsOpen);
        ui.DrainPosted();
        Assert.False(rig.ViewModel.IsOpen);

        rig.ViewModel.Dispose();
        Assert.False(rig.Keyboard.IsRegistered("Ctrl+OemTilde"));
    }

    [Fact]
    public async Task Session_changes_are_applied_through_the_ui_dispatcher()
    {
        var ui = new RecordingUiDispatcher();
        using var rig = new Rig(ui: ui);

        await rig.Sessions.OpenOrFocusAsync(Repo("alpha"));
        Assert.Empty(rig.ViewModel.Tabs);

        ui.DrainPosted();
        Assert.Single(rig.ViewModel.Tabs);
    }

    [Fact]
    public async Task Events_after_dispose_are_ignored_and_unsubscribed()
    {
        var ui = new RecordingUiDispatcher();
        using var rig = new Rig(ui: ui);
        await rig.Sessions.OpenOrFocusAsync(Repo("alpha"));
        rig.ViewModel.Dispose();

        ui.DrainPosted();
        rig.Sessions.RequestActivation();
        ui.DrainPosted();

        Assert.Empty(rig.ViewModel.Tabs);
        Assert.False(rig.ViewModel.IsOpen);
    }

    // ── height ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_first_open_takes_35_percent_of_the_centre_column_whatever_was_saved()
    {
        using var rig = new Rig(new AppSettings { TerminalDockHeight = 500 }, hostHeight: 1000);

        rig.ViewModel.SetOpen(true);

        Assert.Equal(350, rig.ViewModel.Height);
        rig.ViewModel.SetOpen(false);
        Assert.Equal(0, rig.ViewModel.Height);
        rig.HostHeight = 2000;
        rig.ViewModel.SetOpen(true);
        Assert.Equal(350, rig.ViewModel.Height);
    }

    [Fact]
    public void Without_a_known_host_height_the_saved_height_is_used_but_never_below_the_minimum()
    {
        using var saved = new Rig(new AppSettings { TerminalDockHeight = 300 });
        saved.ViewModel.SetOpen(true);
        Assert.Equal(300, saved.ViewModel.Height);

        using var tiny = new Rig(new AppSettings { TerminalDockHeight = 10 });
        tiny.ViewModel.SetOpen(true);
        Assert.Equal(TerminalDockViewModel.MinDockHeight, tiny.ViewModel.Height);
    }

    [Fact]
    public void Dragging_clamps_between_the_minimum_and_the_host_height_less_the_reserve()
    {
        using var rig = new Rig(hostHeight: 600);
        rig.ViewModel.SetOpen(true);
        Assert.Equal(210, rig.ViewModel.Height);

        rig.ViewModel.DragBy(-50);
        Assert.Equal(260, rig.ViewModel.Height);

        rig.ViewModel.DragBy(-10_000);
        Assert.Equal(600 - TerminalDockViewModel.MinCentreReserve, rig.ViewModel.Height);

        rig.ViewModel.DragBy(10_000);
        Assert.Equal(TerminalDockViewModel.MinDockHeight, rig.ViewModel.Height);
    }

    [Fact]
    public void Dragging_a_closed_dock_changes_nothing()
    {
        using var rig = new Rig(hostHeight: 600);

        rig.ViewModel.DragBy(-100);

        Assert.Equal(0, rig.ViewModel.Height);
    }

    [Fact]
    public void The_height_is_saved_once_after_the_drag_settles()
    {
        var ui = new RecordingUiDispatcher();
        using var rig = new Rig(new AppSettings { TerminalDockHeight = 260 }, ui, hostHeight: 1000);
        rig.ViewModel.SetOpen(true);
        rig.ViewModel.DragBy(-40);
        var timer = ui.LastTimer!;

        rig.ViewModel.CompleteDrag();
        rig.ViewModel.CompleteDrag();

        Assert.True(timer.IsRunning);
        Assert.Equal(260, rig.Settings.Current.TerminalDockHeight);

        timer.Fire();

        Assert.False(timer.IsRunning);
        Assert.Equal(390, rig.Settings.Current.TerminalDockHeight);
    }

    // ── follow the focused repository ───────────────────────────────────────────────────────

    [Fact]
    public async Task FindRepoSession_matches_by_tab_id()
    {
        using var rig = new Rig();
        var context = OpenRepo("dock-find-seed");
        var session = await rig.Sessions.OpenOrFocusAsync(RepoTerminalOrigin.For(context));
        await rig.Sessions.OpenOrFocusAsync(Repo("other"));

        Assert.Same(session, rig.ViewModel.FindRepoSession(context));
    }

    [Fact]
    public async Task The_dock_hides_for_a_repo_without_a_terminal_and_returns_when_focus_comes_back()
    {
        using var rig = new Rig();
        var withTerminal = OpenRepo("dock-follow-a-seed");
        var without = OpenRepo("dock-follow-b-seed");
        var session = await rig.Sessions.OpenOrFocusAsync(RepoTerminalOrigin.For(withTerminal));
        rig.Sessions.RequestActivation();
        Assert.True(rig.ViewModel.IsOpen);

        rig.Workspace.SetFocused(without);
        Assert.False(rig.ViewModel.IsOpen);
        Assert.Single(rig.Sessions.Sessions);

        rig.Workspace.SetFocused(withTerminal);
        Assert.True(rig.ViewModel.IsOpen);
        Assert.Same(session, rig.ViewModel.ActiveSession);
    }

    [Fact]
    public async Task A_dock_the_user_closed_stays_closed_when_focus_returns_to_a_repo_with_a_terminal()
    {
        using var rig = new Rig();
        var withTerminal = OpenRepo("dock-user-a-seed");
        await rig.Sessions.OpenOrFocusAsync(RepoTerminalOrigin.For(withTerminal));
        rig.Sessions.RequestActivation();
        rig.ViewModel.CloseDockCommand.Execute(null);

        rig.Workspace.SetFocused(withTerminal);

        Assert.False(rig.ViewModel.IsOpen);
    }

    [Fact]
    public async Task A_home_terminal_stays_visible_when_a_repo_without_a_terminal_gets_focus()
    {
        using var rig = new Rig();
        var without = OpenRepo("dock-home-seed");
        await rig.Sessions.OpenNewAsync(TerminalOrigin.Home("/home/me"));
        rig.Sessions.RequestActivation();

        rig.Workspace.SetFocused(without);

        Assert.True(rig.ViewModel.IsOpen);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        Assert.True(condition());
    }
}
