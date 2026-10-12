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
    private readonly AiTestSupport.Client _client = new();
    private readonly AiTestSupport.Factory _factory;
    private readonly AiTestSupport.CountingCatalog _catalog;

    public StagingPanelAiButtonsTests()
    {
        _repoDir = TestScratch.NewDirectory("historia-staging-ai-");
        LibGit2Sharp.Repository.Init(_repoDir);
        File.WriteAllText(System.IO.Path.Combine(_repoDir, "a.txt"), "one\n");
        using (var repo = new LibGit2Sharp.Repository(_repoDir))
        {
            LibGit2Sharp.Commands.Stage(repo, "a.txt");
            var sig = new LibGit2Sharp.Signature("Test", "test@example.com", DateTimeOffset.Now);
            repo.Commit("initial", sig, sig, new LibGit2Sharp.CommitOptions());
        }

        _profiles = new AiProfileService(_settings);
        _factory = new AiTestSupport.Factory(_client);
        _catalog = new AiTestSupport.CountingCatalog(new AiModelCatalog(_profiles, _factory));

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
        sc.AddSingleton<IAiClientFactory>(_factory);
        sc.AddSingleton<IAiModelCatalog>(_catalog);
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
        TestScratch.Delete(_repoDir);
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
        var baseProfile = added with { Server = "https://ai.example.test/v1", Model = "m", ApiKey = "key", Name = name };
        var configured = configure is not null ? configure(baseProfile) : baseProfile;
        await _profiles.UpdateAsync(added.Id, _ => configured);
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

        var strip = panel.CommitOptionsRowForTesting!.Children.OfType<StackPanel>().Single();

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
        Assert.Equal(4, Grid.GetColumn(panel.AmendForTesting));
        Assert.Equal(HorizontalAlignment.Right, panel.AmendForTesting.HorizontalAlignment);
    }

    [AvaloniaFact]
    public async Task The_icons_are_two_px_above_the_amend_text_size()
    {
        var panel = NewPanel();
        var glyphSize = panel.AmendForTesting.FontSize + 2;

        var wandPath = Assert.IsType<Avalonia.Controls.Shapes.Path>(panel.WandForTesting!.Content);
        var gearPath = Assert.IsType<Avalonia.Controls.Shapes.Path>(panel.AiSettingsForTesting!.Content);
        Assert.Equal(glyphSize, wandPath.Width);
        Assert.Equal(glyphSize, wandPath.Height);
        Assert.Equal(glyphSize, gearPath.Width);
        Assert.Equal(glyphSize, gearPath.Height);

        await ActivateAsync();

        var activeWandPath = Assert.IsType<Avalonia.Controls.Shapes.Path>(panel.WandForTesting.Content);
        Assert.Equal(glyphSize, activeWandPath.Width);
        Assert.Equal(glyphSize, activeWandPath.Height);
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
        Assert.DoesNotContain(_notifications.Published, n => n.Severity == NotificationSeverity.Info);
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

    // ── Model picker ──

    [AvaloniaFact]
    public void The_picker_sits_right_of_the_cog_and_the_divider_sits_left_of_amend()
    {
        var panel = NewPanel();

        Assert.NotNull(panel.ModelPickerForTesting);
        Assert.NotNull(panel.AmendDividerForTesting);
        Assert.Equal(1, Grid.GetColumn(panel.ModelPickerForTesting!));
        Assert.Equal(3, Grid.GetColumn(panel.AmendDividerForTesting!));
        Assert.Equal(4, Grid.GetColumn(panel.AmendForTesting));
    }

    [AvaloniaFact]
    public void The_divider_is_one_px_in_the_border_colour()
    {
        var panel = NewPanel();
        var window = new Window { Width = 600, Height = 900, Content = panel };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var divider = panel.AmendDividerForTesting!;
        Assert.Equal(1, divider.Width);
        Assert.Equal(VerticalAlignment.Stretch, divider.VerticalAlignment);
        Assert.Equal(new Thickness(8, 2), divider.Margin);

        if (Application.Current is not null && Application.Current.TryFindResource("ColorBorder", out var border) && border is IBrush borderBrush)
        {
            Assert.Equal(borderBrush, divider.Background);
        }
    }

    [AvaloniaFact]
    public void With_no_active_profile_the_picker_is_visible_disabled_and_reads_Select_a_Model()
    {
        var panel = NewPanel();
        var picker = panel.ModelPickerForTesting!;

        Assert.True(picker.IsVisible);
        Assert.False(picker.IsEnabled);
        Assert.Null(picker.SelectedItem);
        Assert.Equal("Select a Model", picker.PlaceholderText);
        Assert.Equal("Set up an AI integration to choose a model", ToolTip.GetTip(picker));
    }

    [AvaloniaFact]
    public async Task The_picker_shows_the_active_profiles_model()
    {
        var active = await ActivateAsync(p => p with { Model = "gemini-2.5-flash" }, name: "Gemini");
        var panel = NewPanel();
        var picker = panel.ModelPickerForTesting!;

        Assert.True(picker.IsEnabled);
        Assert.Equal("gemini-2.5-flash", picker.SelectedItem);
        Assert.Contains($"Model for {_profiles.DisplayLabel(active)}", ToolTip.GetTip(picker) as string);
    }

    [AvaloniaFact]
    public async Task A_blank_model_reads_Select_a_Model()
    {
        await ActivateAsync(p => p with { Model = "" });
        var panel = NewPanel();
        var picker = panel.ModelPickerForTesting!;

        Assert.True(picker.IsEnabled);
        Assert.Null(picker.SelectedItem);
        Assert.Equal("Select a Model", picker.PlaceholderText);
    }

    [AvaloniaFact]
    public async Task A_loaded_list_without_the_saved_model_reads_Select_a_Model_and_changes_nothing()
    {
        var active = await ActivateAsync(p => p with { Model = "retired-model" });
        var catalog = _sp.GetRequiredService<IAiModelCatalog>();
        catalog.Remember(active, ["new-model-1", "new-model-2"]);

        var panel = NewPanel();
        var picker = panel.ModelPickerForTesting!;

        Assert.Null(picker.SelectedItem);
        Assert.Equal("retired-model", _profiles.ActiveProfile!.Model);
    }

    [AvaloniaFact]
    public async Task Picking_a_model_saves_it_to_the_active_profile_only()
    {
        var p1 = await ActivateAsync(p => p with { Model = "m1" }, name: "P1");
        var p2Added = await _profiles.AddAsync();
        await _profiles.UpdateAsync(p2Added.Id, p => p with { Name = "P2", Model = "m2" });

        var catalog = _sp.GetRequiredService<IAiModelCatalog>();
        catalog.Remember(p1, ["m1", "m1-new"]);

        var panel = NewPanel();
        var picker = panel.ModelPickerForTesting!;
        picker.SelectedItem = "m1-new";
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("m1-new", _profiles.ActiveProfile!.Model);
        Assert.Equal("m2", _profiles.Profiles.Single(p => p.Id == p2Added.Id).Model);
    }

    [AvaloniaFact]
    public async Task Switching_the_active_profile_switches_the_picker()
    {
        var p1 = await ActivateAsync(p => p with { Model = "m1" });
        var p2Added = await _profiles.AddAsync();
        await _profiles.UpdateAsync(p2Added.Id, p => p with { Model = "m2" });

        var panel = NewPanel();
        var picker = panel.ModelPickerForTesting!;
        Assert.Equal("m1", picker.SelectedItem);

        await _profiles.SetActiveAsync(p2Added.Id);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("m2", picker.SelectedItem);
    }

    [AvaloniaFact]
    public async Task Constructing_the_panel_makes_no_model_request()
    {
        await ActivateAsync();
        Assert.Empty(_client.Lists);

        _ = NewPanel();

        Assert.Empty(_client.Lists);
    }

    [AvaloniaFact]
    public async Task Opening_the_picker_loads_the_list_once_with_auto_fetch_on()
    {
        _client.ListResult = () => ["model-a", "model-b"];
        await ActivateAsync(p => p with { AutoFetchModels = true });
        var panel = NewPanel();

        panel.OpenModelPickerForTesting();
        await panel.ModelLoadForTesting;

        Assert.Single(_client.Lists);

        panel.OpenModelPickerForTesting();
        await panel.ModelLoadForTesting;

        Assert.Single(_client.Lists);
    }

    [AvaloniaFact]
    public async Task Opening_the_picker_with_auto_fetch_off_makes_no_request_and_offers_a_cached_list()
    {
        var active = await ActivateAsync(p => p with { AutoFetchModels = false, Model = "current-model" });
        var panel = NewPanel();
        var picker = panel.ModelPickerForTesting!;

        panel.OpenModelPickerForTesting();
        await panel.ModelLoadForTesting;

        Assert.Empty(_client.Lists);
        Assert.Equal(["current-model"], picker.ItemsSource as IEnumerable<string>);
        Assert.Contains("Press refresh in AI Integrations to load models", ToolTip.GetTip(picker) as string);

        var catalog = _sp.GetRequiredService<IAiModelCatalog>();
        catalog.Remember(active, ["current-model", "cached-other"]);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(["current-model", "cached-other"], picker.ItemsSource as IEnumerable<string>);
    }

    [AvaloniaFact]
    public async Task The_dropdown_opened_event_reaches_the_load()
    {
        _client.ListResult = () => ["model-1", "model-2"];
        await ActivateAsync(p => p with { AutoFetchModels = true });
        var panel = NewPanel();
        var window = new Window { Width = 600, Height = 900, Content = panel };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        panel.ModelPickerForTesting!.IsDropDownOpen = true;
        Dispatcher.UIThread.RunJobs();
        await panel.ModelLoadForTesting;

        Assert.Single(_client.Lists);
    }

    [AvaloniaFact]
    public async Task A_failed_list_load_publishes_the_classified_notification()
    {
        _client.ListResult = () => throw new AiException(AiErrorKind.Unauthorized, "unauthorized key");
        await ActivateAsync(p => p with { AutoFetchModels = true });
        var panel = NewPanel();

        panel.OpenModelPickerForTesting();
        await panel.ModelLoadForTesting;

        Assert.Contains(_notifications.Published, n => n.Severity == NotificationSeverity.Error && n.Message.Contains("Couldn't load the model list"));
    }

    [AvaloniaFact]
    public async Task The_picker_fills_the_row_at_the_narrowest_right_column()
    {
        await ActivateAsync();
        var panel = NewPanel();
        var window = new Window { Width = 360, Height = 600, Content = panel };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var picker = panel.ModelPickerForTesting!;
        var divider = panel.AmendDividerForTesting!;
        var slack = divider.Bounds.Left - 8 - picker.Bounds.Right;
        Assert.InRange(slack, 0, 2);
    }

    [AvaloniaFact]
    public async Task The_picker_width_is_fixed_and_the_divider_stays_next_to_amend()
    {
        await ActivateAsync();
        var panel = NewPanel();
        var window360 = new Window { Width = 360, Height = 600, Content = panel };
        window360.Show();
        Dispatcher.UIThread.RunJobs();

        var picker = panel.ModelPickerForTesting!;
        var divider = panel.AmendDividerForTesting!;
        var amend = panel.AmendForTesting;

        var pickerWidth360 = picker.Bounds.Width;
        var gap360 = amend.Bounds.Left - divider.Bounds.Right;

        window360.Close();

        var panel900 = NewPanel();
        var window900 = new Window { Width = 900, Height = 600, Content = panel900 };
        window900.Show();
        Dispatcher.UIThread.RunJobs();

        var picker900 = panel900.ModelPickerForTesting!;
        var divider900 = panel900.AmendDividerForTesting!;
        var amend900 = panel900.AmendForTesting;

        Assert.Equal(pickerWidth360, picker900.Bounds.Width);
        Assert.Equal(gap360, amend900.Bounds.Left - divider900.Bounds.Right);
    }

    [AvaloniaFact]
    public async Task The_picker_does_not_grow_the_options_row()
    {
        await ActivateAsync();
        var panel = NewPanel();
        var window = new Window { Width = 600, Height = 600, Content = panel };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var row = panel.CommitOptionsRowForTesting!;
        var amend = panel.AmendForTesting;
        Assert.Equal(amend.Bounds.Height, row.Bounds.Height);
    }

    [AvaloniaFact]
    public async Task An_unrelated_profile_save_keeps_the_items_and_selection()
    {
        var active = await ActivateAsync(p => p with { Model = "m1" });
        var catalog = _sp.GetRequiredService<IAiModelCatalog>();
        catalog.Remember(active, ["m1", "m2"]);

        var panel = NewPanel();
        var picker = panel.ModelPickerForTesting!;
        var itemsBefore = picker.ItemsSource;

        await _profiles.UpdateAsync(active.Id, p => p with { Name = "Renamed" });
        Dispatcher.UIThread.RunJobs();

        var itemsAfter = picker.ItemsSource;
        Assert.Same(itemsBefore, itemsAfter);
        Assert.Equal("m1", picker.SelectedItem);
    }

    [AvaloniaFact]
    public async Task Disposing_stops_listening_to_the_catalog()
    {
        var active = await ActivateAsync();
        var before = _catalog.Subscribers;
        var panel = NewPanel();
        var picker = panel.ModelPickerForTesting!;
        Assert.Equal(before + 1, _catalog.Subscribers);

        panel.Dispose();

        _catalog.Remember(active, ["new-model"]);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(before, _catalog.Subscribers);
        Assert.Equal(["m"], picker.ItemsSource as IEnumerable<string>);
    }

    [AvaloniaFact]
    public async Task Reopening_the_picker_while_the_list_loads_makes_no_second_request()
    {
        _client.ListResult = () => ["m", "model-b"];
        _client.ListGate = new TaskCompletionSource();
        await ActivateAsync(p => p with { AutoFetchModels = true });
        var panel = NewPanel();
        var picker = panel.ModelPickerForTesting!;

        panel.OpenModelPickerForTesting();
        await AiTestSupport.PumpUntil(() => _client.Lists.Count == 1);
        var firstLoad = panel.ModelLoadForTesting;
        panel.OpenModelPickerForTesting();
        Dispatcher.UIThread.RunJobs();

        Assert.Same(firstLoad, panel.ModelLoadForTesting);
        Assert.Equal("Loading…", picker.PlaceholderText);

        _client.ListGate.SetResult();
        await panel.ModelLoadForTesting;
        Dispatcher.UIThread.RunJobs();

        Assert.Single(_client.Lists);
        Assert.Equal(["m", "model-b"], picker.ItemsSource as IEnumerable<string>);
        Assert.Equal("Select a Model", picker.PlaceholderText);
    }

    [AvaloniaFact]
    public async Task The_tooltip_says_loading_while_the_list_loads_over_a_selected_model()
    {
        _client.ListResult = () => ["m", "model-b"];
        _client.ListGate = new TaskCompletionSource();
        await ActivateAsync(p => p with { AutoFetchModels = true });
        var panel = NewPanel();
        var picker = panel.ModelPickerForTesting!;

        panel.OpenModelPickerForTesting();
        await AiTestSupport.PumpUntil(() => _client.Lists.Count == 1);

        Assert.Equal("m", picker.SelectedItem);
        Assert.Contains("Loading models…", ToolTip.GetTip(picker) as string);

        _client.ListGate.SetResult();
        await panel.ModelLoadForTesting;
        Dispatcher.UIThread.RunJobs();

        Assert.DoesNotContain("Loading models…", ToolTip.GetTip(picker) as string);
        Assert.StartsWith("Model for ", ToolTip.GetTip(picker) as string);
    }
}
