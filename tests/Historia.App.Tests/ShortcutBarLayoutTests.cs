namespace HistoriaApp.Tests;

public class ShortcutBarLayoutTests
{
    private static ServiceProvider CreateServices()
    {
        var sc = new ServiceCollection();
        var settings = SettingsService.Load();
        sc.AddSingleton<ISettingsService>(settings);
        sc.AddSingleton<SettingsService>(settings);
        sc.AddSingleton<IUiDispatcher, Historia.Platform.UiDispatcher>();
        sc.AddSingleton<IDialogService, Historia.Platform.AvaloniaDialogService>();
        sc.AddSingleton<IScrollDirectionProbe, Historia.Platform.ScrollDirectionProbe>();
        sc.AddSingleton<IRepoActionBus, RepoActionBus>();
        sc.AddSingleton<IRepoNetworkActionRunner, RepoNetworkActionRunner>();
        sc.AddSingleton<IAutoFetchHost, AutoFetchHost>();
        sc.AddSingleton<IAutoFetchService, AutoFetchService>();
        sc.AddSingleton<INavigationBus, NavigationBus>();
        sc.AddSingleton<IKeyboardCommandRouter, KeyboardCommandRouter>();
        sc.AddSingleton<INotificationService, NotificationService>();
        sc.AddSingleton<IRepoLayoutService, RepoLayoutService>();
        sc.AddSingleton<ISystemThemeProbe, FakeThemeProbe>();
        sc.AddSingleton<IThemeService, ThemeService>();
        sc.AddSingleton<ICustomThemeService, CustomThemeService>();
        sc.AddSingleton<IWorkspaceService, WorkspaceService>();
        sc.AddSingleton<IProfileService, ProfileService>();
        sc.AddSingleton<IGitCliService, GitCliService>();
        var locator = new GitExecutableLocator();
        locator.ResolveAsync(null).GetAwaiter().GetResult();
        sc.AddSingleton<IGitExecutableLocator>(locator);
        sc.AddSingleton<LibGit2Service>();
        sc.AddSingleton<IBackgroundWorkGate, BackgroundWorkGate>();
        sc.AddSingleton<IGraphLayoutService, GraphLayoutService>();
        sc.AddSingleton<IRepoStateCache, RepoStateCache>();
        sc.AddSingleton<IStashService, StashService>();
        sc.AddSingleton<IConflictService, ConflictService>();
        sc.AddSingleton<IPatchService, PatchService>();
        sc.AddSingleton<ICherryPickService, CherryPickService>();
        sc.AddSingleton<IRebaseService, RebaseService>();
        sc.AddSingleton<ISubmoduleService, SubmoduleService>();
        sc.AddSingleton<IWorktreeService, WorktreeService>();
        sc.AddSingleton<ISshKeyService, SshKeyService>();
        sc.AddSingleton<ICloneService, CloneService>();
        sc.AddSingleton<ITerminalBackend, PortaPtyBackend>();
        sc.AddSingleton<ITerminalSessionService, TerminalSessionService>();
        sc.AddSingleton<IRecentRepositoryService, RecentRepositoryService>();
        sc.AddSingleton<IRepoIdentityService, RepoIdentityService>();
        sc.AddSingleton<ICredentialVaultService, FakeVault>();
        sc.AddSingleton<IExternalLauncher, ExternalLauncher>();
        return sc.BuildServiceProvider();
    }

    [AvaloniaFact]
    public async Task Left_bar_has_branch_settings_terminal_in_order()
    {
        await using var sp = CreateServices();
        var bar = new ShortcutsBarControl(sp, ShortcutsBarSide.Left);
        var panel = (StackPanel)((Border)bar.Content!).Child!;
        var labels = panel.Children
            .OfType<Button>()
            .Select(b => ((StackPanel)b.Content!).Children.OfType<TextBlock>().First().Text)
            .ToList();

        Assert.Equal(["Branch", "Settings", "Terminal"], labels);
    }

    [AvaloniaFact]
    public async Task Right_bar_has_fetch_pull_push_stash_in_order()
    {
        await using var sp = CreateServices();
        var bar = new ShortcutsBarControl(sp, ShortcutsBarSide.Right);
        var panel = (StackPanel)((Border)bar.Content!).Child!;
        var labels = panel.Children
            .OfType<Button>()
            .Select(b => ((StackPanel)b.Content!).Children.OfType<TextBlock>().First().Text)
            .ToList();

        Assert.Equal(["Fetch", "Pull", "Push", "Stash"], labels);
    }

    [AvaloniaFact]
    public async Task Bars_match_the_graph_header_row()
    {
        Assert.Equal(CommitGraphControl.HeaderHeight, ShortcutsBarControl.BarHeight);

        await using var sp = CreateServices();
        var window = new MainWindow(sp);
        window.Width = 1280;
        window.Height = 760;
        window.Measure(new Size(1280, 760));
        window.Arrange(new Rect(0, 0, 1280, 760));

        var leftHost = window.FindControl<ContentControl>("LeftShortcutsHost")!;
        var rightHost = window.FindControl<ContentControl>("RightShortcutsHost")!;

        Assert.Equal(0, leftHost.Bounds.Top);
        Assert.Equal(0, rightHost.Bounds.Top);
        Assert.Equal(CommitGraphControl.HeaderHeight, leftHost.Bounds.Height);
        Assert.Equal(CommitGraphControl.HeaderHeight, rightHost.Bounds.Height);

        window.Close();
    }

