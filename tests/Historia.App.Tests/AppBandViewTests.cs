namespace HistoriaApp.Tests;

/// <summary>
/// The app band's view mechanics over a real <see cref="WorkspaceService"/>: where the add button
/// sits, clicking and middle-clicking a tab, and the pointer drag that previews a reorder and
/// saves it through the workspace.
/// </summary>
[Trait("tier", "ui")]
public sealed class AppBandViewTests : IAsyncDisposable
{
    private readonly List<string> _dirs = [];
    private ServiceProvider? _sp;

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

    private string NewRepoDir()
    {
        var dir = TestScratch.NewDirectory("historia-test-appband-");
        _dirs.Add(dir);
        LibGit2Sharp.Repository.Init(dir);
        return dir;
    }

    private async Task<(AppBandView Band, Window Window, IWorkspaceService Workspace, RepositoryContext[] Repos)> CreateBandAsync(int repoCount, int width = 1200)
    {
        _sp = CreateServices();
        var workspace = _sp.GetRequiredService<IWorkspaceService>();
        var repos = new List<RepositoryContext>();
        for (var i = 0; i < repoCount; i++)
            repos.Add(await workspace.OpenAsync(NewRepoDir()));

        var band = new AppBandView();
        band.Initialize(_sp);
        var window = new Window { Width = width, Height = 120, Content = band };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (band, window, workspace, [.. repos]);
    }

