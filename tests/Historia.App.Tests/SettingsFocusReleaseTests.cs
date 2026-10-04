namespace HistoriaApp.Tests;

/// <summary>A settings textbox commits on <c>LostFocus</c>, but clicking a non-focusable
/// label or blank space never moves focus on its own — <see cref="SettingsView"/>'s root now
/// clears focus on any press that doesn't land on a real input, letting the previously focused
/// textbox's own LostFocus handler run.</summary>
[Trait("tier", "ui")]
public sealed class SettingsFocusReleaseTests
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
        return sc.BuildServiceProvider();
    }

    /// <summary>The terminal shell-path TextBox lives in the "Terminal" section, which starts
    /// collapsed (<c>_terminalExpanded = false</c>) — its content is IsVisible=false and neither
    /// focusable nor hit-testable until expanded. Flip the private field and re-run the private
    /// Rebuild() directly, the same reflection approach <c>SettingsNumericCommitTests</c> uses
    /// for other private members, rather than simulating a click on the section header.</summary>
    private static void ExpandTerminalSection(SettingsView view)
    {
        var field = typeof(SettingsView).GetField("_terminalExpanded", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("SettingsView._terminalExpanded not found — renamed?");
        field.SetValue(view, true);
        var rebuild = typeof(SettingsView).GetMethod("Rebuild", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("SettingsView.Rebuild not found — renamed?");
        rebuild.Invoke(view, null);
    }

    private static (SettingsView View, Window Window, TextBox ShellPath) CreateView()
    {
        var sp = CreateServices();
        var view = new SettingsView(sp);
        ExpandTerminalSection(view);
        // Tall enough that every section, including the collapsed-then-expanded Terminal
        // section near the bottom, lands inside the viewport without needing to scroll —
        // clicking a point outside the ScrollViewer's viewport does not reach the control.
        var window = new Window { Width = 900, Height = 2400, Content = view };
        window.Show();
        window.Activate();
        Dispatcher.UIThread.RunJobs();

        // Picked by watermark, not position — a ComboBox's own (hidden) template part is also a
        // TextBox and sorts earlier in the visual tree than the Terminal section's fields.
        var shellPath = view.GetVisualDescendants().OfType<TextBox>()
            .FirstOrDefault(t => t.Watermark == "Platform default (pwsh / zsh / bash)")
            ?? throw new InvalidOperationException("Terminal shell path TextBox not found.");
        return (view, window, shellPath);
    }

    /// <summary>A real pointer click, not a programmatic <c>.Focus()</c> call — this exercises
    /// the same focus path a user's click takes, which is what actually establishes headless
    /// keyboard focus reliably here.</summary>
    private static void ClickOn(Window window, Control target)
    {
        var point = target.TranslatePoint(new Point(2, 2), window) ?? new Point(10, 10);
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void Pressing_a_non_interactive_TextBlock_clears_focus_and_raises_LostFocus()
    {
        var (view, window, box) = CreateView();
        ClickOn(window, box);
        Assert.True(box.IsFocused);

        var lostFocusFired = false;
        box.LostFocus += (_, _) => lostFocusFired = true;

        var label = view.GetVisualDescendants().OfType<TextBlock>()
            .FirstOrDefault(t => !ReferenceEquals(t, box))
            ?? throw new InvalidOperationException("No TextBlock found to press.");
        ClickOn(window, label);

        Assert.False(box.IsFocused);
        Assert.True(lostFocusFired);

        window.Close();
    }

    [AvaloniaFact]
    public void Pressing_another_TextBox_moves_focus_normally_and_does_not_swallow_the_click()
    {
        var (view, window, box) = CreateView();
        var otherBox = view.GetVisualDescendants().OfType<TextBox>()
            .FirstOrDefault(t => t.Watermark == "Separated by spaces") // the shell-args field
            ?? throw new InvalidOperationException("Terminal shell args TextBox not found.");

        ClickOn(window, box);

        ClickOn(window, otherBox);

        Assert.True(otherBox.IsFocused);

        window.Close();
    }

    [AvaloniaFact]
    public void Pressing_a_button_still_invokes_it()
    {
        var (view, window, box) = CreateView();
        ClickOn(window, box);

        // "Add rule" (Issue autolinks) is a plain TextButton with no side effect beyond a
        // config update — safe to click, unlike "Browse…" which opens a real file picker.
        var button = view.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => (b.Content as string) == "Add rule")
            ?? throw new InvalidOperationException("'Add rule' button not found.");
        var clicked = false;
        button.Click += (_, _) => clicked = true;

        var point = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window) ?? new Point(10, 10);
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.True(clicked);

        window.Close();
    }
}
