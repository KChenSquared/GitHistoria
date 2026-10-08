namespace HistoriaApp.Tests;

[Trait("tier", "ui")]
public class StagingPanelAiButtonsTests : IDisposable
{
    private sealed class FakeGenerator : ICommitMessageGenerator
    {
        public int Calls { get; private set; }
        public bool? LastAmend { get; private set; }
        public CancellationToken LastToken { get; private set; }

        public Func<CancellationToken, Task<CommitMessageResult>> Behavior { get; set; } =
            _ => Task.FromResult(new CommitMessageResult("feat: from ai", null, null));

        public Task<CommitMessageResult> GenerateAsync(RepositoryContext ctx, bool amend, CancellationToken ct)
        {
            Calls++;
            LastAmend = amend;
            LastToken = ct;
            return Behavior(ct);
        }
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

    private readonly string _repoDir;
    private readonly ServiceProvider _sp;
    private readonly RepositoryContext _context;
    private readonly AiTestSupport.Dialogs _dialogs = new();
    private readonly AiTestSupport.Notifications _notifications = new();
    private readonly InMemorySettingsService _settings = new();
    private readonly AiProfileService _profiles;
    private readonly AiIntegrationsPanel _panel = new();
    private readonly FakeGenerator _generator = new();
    private readonly List<StagingPanelView> _views = [];

    public StagingPanelAiButtonsTests()
    {
        _repoDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "historia-staging-ai-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_repoDir);
        LibGit2Sharp.Repository.Init(_repoDir);
        File.WriteAllText(System.IO.Path.Combine(_repoDir, "a.txt"), "one\n");
        using (var repo = new LibGit2Sharp.Repository(_repoDir))
        {
            LibGit2Sharp.Commands.Stage(repo, "a.txt");
            var sig = new LibGit2Sharp.Signature("Test", "test@example.com", DateTimeOffset.Now);
            repo.Commit("initial", sig, sig, new LibGit2Sharp.CommitOptions());
        }

        _profiles = new AiProfileService(_settings);

        var sc = new ServiceCollection();
        sc.AddSingleton<ISettingsService>(_settings);
        sc.AddSingleton<IUiDispatcher, Historia.Platform.UiDispatcher>();
        sc.AddSingleton<IDialogService>(_dialogs);
        sc.AddSingleton<IScrollDirectionProbe, Historia.Platform.ScrollDirectionProbe>();
        sc.AddSingleton<IRepoActionBus, RepoActionBus>();
        sc.AddSingleton<IRepoNetworkActionRunner, RepoNetworkActionRunner>();
        sc.AddSingleton<IAutoFetchHost, AutoFetchHost>();
        sc.AddSingleton<IAutoFetchService, AutoFetchService>();
        sc.AddSingleton<INavigationBus, NavigationBus>();
        sc.AddSingleton<IKeyboardCommandRouter, KeyboardCommandRouter>();
        sc.AddSingleton<INotificationService>(_notifications);
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
        sc.AddSingleton<Historia.Theming.AvaloniaThemeApplier>();
        sc.AddSingleton<IAiProfileService>(_profiles);
        sc.AddSingleton<IAiIntegrationsPanel>(_panel);
        sc.AddSingleton<ICommitMessageGenerator>(_generator);
        _sp = sc.BuildServiceProvider();
        _sp.GetRequiredService<Historia.Theming.AvaloniaThemeApplier>();

        var ws = _sp.GetRequiredService<IWorkspaceService>();
        _context = ws.OpenAsync(_repoDir).GetAwaiter().GetResult();
        ws.Focus(_context);
    }