    private static List<Border> TabShells(AppBandView band)
        => band.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("tabShell")).ToList();

    private static Point CenterOf(Visual target, Visual relativeTo)
        => target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), relativeTo)!.Value;

    // A point `x` pixels inside the target's left edge, on its vertical centre.
    private static Point InsideOf(Visual target, Visual relativeTo, double x)
        => target.TranslatePoint(new Point(x, target.Bounds.Height / 2), relativeTo)!.Value;

    private static Border BodyOf(Border shell)
        => shell.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("tabBody"));

    // The "+" at the end of the strip: the last child of the tab panel.
    private static Button StripAddButton(AppBandView band)
    {
        var panel = Assert.IsAssignableFrom<Panel>(band.TabStrip.ItemsPanelRoot);
        var container = Assert.IsType<Avalonia.Controls.Presenters.ContentPresenter>(panel.Children[^1]);
        Assert.IsType<AddTabViewModel>(container.DataContext);
        return Assert.IsType<Button>(container.Child);
    }

    [AvaloniaFact]
    public async Task The_add_button_ends_the_strip_after_the_last_tab_and_stays_there_after_a_rebuild()
    {
        var (band, window, workspace, _) = await CreateBandAsync(2);

        var add = StripAddButton(band);
        Assert.True(add.IsEffectivelyVisible);
        Assert.Equal(24, add.Width);
        Assert.Equal(new Thickness(2, 0), add.Margin);
        Assert.False(band.AddButton.IsVisible);

        await workspace.OpenAsync(NewRepoDir());
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(3, TabShells(band).Count);
        var rebuilt = StripAddButton(band);
        Assert.True(rebuilt.IsEffectivelyVisible);
        Assert.False(band.AddButton.IsVisible);

        window.Close();
        band.Dispose();
    }

    [AvaloniaFact]
    public async Task The_add_button_moves_beside_the_arrows_when_the_tabs_overflow()
    {
        var (band, window, _, _) = await CreateBandAsync(8, width: 480);

        Assert.True(band.AddButton.IsEffectivelyVisible);
        Assert.False(StripAddButton(band).IsEffectivelyVisible);
        Assert.True(band.RightArrow.IsHitTestVisible);
        Assert.True(band.LeftArrow.IsHitTestVisible);

        window.Close();
        band.Dispose();
    }

    [AvaloniaFact]
    public async Task Clicking_a_tab_focuses_its_repository_and_shows_the_repository_view()
    {
        var (band, window, workspace, repos) = await CreateBandAsync(2);
        var routes = new List<string>();
        _sp!.GetRequiredService<INavigationBus>().NavigationRequested += routes.Add;
        Assert.Same(repos[1], workspace.Focused);

        var first = TabShells(band).Single(s => s.DataContext is RepoTabViewModel tab && ReferenceEquals(tab.Context, repos[0]));
        var point = CenterOf(BodyOf(first), window);
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.Same(repos[0], workspace.Focused);
        Assert.Contains("repo", routes);
        Assert.Equal([true, false], band.ViewModel!.Tabs.OfType<RepoTabViewModel>().Select(t => t.IsActive));

        window.Close();
        band.Dispose();
    }

    [AvaloniaFact]
    public async Task Dragging_a_tab_past_its_neighbour_previews_the_swap_and_saves_it_on_release()
    {
        var (band, window, workspace, repos) = await CreateBandAsync(3);
        var shells = TabShells(band);
        var first = shells.Single(s => ReferenceEquals(((RepoTabViewModel)s.DataContext!).Context, repos[0]));
        var second = shells.Single(s => ReferenceEquals(((RepoTabViewModel)s.DataContext!).Context, repos[1]));
        var start = InsideOf(BodyOf(first), window, 20);
        var past = InsideOf(second, window, 10);

        window.MouseDown(start, MouseButton.Left);
        window.MouseMove(new Point(start.X + 8, start.Y), RawInputModifiers.LeftMouseButton);
        Dispatcher.UIThread.RunJobs();
        Assert.True(band.ViewModel!.IsDragging);
        Assert.True(((RepoTabViewModel)band.ViewModel.Tabs[0]).IsDragging);

        window.MouseMove(past, RawInputModifiers.LeftMouseButton);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal([repos[1], repos[0], repos[2]], band.ViewModel.Tabs.OfType<RepoTabViewModel>().Select(t => t.Context));
        Assert.Equal([repos[0], repos[1], repos[2]], workspace.OpenRepositories);

        window.MouseUp(past, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.False(band.ViewModel.IsDragging);
        Assert.Equal([repos[1], repos[0], repos[2]], workspace.OpenRepositories);
        Assert.Equal([repos[1], repos[0], repos[2]], band.ViewModel.Tabs.OfType<RepoTabViewModel>().Select(t => t.Context));
        Assert.All(band.ViewModel.Tabs, t => Assert.False(t.IsDragging));
        Assert.Same(repos[2], workspace.Focused);

        window.Close();
        band.Dispose();
    }

    [AvaloniaFact]
    public async Task A_drag_that_ends_in_its_own_slot_keeps_the_order()
    {
        var (band, window, workspace, repos) = await CreateBandAsync(2);
        var first = TabShells(band).Single(s => ReferenceEquals(((RepoTabViewModel)s.DataContext!).Context, repos[0]));
        var start = InsideOf(BodyOf(first), window, 20);

        window.MouseDown(start, MouseButton.Left);
        window.MouseMove(new Point(start.X + 10, start.Y), RawInputModifiers.LeftMouseButton);
        window.MouseMove(start, RawInputModifiers.LeftMouseButton);
        window.MouseUp(start, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.False(band.ViewModel!.IsDragging);
        Assert.Equal([repos[0], repos[1]], workspace.OpenRepositories);
        Assert.Equal([repos[0], repos[1]], band.ViewModel.Tabs.OfType<RepoTabViewModel>().Select(t => t.Context));

        window.Close();
        band.Dispose();
    }

    [AvaloniaFact]
    public async Task Middle_clicking_a_tab_closes_its_repository()
    {
        var (band, window, workspace, repos) = await CreateBandAsync(2);
        var first = TabShells(band).Single(s => ReferenceEquals(((RepoTabViewModel)s.DataContext!).Context, repos[0]));
        var point = CenterOf(BodyOf(first), window);

        window.MouseDown(point, MouseButton.Middle);
        window.MouseUp(point, MouseButton.Middle);
        for (var i = 0; i < 100 && workspace.OpenRepositories.Count > 1; i++)
        {
            await Task.Delay(20);
            Dispatcher.UIThread.RunJobs();
        }

        Assert.Equal([repos[1]], workspace.OpenRepositories);
        Assert.Single(band.ViewModel!.Tabs);

        window.Close();
        band.Dispose();
    }

    [AvaloniaFact]
    public async Task Clicking_the_wordmark_opens_Home()
    {
        var (band, window, _, _) = await CreateBandAsync(1);
        var routes = new List<string>();
        _sp!.GetRequiredService<INavigationBus>().NavigationRequested += routes.Add;
        var word = band.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == "Historia");
        var point = CenterOf(word, window);

        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);

        Assert.Equal([""], routes);

        window.Close();
        band.Dispose();
    }

    [AvaloniaFact]
    public async Task Opening_the_menu_from_a_repository_view_switches_the_detail_pane_to_Home()
    {
        var (band, window, _, _) = await CreateBandAsync(1);
        var nav = _sp!.GetRequiredService<INavigationBus>();
        var routes = new List<string>();
        nav.NavigationRequested += routes.Add;
        nav.ReportRoute("repo");

        band.Hamburger.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal([""], routes);

        window.Close();
        band.Dispose();
    }

    private static void AssertAddMenuRoutes(Button button, List<string> routes)
    {
        var flyout = Assert.IsType<MenuFlyout>(button.Flyout);
        flyout.ShowAt(button);
        Dispatcher.UIThread.RunJobs();
        foreach (var item in flyout.Items.OfType<MenuItem>())
            item.Command?.Execute(item.CommandParameter);
        flyout.Hide();

        Assert.Equal(["?intent=open", "?intent=clone", "?intent=init"], routes);
    }

    [AvaloniaFact]
    public async Task The_add_menu_at_the_end_of_the_strip_requests_the_open_clone_and_initialize_intents()
    {
        var (band, window, _, _) = await CreateBandAsync(1);
        var routes = new List<string>();
        _sp!.GetRequiredService<INavigationBus>().NavigationRequested += routes.Add;

        AssertAddMenuRoutes(StripAddButton(band), routes);

        window.Close();
        band.Dispose();
    }

    [AvaloniaFact]
    public async Task The_add_menu_beside_the_arrows_requests_the_same_intents()
    {
        var (band, window, _, _) = await CreateBandAsync(1);
        var routes = new List<string>();
        _sp!.GetRequiredService<INavigationBus>().NavigationRequested += routes.Add;

        AssertAddMenuRoutes(band.AddButton, routes);

        window.Close();
        band.Dispose();
    }

    [AvaloniaFact]
    public async Task The_tab_context_menu_offers_location_copy_and_close()
    {
        var (band, window, _, repos) = await CreateBandAsync(2);
        var shell = TabShells(band).Single(s => ReferenceEquals(((RepoTabViewModel)s.DataContext!).Context, repos[0]));
        var body = BodyOf(shell);
        var menu = Assert.IsType<ContextMenu>(body.ContextMenu);

        menu.Open(body);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(
            ["Open repository location", "Copy path", "Close"],
            menu.Items.OfType<MenuItem>().Select(i => i.Header as string));
        Assert.All(menu.Items.OfType<MenuItem>(), item => Assert.NotNull(item.Command));

        menu.Close();
        window.Close();
        band.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (_sp is not null)
        {
            if (_sp.GetRequiredService<IWorkspaceService>() is WorkspaceService workspace)
                await workspace.DisposeAsync();
            _sp.Dispose();
        }
        foreach (var dir in _dirs)
            TestScratch.Delete(dir);
    }
}
