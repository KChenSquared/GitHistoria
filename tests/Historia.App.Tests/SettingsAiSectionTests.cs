namespace HistoriaApp.Tests;

[Trait("tier", "ui")]
public sealed class SettingsAiSectionTests
{
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

    private sealed class FakeCmdrPathService : ICmdrPathService
    {
        public CmdrPathStatus GetStatus() => new CmdrPathStatus.Unavailable();
        public CmdrPathApplyResult Apply(PathChange confirmed) => new CmdrPathApplyResult.Failed("Unavailable");
    }

    private static ServiceProvider CreateServices(IAiIntegrationsPanel? panel)
    {
        var sc = new ServiceCollection();
        sc.AddSingleton<ISettingsService>(new InMemorySettingsService());
        sc.AddSingleton<IAiProfileService, AiProfileService>();
        if (panel is not null)
            sc.AddSingleton<IAiIntegrationsPanel>(panel);
        sc.AddSingleton<IAiClientFactory>(new AiTestSupport.Factory(new AiTestSupport.Client()));
        sc.AddSingleton<IUiDispatcher, Historia.Platform.UiDispatcher>();
        sc.AddSingleton<IDialogService, AiTestSupport.Dialogs>();
        sc.AddSingleton<IScrollDirectionProbe, Historia.Platform.ScrollDirectionProbe>();
        sc.AddSingleton<IRepoActionBus, RepoActionBus>();
        sc.AddSingleton<IRepoNetworkActionRunner, RepoNetworkActionRunner>();
        sc.AddSingleton<IAutoFetchHost, AutoFetchHost>();
        sc.AddSingleton<IAutoFetchService, AutoFetchService>();
        sc.AddSingleton<Historia.Services.UI.INavigationBus, Historia.Services.UI.NavigationBus>();
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
        sc.AddSingleton<Historia.Services.Storage.IRepoStateCache, Historia.Services.Storage.RepoStateCache>();
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
        sc.AddSingleton<ICmdrPathService, FakeCmdrPathService>();
        return sc.BuildServiceProvider();
    }

    /// <summary>The terminal shell-path TextBox and the Add rule button live in the "Terminal"
    /// and "Advanced" sections, which start collapsed (<c>_terminalExpanded = false</c>,
    /// <c>_advExpanded = false</c>) — their content is IsVisible=false and neither focusable
    /// nor hit-testable until expanded. Flip the private fields and re-run the private
    /// Rebuild() directly, the same reflection approach <c>SettingsNumericCommitTests</c> uses
    /// for other private members, rather than simulating a click on the section header.</summary>
    private static void ExpandSections(SettingsView view)
    {
        var termField = typeof(SettingsView).GetField("_terminalExpanded", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("SettingsView._terminalExpanded not found — renamed?");
        termField.SetValue(view, true);
        var advField = typeof(SettingsView).GetField("_advExpanded", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("SettingsView._advExpanded not found — renamed?");
        advField.SetValue(view, true);
        var rebuild = typeof(SettingsView).GetMethod("Rebuild", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("SettingsView.Rebuild not found — renamed?");
        rebuild.Invoke(view, null);
    }

    private static (SettingsView View, Window Window) Create(IAiIntegrationsPanel? panel)
    {
        var view = new SettingsView(CreateServices(panel));
        var window = new Window { Width = 900, Height = 2400, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (view, window);
    }

    private static TextBlock Find(SettingsView view, string text) =>
        view.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == text);

    private const string Hint = "AI services open in the centre pane.";

    [AvaloniaFact]
    public void The_AI_Integration_section_comes_after_Advanced()
    {
        var (view, _) = Create(new AiIntegrationsPanel());

        var texts = view.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();

        Assert.True(texts.IndexOf("AI Integration") > texts.IndexOf("Advanced"));
        Assert.True(texts.IndexOf("Advanced") >= 0);
    }

    [AvaloniaFact]
    public void The_section_starts_collapsed_while_the_window_is_closed()
    {
        var (view, _) = Create(new AiIntegrationsPanel());

        Assert.False(Find(view, Hint).IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void Expanding_the_section_opens_the_window()
    {
        var panel = new AiIntegrationsPanel();
        var (view, window) = Create(panel);
        var header = Find(view, "AI Integration");
        var point = header.TranslatePoint(new Point(2, 2), window)!.Value;

        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.True(panel.IsOpen);
        Assert.True(Find(view, Hint).IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void Closing_the_window_collapses_the_section()
    {
        var panel = new AiIntegrationsPanel();
        var (view, _) = Create(panel);
        panel.Open();
        Dispatcher.UIThread.RunJobs();
        Assert.True(Find(view, Hint).IsEffectivelyVisible);

        panel.Close();
        Dispatcher.UIThread.RunJobs();

        Assert.False(Find(view, Hint).IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void Collapsing_the_section_closes_the_window()
    {
        var panel = new AiIntegrationsPanel();
        var (view, window) = Create(panel);
        panel.Open();
        Dispatcher.UIThread.RunJobs();
        var header = Find(view, "AI Integration");
        var point = header.TranslatePoint(new Point(2, 2), window)!.Value;

        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.False(panel.IsOpen);
    }

    [AvaloniaFact]
    public void Without_the_panel_service_the_section_is_omitted()
    {
        var (view, _) = Create(panel: null);

        Assert.DoesNotContain(view.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "AI Integration");
    }

    [AvaloniaFact]
    public void Disposing_the_view_stops_it_following_the_panel()
    {
        var panel = new AiIntegrationsPanel();
        var (view, _) = Create(panel);
        view.Dispose();

        panel.Open();
        Dispatcher.UIThread.RunJobs();

        Assert.False(Find(view, Hint).IsEffectivelyVisible);
    }
}