    [AvaloniaFact]
    public async Task Bars_have_no_profile_or_ssh_controls()
    {
        await using var sp = CreateServices();
        var left = new ShortcutsBarControl(sp, ShortcutsBarSide.Left);
        var right = new ShortcutsBarControl(sp, ShortcutsBarSide.Right);

        static void AssertClean(Control control)
        {
            var toggleButtons = control.GetVisualDescendants().OfType<ToggleButton>().ToList();
            Assert.Empty(toggleButtons);

            var texts = control.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
            Assert.DoesNotContain(texts, t => t != null && (t.Contains("SSH", StringComparison.OrdinalIgnoreCase) || t.Contains("Local config", StringComparison.OrdinalIgnoreCase)));
        }

        AssertClean(left);
        AssertClean(right);
    }

    [Fact]
    public void Effective_min_never_hides_bar_content()
    {
        Assert.True(BodyColumnLayout.EffectiveMin(250, 150) >= 150);
        Assert.True(BodyColumnLayout.EffectiveMin(250, 250) >= 250);
        Assert.True(BodyColumnLayout.EffectiveMin(250, 310.2) >= 310.2);
    }

    [Fact]
    public void Resolve_keeps_side_panes_at_min_when_window_is_narrow()
    {
        // total below the three intents (264 + 430 = 694) -> centre shrinks first, both sides stay >= their mins
        var (left1, centre1, right1) = BodyColumnLayout.Resolve(600, 264, 430, 250, 250);
        Assert.Equal(0, centre1);
        Assert.True(left1 >= 250);
        Assert.True(right1 >= 250);
        Assert.Equal(600, left1 + right1);

        // total below leftMin + rightMin (500) -> left kept, right takes the rest
        var (left2, centre2, right2) = BodyColumnLayout.Resolve(400, 264, 430, 250, 250);
        Assert.Equal(0, centre2);
        Assert.Equal(250, left2);
        Assert.Equal(150, right2);
    }

    [Fact]
    public void Resolve_matches_previous_behaviour_when_space_allows()
    {
        // At 1280 px window (content width 1280 - 2 * 4 = 1272), default intents 264 and 430
        var (left, centre, right) = BodyColumnLayout.Resolve(1272, 264, 430, 250, 250);
        Assert.Equal(264, left);
        Assert.Equal(430, right);
        Assert.Equal(1272 - 264 - 430, centre);
    }

    [AvaloniaFact]
    public async Task Bars_follow_their_columns()
    {
        await using var sp = CreateServices();
        var window = new MainWindow(sp);
        window.Width = 1600;
        window.Height = 800;
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var leftHost = window.FindControl<ContentControl>("LeftShortcutsHost")!;
        var rightHost = window.FindControl<ContentControl>("RightShortcutsHost")!;
        var bodyGrid = window.FindControl<Grid>("BodyGrid")!;

        var leftCol = bodyGrid.ColumnDefinitions[0];
        var rightCol = bodyGrid.ColumnDefinitions[4];

        Assert.Equal(leftCol.Width.Value, leftHost.Bounds.Width, 1);
        Assert.Equal(rightCol.Width.Value, rightHost.Bounds.Width, 1);

        // Move the right divider through ApplyBodyLayout intents
        window.SetIntents(window.LeftMin, 500);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(leftCol.Width.Value, leftHost.Bounds.Width, 1);
        Assert.Equal(rightCol.Width.Value, rightHost.Bounds.Width, 1);

        window.Close();
    }

    [AvaloniaFact]
    public async Task Route_change_does_not_replace_bars()
    {
        await using var sp = CreateServices();
        var window = new MainWindow(sp);
        window.Width = 1280;
        window.Height = 760;
        window.Measure(new Size(1280, 760));
        window.Arrange(new Rect(0, 0, 1280, 760));

        var leftHost = window.FindControl<ContentControl>("LeftShortcutsHost")!;
        var rightHost = window.FindControl<ContentControl>("RightShortcutsHost")!;

        var leftBar = leftHost.Content;
        var rightBar = rightHost.Content;
        Assert.NotNull(leftBar);
        Assert.NotNull(rightBar);

        var nav = sp.GetRequiredService<INavigationBus>();
        nav.RequestNavigate("settings");
        Dispatcher.UIThread.RunJobs();

        Assert.Same(leftBar, leftHost.Content);
        Assert.Same(rightBar, rightHost.Content);

        window.Close();
    }

    private sealed class FakeThemeProbe : ISystemThemeProbe
    {
        public bool IsLightTheme => false;
        public bool IsHighContrast => false;
#pragma warning disable CS0067
        public event EventHandler? Changed;
#pragma warning restore CS0067
    }

    private sealed class FakeVault : ICredentialVaultService
    {
        public Task StoreTokenAsync(string key, string token, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> RetrieveTokenAsync(string key, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task DeleteTokenAsync(string key, CancellationToken ct = default) => Task.CompletedTask;
    }
}
