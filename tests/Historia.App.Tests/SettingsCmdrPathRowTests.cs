namespace HistoriaApp.Tests;

[Trait("tier", "ui")]
public sealed class SettingsCmdrPathRowTests
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

    private sealed class ScriptedDialogService : IDialogService
    {
        public bool ConfirmResult { get; set; } = true;
        public List<(string Title, string Effect, string ConfirmLabel)> DestructiveCalls { get; } = [];
        public List<(string Title, string Message)> ConfirmCalls { get; } = [];
        public List<(string Title, string Message)> ShowMessageCalls { get; } = [];

        public Task<bool> ConfirmDestructiveAsync(string title, string effect, string confirmLabel)
        {
            DestructiveCalls.Add((title, effect, confirmLabel));
            return Task.FromResult(ConfirmResult);
        }

        public Task<bool> ConfirmAsync(string title, string message, bool destructive = false,
            string confirmLabel = "OK", bool defaultToCancel = false, DialogIcon icon = DialogIcon.None)
        {
            ConfirmCalls.Add((title, message));
            return Task.FromResult(ConfirmResult);
        }

        public Task ShowMessageAsync(string title, string message)
        {
            ShowMessageCalls.Add((title, message));
            return Task.CompletedTask;
        }

        public Task<string?> PickFolderAsync(string title, string? initialDirectory = null) => Task.FromResult<string?>(null);
        public Task<string?> PickFileAsync(string title, string filter, string? initialDirectory = null) => Task.FromResult<string?>(null);
        public Task<int> ChooseAsync(string title, string message, IReadOnlyList<string> options,
            int defaultIndex = 0, int destructiveIndex = -1, DialogIcon icon = DialogIcon.None) => Task.FromResult(-1);
        public Task<string?> PromptAsync(string title, string message, string? defaultValue = null,
            string confirmLabel = "OK", bool primaryConfirm = false) => Task.FromResult<string?>(null);
    }

    private sealed class RecordingCmdrPathService : ICmdrPathService
    {
        public CmdrPathStatus StatusToReturn { get; set; } = new CmdrPathStatus.Current();
        public CmdrPathApplyResult ApplyResultToReturn { get; set; } = new CmdrPathApplyResult.Written(@"C:\Backups\path.reg");
        public List<PathChange> ApplyCalls { get; } = [];
        public bool ThrowFromGetStatus { get; set; }
        public bool ThrowFromApply { get; set; }
        public TaskCompletionSource<bool>? ApplyGate { get; set; }
        public TaskCompletionSource<bool>? SubsequentStatusGate { get; set; }
        private int _statusCallCount;

        public int StatusCallCount => _statusCallCount;

        public CmdrPathStatus GetStatus()
        {
            if (ThrowFromGetStatus)
                throw new InvalidOperationException("Status error");

            _statusCallCount++;
            if (_statusCallCount > 1 && SubsequentStatusGate != null)
            {
                SubsequentStatusGate.Task.GetAwaiter().GetResult();
            }

            return StatusToReturn;
        }

        public CmdrPathApplyResult Apply(PathChange confirmed)
        {
            ApplyCalls.Add(confirmed);

            if (ApplyGate != null)
            {
                ApplyGate.Task.GetAwaiter().GetResult();
            }

            if (ThrowFromApply)
                throw new InvalidOperationException("Apply error");

            return ApplyResultToReturn;
        }
    }

    private static ServiceProvider CreateServices(
        RecordingCmdrPathService cmdrService,
        ScriptedDialogService dialogService)
    {
        var sc = new ServiceCollection();
        var settings = SettingsService.Load();
        sc.AddSingleton<ISettingsService>(settings);
        sc.AddSingleton<SettingsService>(settings);
        sc.AddSingleton<IUiDispatcher, Historia.Platform.UiDispatcher>();
        sc.AddSingleton<IDialogService>(dialogService);
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
        sc.AddSingleton<ICmdrPathService>(cmdrService);
        return sc.BuildServiceProvider();
    }

    private static StackPanel GeneralBody(SettingsView view)
    {
        Attach(view);
        EnsureCmdrStatusLoaded(view);
        return GeneralBodyNow(view);
    }

    /// <summary>A button follows its command's CanExecute only while it is in a window.</summary>
    private static void Attach(SettingsView view)
    {
        if (view.GetVisualRoot() is not null)
            return;

        new Window { Content = view }.Show();
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>The General body as rendered now, without waiting for a status refresh that a
    /// test may be holding back.</summary>
    private static StackPanel GeneralBodyNow(SettingsView view)
    {
        var root = view.FindControl<StackPanel>("PageRoot")!;
        var header = root.Children.OfType<SettingsSectionHeader>().Single(h =>
            h.GetLogicalDescendants().OfType<TextBlock>().Any(t => t.Text == "General"));
        return (StackPanel)root.Children[root.Children.IndexOf(header) + 1];
    }

    /// <summary>Waits for the PATH status check the view starts and lets its result land.</summary>
    private static void EnsureCmdrStatusLoaded(SettingsView view)
    {
        view.ViewModel.CmdrStatusTask.GetAwaiter().GetResult();
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>The Add to PATH command's latest run.</summary>
    private static Task ApplyTask(SettingsView view) => view.ViewModel.AddToPathCommand.ExecutionTask ?? Task.CompletedTask;

    /// <summary>Clicks a button the way the control does: its command runs only while it can.</summary>
    private static void Click(Button button)
    {
        var command = button.Command ?? throw new InvalidOperationException("The button has no command.");
        if (button.IsEffectivelyEnabled && command.CanExecute(button.CommandParameter))
            command.Execute(button.CommandParameter);
    }

    /// <summary>The Command line button, or null while its row is not shown.</summary>
    private static Button? FindCmdrButton(Control root)
    {
        var row = root.GetLogicalDescendants().OfType<Panel>()
            .FirstOrDefault(p => p.Children.OfType<TextBlock>().Any(t => t.Text == "Command line"));
        if (row is null)
            return null;

        var shown = row.IsVisible
            && row.GetLogicalAncestors().TakeWhile(a => !ReferenceEquals(a, root)).OfType<Control>().All(a => a.IsVisible);
        return shown ? row.GetLogicalDescendants().OfType<Button>().FirstOrDefault() : null;
    }

    private static string? RowLabel(Control row) => row switch
    {
        CheckBox { Content: string s } => s,
        Panel p when p.Children.Any(c => c.Classes.Contains(ViewKit.HelpHintClass))
            => RowLabel(p.Children.Single(c => !c.Classes.Contains(ViewKit.HelpHintClass))),
        Panel p => p.Children.OfType<TextBlock>().FirstOrDefault()?.Text,
        _ => null,
    };

    [AvaloniaFact]
    public void Row_sits_directly_after_Theme_in_General_and_button_lacks_primary_class()
    {
        var cmdr = new RecordingCmdrPathService
        {
            StatusToReturn = new CmdrPathStatus.NeedsUpdate(
                new PathChange(null, @"C:\NewCmdr", @"C:\NewCmdr", [], false),
                [],
                @"C:\NewCmdr",
                @"C:\Backups"),
        };
        var dialogs = new ScriptedDialogService();
        var sp = CreateServices(cmdr, dialogs);
        var view = new SettingsView(sp);

        var general = GeneralBody(view);
        var rowLabels = general.Children
            .Select(RowLabel)
            .Where(t => t is not null)
            .ToList();

        var themeIdx = rowLabels.IndexOf("Theme");
        var cmdrIdx = rowLabels.IndexOf("Command line");

        Assert.True(themeIdx >= 0, "Theme row missing.");
        Assert.Equal(themeIdx + 1, cmdrIdx);

        var button = FindCmdrButton(general);
        Assert.NotNull(button);
        Assert.False(button.Classes.Contains("primary"), "Button should not have primary class.");

        view.Dispose();
    }

    [AvaloniaFact]
    public void Unavailable_renders_no_Command_line_row()
    {
        var cmdr = new RecordingCmdrPathService
        {
            StatusToReturn = new CmdrPathStatus.Unavailable(),
        };
        var dialogs = new ScriptedDialogService();
        var sp = CreateServices(cmdr, dialogs);
        var view = new SettingsView(sp);

        var general = GeneralBody(view);
        var button = FindCmdrButton(general);
        Assert.Null(button);

        view.Dispose();
    }

    [AvaloniaFact]
    public void Current_renders_disabled_On_PATH_button()
    {
        var cmdr = new RecordingCmdrPathService
        {
            StatusToReturn = new CmdrPathStatus.Current(),
        };
        var dialogs = new ScriptedDialogService();
        var sp = CreateServices(cmdr, dialogs);
        var view = new SettingsView(sp);

        var general = GeneralBody(view);
        var button = FindCmdrButton(general);

        Assert.NotNull(button);
        Assert.Equal("On PATH", button.Content);
        Assert.False(button.IsEffectivelyEnabled);

        view.Dispose();
    }

    [AvaloniaFact]
    public void NeedsUpdate_renders_enabled_Add_to_PATH_button()
    {
        var cmdr = new RecordingCmdrPathService
        {
            StatusToReturn = new CmdrPathStatus.NeedsUpdate(
                new PathChange(null, @"C:\NewCmdr", @"C:\NewCmdr", [], false),
                [],
                @"C:\NewCmdr",
                @"C:\Backups"),
        };
        var dialogs = new ScriptedDialogService();
        var sp = CreateServices(cmdr, dialogs);
        var view = new SettingsView(sp);

        var general = GeneralBody(view);
        var button = FindCmdrButton(general);

        Assert.NotNull(button);
        Assert.Equal("Add to PATH", button.Content);
        Assert.True(button.IsEffectivelyEnabled);

        view.Dispose();
    }

    [AvaloniaFact]
    public async Task Clicking_calls_ConfirmDestructiveAsync_and_Cancel_skips_apply()
    {
        var preview = new PathChange(null, @"C:\NewCmdr", @"C:\NewCmdr", [@"C:\OldCmdr1", @"C:\OldCmdr2"], false);
        var cmdr = new RecordingCmdrPathService
        {
            StatusToReturn = new CmdrPathStatus.NeedsUpdate(preview, [@"C:\System\cmdr"], @"C:\NewCmdr", @"C:\Backups"),
        };
        var dialogs = new ScriptedDialogService { ConfirmResult = false };
        var sp = CreateServices(cmdr, dialogs);
        var view = new SettingsView(sp);

        var general = GeneralBody(view);
        var button = FindCmdrButton(general);
        Assert.NotNull(button);

        Click(button);
        await ApplyTask(view);
        EnsureCmdrStatusLoaded(view);

        Assert.Single(dialogs.DestructiveCalls);
        var call = dialogs.DestructiveCalls[0];
        Assert.Equal("Update PATH", call.Title);
        Assert.Equal("Add to PATH", call.ConfirmLabel);
        Assert.Contains(@"C:\NewCmdr", call.Effect);
        Assert.Contains(@"C:\OldCmdr1", call.Effect);
        Assert.Contains(@"C:\OldCmdr2", call.Effect);
        Assert.Contains(@"C:\Backups", call.Effect);
        Assert.Contains(@"C:\System\cmdr", call.Effect);

        Assert.Empty(dialogs.ConfirmCalls);
        Assert.Empty(cmdr.ApplyCalls);

        view.Dispose();
    }

    [AvaloniaFact]
    public async Task Confirm_calls_Apply_once_with_previewed_change()
    {
        var preview = new PathChange(null, @"C:\NewCmdr", @"C:\NewCmdr", [], false);
        var cmdr = new RecordingCmdrPathService
        {
            StatusToReturn = new CmdrPathStatus.NeedsUpdate(preview, [], @"C:\NewCmdr", @"C:\Backups"),
            ApplyResultToReturn = new CmdrPathApplyResult.Written(@"C:\Backups\backup.reg"),
        };
        var dialogs = new ScriptedDialogService { ConfirmResult = true };
        var sp = CreateServices(cmdr, dialogs);
        var view = new SettingsView(sp);

        var general = GeneralBody(view);
        var button = FindCmdrButton(general);
        Assert.NotNull(button);

        Click(button);
        await ApplyTask(view);
        EnsureCmdrStatusLoaded(view);

        Assert.Single(cmdr.ApplyCalls);
        Assert.Equal(preview, cmdr.ApplyCalls[0]);
        Assert.Empty(dialogs.ShowMessageCalls);

        view.Dispose();
    }

    [AvaloniaFact]
    public async Task Changed_result_shows_message()
    {
        var preview = new PathChange(null, @"C:\NewCmdr", @"C:\NewCmdr", [], false);
        var cmdr = new RecordingCmdrPathService
        {
            StatusToReturn = new CmdrPathStatus.NeedsUpdate(preview, [], @"C:\NewCmdr", @"C:\Backups"),
            ApplyResultToReturn = new CmdrPathApplyResult.Changed(),
        };
        var dialogs = new ScriptedDialogService { ConfirmResult = true };
        var sp = CreateServices(cmdr, dialogs);
        var view = new SettingsView(sp);

        var general = GeneralBody(view);
        var button = FindCmdrButton(general);
        Assert.NotNull(button);

        Click(button);
        await ApplyTask(view);
        EnsureCmdrStatusLoaded(view);

        Assert.Single(dialogs.ShowMessageCalls);
        Assert.Equal("Update PATH", dialogs.ShowMessageCalls[0].Title);
        Assert.Contains("PATH changed while the dialog was open", dialogs.ShowMessageCalls[0].Message);

        view.Dispose();
    }

    [AvaloniaFact]
    public async Task Failed_result_shows_message()
    {
        var preview = new PathChange(null, @"C:\NewCmdr", @"C:\NewCmdr", [], false);
        var cmdr = new RecordingCmdrPathService
        {
            StatusToReturn = new CmdrPathStatus.NeedsUpdate(preview, [], @"C:\NewCmdr", @"C:\Backups"),
            ApplyResultToReturn = new CmdrPathApplyResult.Failed("Disk full"),
        };
        var dialogs = new ScriptedDialogService { ConfirmResult = true };
        var sp = CreateServices(cmdr, dialogs);
        var view = new SettingsView(sp);

        var general = GeneralBody(view);
        var button = FindCmdrButton(general);
        Assert.NotNull(button);

        Click(button);
        await ApplyTask(view);
        EnsureCmdrStatusLoaded(view);

        Assert.Single(dialogs.ShowMessageCalls);
        Assert.Equal("Update PATH", dialogs.ShowMessageCalls[0].Title);
        Assert.Equal("Disk full", dialogs.ShowMessageCalls[0].Message);

        view.Dispose();
    }

    [WindowsAvaloniaFact]
    public async Task Effect_says_keeps_when_nothing_is_added_and_lists_duplicates_separately()
    {
        var preview = new PathChange(null, @"C:\NewCmdr", null, [@"C:\Old", @"C:\NewCmdr"], false);
        var cmdr = new RecordingCmdrPathService
        {
            StatusToReturn = new CmdrPathStatus.NeedsUpdate(preview, [], @"C:\NewCmdr", @"C:\Backups"),
        };
        var dialogs = new ScriptedDialogService { ConfirmResult = false };
        var sp = CreateServices(cmdr, dialogs);
        var view = new SettingsView(sp);

        var general = GeneralBody(view);
        var button = FindCmdrButton(general);
        Assert.NotNull(button);

        Click(button);
        await ApplyTask(view);
        EnsureCmdrStatusLoaded(view);

        Assert.Single(dialogs.DestructiveCalls);
        var call = dialogs.DestructiveCalls[0];
        Assert.DoesNotContain("Adds", call.Effect);
        Assert.Contains(@"Keeps C:\NewCmdr on your user PATH.", call.Effect);
        Assert.Contains("Removes from your user PATH:", call.Effect);
        Assert.Contains(@"C:\Old", call.Effect);
        Assert.Contains("Removes duplicates of that folder:", call.Effect);
        Assert.Contains(@"C:\NewCmdr", call.Effect);
        Assert.Contains(@"C:\Backups", call.Effect);
        Assert.DoesNotContain(AppContext.BaseDirectory, call.Effect);

        view.Dispose();
    }

    [AvaloniaFact]
    public async Task Button_is_disabled_while_apply_runs()
    {
        var preview = new PathChange(null, @"C:\NewCmdr", @"C:\NewCmdr", [], false);
        var applyGate = new TaskCompletionSource<bool>();
        var cmdr = new RecordingCmdrPathService
        {
            StatusToReturn = new CmdrPathStatus.NeedsUpdate(preview, [], @"C:\NewCmdr", @"C:\Backups"),
            ApplyGate = applyGate,
        };
        var dialogs = new ScriptedDialogService { ConfirmResult = true };
        var sp = CreateServices(cmdr, dialogs);
        var view = new SettingsView(sp);

        var general = GeneralBody(view);
        var button = FindCmdrButton(general);
        Assert.NotNull(button);
        Assert.True(button.IsEffectivelyEnabled);

        Click(button);
        Dispatcher.UIThread.RunJobs();

        var buttonDuringApply = FindCmdrButton(GeneralBody(view));
        Assert.NotNull(buttonDuringApply);
        Assert.False(buttonDuringApply.IsEffectivelyEnabled);

        applyGate.SetResult(true);
        await ApplyTask(view);
        EnsureCmdrStatusLoaded(view);

        view.Dispose();
    }

    [AvaloniaFact]
    public async Task Button_stays_disabled_after_a_write_until_the_new_status_arrives()
    {
        var preview = new PathChange(null, @"C:\NewCmdr", @"C:\NewCmdr", [], false);
        var statusGate = new TaskCompletionSource<bool>();
        var cmdr = new RecordingCmdrPathService
        {
            StatusToReturn = new CmdrPathStatus.NeedsUpdate(preview, [], @"C:\NewCmdr", @"C:\Backups"),
            SubsequentStatusGate = statusGate,
        };
        var dialogs = new ScriptedDialogService { ConfirmResult = true };
        var sp = CreateServices(cmdr, dialogs);
        var view = new SettingsView(sp);

        var general = GeneralBody(view);
        var button = FindCmdrButton(general);
        Assert.NotNull(button);

        Click(button);
        await ApplyTask(view);
        Dispatcher.UIThread.RunJobs();

        var buttonDuringWait = FindCmdrButton(GeneralBodyNow(view));
        Assert.NotNull(buttonDuringWait);
        Assert.Equal("Add to PATH", buttonDuringWait.Content);
        Assert.False(buttonDuringWait.IsEffectivelyEnabled);

        cmdr.StatusToReturn = new CmdrPathStatus.Current();
        statusGate.SetResult(true);
        EnsureCmdrStatusLoaded(view);

        var currentButton = FindCmdrButton(GeneralBodyNow(view));
        Assert.NotNull(currentButton);
        Assert.Equal("On PATH", currentButton.Content);
        Assert.False(currentButton.IsEffectivelyEnabled);

        Assert.Single(dialogs.DestructiveCalls);
        Assert.Single(cmdr.ApplyCalls);

        view.Dispose();
    }

    [AvaloniaFact]
    public void Throwing_status_hides_the_row()
    {
        var cmdr = new RecordingCmdrPathService
        {
            ThrowFromGetStatus = true,
        };
        var dialogs = new ScriptedDialogService();
        var sp = CreateServices(cmdr, dialogs);
        var view = new SettingsView(sp);

        var general = GeneralBody(view);
        var button = FindCmdrButton(general);
        Assert.Null(button);

        view.Dispose();
    }

    [AvaloniaFact]
    public async Task Throwing_apply_shows_a_message_and_refreshes()
    {
        var preview = new PathChange(null, @"C:\NewCmdr", @"C:\NewCmdr", [], false);
        var cmdr = new RecordingCmdrPathService
        {
            StatusToReturn = new CmdrPathStatus.NeedsUpdate(preview, [], @"C:\NewCmdr", @"C:\Backups"),
            ThrowFromApply = true,
        };
        var dialogs = new ScriptedDialogService { ConfirmResult = true };
        var sp = CreateServices(cmdr, dialogs);
        var view = new SettingsView(sp);

        var general = GeneralBody(view);
        var button = FindCmdrButton(general);
        Assert.NotNull(button);

        Click(button);
        await ApplyTask(view);
        EnsureCmdrStatusLoaded(view);

        Assert.Single(dialogs.ShowMessageCalls);
        Assert.Equal("Update PATH", dialogs.ShowMessageCalls[0].Title);
        Assert.Equal("Apply error", dialogs.ShowMessageCalls[0].Message);
        Assert.Equal(2, cmdr.StatusCallCount);

        view.Dispose();
    }
}