    public void Dispose()
    {
        foreach (var view in _views)
            try { view.Dispose(); } catch { }
        try { _context.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
        try { _sp.Dispose(); } catch { }
        try { Directory.Delete(_repoDir, true); } catch { }
    }

    private async Task StageChangeAsync()
    {
        File.WriteAllText(System.IO.Path.Combine(_repoDir, "a.txt"), "one\ntwo\n");
        using (var repo = new LibGit2Sharp.Repository(_repoDir))
            LibGit2Sharp.Commands.Stage(repo, "a.txt");
        await _context.State.RefreshStatusAsync();
        Dispatcher.UIThread.RunJobs();
    }

    private async Task<AiServiceProfile> ActivateAsync(Func<AiServiceProfile, AiServiceProfile>? configure = null, string name = "Gemini")
    {
        var added = await _profiles.AddAsync();
        await _profiles.UpdateAsync(added.Id, p => (configure?.Invoke(p) ?? p with { Server = "https://ai.example.test/v1", Model = "m", ApiKey = "key" }) with { Name = name });
        await _profiles.SetActiveAsync(added.Id);
        Dispatcher.UIThread.RunJobs();
        return _profiles.Profiles.Single(p => p.Id == added.Id);
    }

    private StagingPanelView NewPanel()
    {
        var panel = new StagingPanelView(_sp, _context);
        _views.Add(panel);
        Dispatcher.UIThread.RunJobs();
        return panel;
    }

    private static void Click(Button button)
    {
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
    }

    private static Task Settled(StagingPanelView panel) =>
        AiTestSupport.PumpUntil(() => panel.GenerationForTesting.IsCompleted && !panel.IsGeneratingForTesting);

    // ── Buttons ──

    [AvaloniaFact]
    public void The_buttons_sit_left_in_the_amend_row_wand_first()
    {
        var panel = NewPanel();

        var strip = Assert.IsType<StackPanel>(panel.CommitOptionsRowForTesting!.Children.OfType<StackPanel>().Single());

        Assert.Equal(0, Grid.GetColumn(strip));
        Assert.Equal(HorizontalAlignment.Left, strip.HorizontalAlignment);
        Assert.Same(panel.WandForTesting, strip.Children[0]);
        Assert.Same(panel.AiSettingsForTesting, strip.Children[1]);
        Assert.Empty(panel.MessageCellForTesting!.Children.OfType<StackPanel>());
        Assert.Equal("Setup AI Integration", ToolTip.GetTip(panel.AiSettingsForTesting!));
        Assert.Equal("Generate Commit Message", ToolTip.GetTip(panel.WandForTesting!));
    }

    [AvaloniaFact]
    public void The_amend_checkbox_reads_Amend_and_sits_right()
    {
        var panel = NewPanel();

        Assert.Equal("Amend", panel.AmendForTesting.Content);
        Assert.Equal(2, Grid.GetColumn(panel.AmendForTesting));
        Assert.Equal(HorizontalAlignment.Right, panel.AmendForTesting.HorizontalAlignment);
    }

    [AvaloniaFact]
    public async Task The_icons_match_the_amend_text_size()
    {
        var panel = NewPanel();
        var fontSize = panel.AmendForTesting.FontSize;

        var wandPath = Assert.IsType<Avalonia.Controls.Shapes.Path>(panel.WandForTesting!.Content);
        var gearPath = Assert.IsType<Avalonia.Controls.Shapes.Path>(panel.AiSettingsForTesting!.Content);
        Assert.Equal(fontSize, wandPath.Width);
        Assert.Equal(fontSize, wandPath.Height);
        Assert.Equal(fontSize, gearPath.Width);
        Assert.Equal(fontSize, gearPath.Height);

        await ActivateAsync();

        var activeWandPath = Assert.IsType<Avalonia.Controls.Shapes.Path>(panel.WandForTesting.Content);
        Assert.Equal(fontSize, activeWandPath.Width);
        Assert.Equal(fontSize, activeWandPath.Height);
    }

    [AvaloniaFact]
    public void The_message_box_reserves_bottom_padding_for_the_counter()
    {
        var panel = NewPanel();

        Assert.True(panel.MessageForTesting.Padding.Bottom >= 16);
    }

    [AvaloniaFact]
    public async Task The_wand_is_disabled_with_no_active_profile_and_enabled_once_one_is_activated()
    {
        var panel = NewPanel();
        Assert.False(panel.WandForTesting!.IsEnabled);

        await ActivateAsync();

        Assert.True(panel.WandForTesting.IsEnabled);
    }

    [AvaloniaFact]
    public void The_disabled_wand_still_shows_its_tooltip()
    {
        var panel = NewPanel();

        Assert.False(panel.WandForTesting!.IsEnabled);
        Assert.True(ToolTip.GetShowOnDisabled(panel.WandForTesting));
        Assert.Equal("Generate Commit Message", ToolTip.GetTip(panel.WandForTesting));
    }

    [AvaloniaFact]
    public async Task The_wand_is_green_when_active_and_grey_when_not()
    {
        var panel = NewPanel();
        var window = new Window { Width = 600, Height = 900, Content = panel };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        if (Application.Current is null || !Application.Current.TryFindResource("ColorBorder", out var border) || border is not IBrush borderBrush)
            return;
        Assert.Equal(borderBrush, panel.WandForTesting!.BorderBrush);

        await ActivateAsync();

        Assert.True(Application.Current.TryFindResource("ColorSuccess", out var success));
        Assert.Equal(success, panel.WandForTesting.BorderBrush);
    }

    [AvaloniaFact]
    public async Task The_settings_button_opens_the_panel_on_the_active_profile()
    {
        var active = await ActivateAsync();
        var panel = NewPanel();

        Click(panel.AiSettingsForTesting!);

        Assert.True(_panel.IsOpen);
        Assert.Equal(active.Id, _panel.FocusProfileId);
    }

    [AvaloniaFact]
    public void The_settings_button_opens_the_panel_with_nothing_active()
    {
        var panel = NewPanel();

        Click(panel.AiSettingsForTesting!);

        Assert.True(_panel.IsOpen);
        Assert.Null(_panel.FocusProfileId);
    }

    // ── Generate ──

    [AvaloniaFact]
    public async Task An_empty_box_is_filled_with_no_confirmation()
    {
        await ActivateAsync();
        await StageChangeAsync();
        var panel = NewPanel();

        Click(panel.WandForTesting!);
        await Settled(panel);

        Assert.Equal("feat: from ai", panel.MessageForTesting.Text);
        Assert.Empty(_dialogs.ConfirmDestructiveCalls);
        Assert.Contains(_notifications.Published, n => n.Severity == NotificationSeverity.Info && n.Message == "Commit message generated by 1. Gemini.");
        Assert.Equal(false, _generator.LastAmend);
        Assert.False(panel.MessageForTesting.IsReadOnly);
    }

    [AvaloniaFact]
    public async Task A_non_empty_box_asks_before_replacing_and_cancel_changes_nothing()
    {
        await ActivateAsync();
        await StageChangeAsync();
        var panel = NewPanel();
        panel.MessageForTesting.Text = "my draft";
        _dialogs.ConfirmDestructiveResult = false;

        Click(panel.WandForTesting!);
        await AiTestSupport.PumpUntil(() => _dialogs.ConfirmDestructiveCalls.Count == 1);
        await Settled(panel);

        var call = Assert.Single(_dialogs.ConfirmDestructiveCalls);
        Assert.Equal("Replace commit message", call.Title);
        Assert.Equal("Replace your commit message draft (8 characters) with an AI-generated message? Your draft is discarded.", call.Effect);
        Assert.Equal("Replace", call.ConfirmLabel);
        Assert.Equal("my draft", panel.MessageForTesting.Text);
        Assert.Equal(0, _generator.Calls);
    }

    [AvaloniaFact]
    public async Task Confirming_replaces_the_draft_with_the_generated_message()
    {
        await ActivateAsync();
        await StageChangeAsync();
        var panel = NewPanel();
        panel.MessageForTesting.Text = "my draft";

        Click(panel.WandForTesting!);
        await AiTestSupport.PumpUntil(() => panel.MessageForTesting.Text == "feat: from ai");
        await Settled(panel);

        Assert.Equal(1, _generator.Calls);
    }

    [AvaloniaFact]
    public async Task A_missing_key_opens_the_panel_and_makes_no_generator_call()
    {
        var active = await ActivateAsync(p => p with { Server = "https://ai.example.test/v1", Model = "m", ApiKey = "", ReadApiKeyFromEnv = true });
        await StageChangeAsync();
        var panel = NewPanel();

        Click(panel.WandForTesting!);
        await Settled(panel);

        Assert.True(_panel.IsOpen);
        Assert.Equal(active.Id, _panel.FocusProfileId);
        Assert.Equal(0, _generator.Calls);
        Assert.Contains(_notifications.Published, n => n.Severity == NotificationSeverity.Warning && n.Message.Contains("API key"));
    }

    [AvaloniaFact]
    public async Task A_keyless_custom_profile_may_generate()
    {
        await ActivateAsync(p => p with { Server = "https://ai.example.test/v1", Model = "m", ApiKey = "" });
        await StageChangeAsync();
        var panel = NewPanel();

        Click(panel.WandForTesting!);
        await Settled(panel);

        Assert.Equal(1, _generator.Calls);
        Assert.False(_panel.IsOpen);
    }

    [AvaloniaFact]
    public async Task Nothing_staged_warns_without_a_generator_call()
    {
        await ActivateAsync();
        var panel = NewPanel();

        Click(panel.WandForTesting!);
        await Settled(panel);

        Assert.Equal(0, _generator.Calls);
        Assert.Contains(_notifications.Published, n => n.Severity == NotificationSeverity.Warning && n.Message == "Stage changes before generating a commit message.");
    }

    [AvaloniaFact]
    public async Task Amend_lets_it_generate_with_nothing_staged_and_passes_the_flag()
    {
        await ActivateAsync();
        var panel = NewPanel();
        panel.AmendForTesting.IsChecked = true;

        Click(panel.WandForTesting!);
        await Settled(panel);

        Assert.Equal(1, _generator.Calls);
        Assert.Equal(true, _generator.LastAmend);
    }

    [AvaloniaFact]
    public async Task A_second_click_cancels_the_request_and_leaves_the_draft_alone()
    {
        await ActivateAsync();
        await StageChangeAsync();
        var panel = NewPanel();
        panel.MessageForTesting.Text = "keep me";
        var gate = new TaskCompletionSource<CommitMessageResult>();
        _generator.Behavior = ct =>
        {
            ct.Register(() => gate.TrySetCanceled(ct));
            return gate.Task;
        };

        Click(panel.WandForTesting!);
        await AiTestSupport.PumpUntil(() => panel.IsGeneratingForTesting);

        Assert.True(panel.MessageForTesting.IsReadOnly);
        Assert.Equal("Generating… click to cancel", ToolTip.GetTip(panel.WandForTesting!));
        Assert.True(panel.WandForTesting!.IsEnabled);

        Click(panel.WandForTesting);
        await Settled(panel);

        Assert.True(_generator.LastToken.IsCancellationRequested);
        Assert.Equal("keep me", panel.MessageForTesting.Text);
        Assert.False(panel.MessageForTesting.IsReadOnly);
        Assert.Equal("Generate Commit Message", ToolTip.GetTip(panel.WandForTesting));
        Assert.DoesNotContain(_notifications.Published, n => n.Severity is NotificationSeverity.Error or NotificationSeverity.Warning);
    }

    [AvaloniaFact]
    public async Task An_error_leaves_the_draft_intact_and_reports_the_classified_failure()
    {
        await ActivateAsync();
        await StageChangeAsync();
        var panel = NewPanel();
        panel.MessageForTesting.Text = "keep me";
        _generator.Behavior = _ => throw new AiException(AiErrorKind.Unauthorized, "The AI service rejected the API key.", "HTTP 401");

        Click(panel.WandForTesting!);
        await Settled(panel);

        Assert.Equal("keep me", panel.MessageForTesting.Text);
        var error = Assert.Single(_notifications.Published, n => n.Severity == NotificationSeverity.Error);
        Assert.Equal("The AI service rejected the API key.", error.Message);
        Assert.Equal("HTTP 401", error.Details);
        Assert.False(panel.MessageForTesting.IsReadOnly);
    }

    [AvaloniaFact]
    public async Task A_model_switch_publishes_the_replacement_warning()
    {
        await ActivateAsync();
        await StageChangeAsync();
        var panel = NewPanel();
        _generator.Behavior = _ => Task.FromResult(new CommitMessageResult("feat: x", "gemini-1.0-pro", "gemini-2.5-flash"));

        Click(panel.WandForTesting!);
        await Settled(panel);

        Assert.Contains(_notifications.Published, n => n.Severity == NotificationSeverity.Warning
            && n.Message == "'gemini-1.0-pro' is no longer available. '1. Gemini' now uses 'gemini-2.5-flash'.");
    }

    [AvaloniaFact]
    public async Task Disposing_during_generation_cancels_it_and_keeps_the_pre_generation_draft()
    {
        await ActivateAsync();
        await StageChangeAsync();
        var panel = NewPanel();
        panel.MessageForTesting.Text = "old draft";
        var gate = new TaskCompletionSource<CommitMessageResult>();
        _generator.Behavior = ct =>
        {
            ct.Register(() => gate.TrySetCanceled(ct));
            return gate.Task;
        };

        Click(panel.WandForTesting!);
        await AiTestSupport.PumpUntil(() => panel.IsGeneratingForTesting);

        panel.Dispose();
        await AiTestSupport.PumpUntil(() => _generator.LastToken.IsCancellationRequested);

        var reopened = NewPanel();
        Assert.Equal("old draft", reopened.MessageForTesting.Text);
    }

    [AvaloniaFact]
    public async Task Committing_during_generation_does_nothing()
    {
        await ActivateAsync();
        await StageChangeAsync();
        var panel = NewPanel();
        panel.MessageForTesting.Text = "feat: old draft";
        var gate = new TaskCompletionSource<CommitMessageResult>();
        _generator.Behavior = ct =>
        {
            ct.Register(() => gate.TrySetCanceled(ct));
            return gate.Task;
        };
        Click(panel.WandForTesting!);
        await AiTestSupport.PumpUntil(() => panel.IsGeneratingForTesting);

        await panel.CommitForTestingAsync();

        using (var repo = new LibGit2Sharp.Repository(_repoDir))
            Assert.Single(repo.Commits);
        Assert.Equal("feat: old draft", panel.MessageForTesting.Text);

        Click(panel.WandForTesting!);
        await Settled(panel);
    }

    [AvaloniaFact]
    public async Task Dispose_unsubscribes_from_profile_changes()
    {
        var panel = NewPanel();
        panel.Dispose();

        await ActivateAsync();

        Assert.False(panel.WandForTesting!.IsEnabled);
    }
}
