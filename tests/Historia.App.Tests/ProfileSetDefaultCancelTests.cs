namespace HistoriaApp.Tests;

/// <summary>
/// Cancelling the "Set as default" chooser must leave the default profile
/// untouched and publish nothing — the user declined, they didn't change anything.
/// </summary>
[Trait("tier", "ui")]
public sealed class ProfileSetDefaultCancelTests
{
    private sealed class CapturingNotificationService : INotificationService
    {
        public List<Notification> Published { get; } = new();
        public IReadOnlyList<Notification> Current => Published;

        public Notification Publish(
            NotificationSeverity severity,
            string message,
            string? details = null,
            IReadOnlyList<NotificationAction>? actions = null)
        {
            var n = new Notification
            {
                Id = Guid.NewGuid(),
                Severity = severity,
                Message = message,
                Details = details,
                Actions = actions ?? [],
                CreatedAt = DateTimeOffset.Now,
            };
            Published.Add(n);
            Changed?.Invoke(this, new NotificationsChangedEventArgs(Published));
            return n;
        }

        public void Dismiss(Guid id) { }
        public event EventHandler<NotificationsChangedEventArgs>? Changed;
    }

    /// <summary>Scripts <see cref="ChooseAsync"/> to a fixed choice; every other member throws
    /// since this flow never reaches them.</summary>
    private sealed class FakeDialogService : IDialogService
    {
        public int ChooseCalls;
        public int ChooseResult;

        public Task<int> ChooseAsync(string title, string message, IReadOnlyList<string> options,
            int defaultIndex = 0, int destructiveIndex = -1, DialogIcon icon = DialogIcon.None)
        {
            ChooseCalls++;
            return Task.FromResult(ChooseResult);
        }

        public Task<string?> PickFolderAsync(string title, string? initialDirectory = null) => throw new NotSupportedException();
        public Task<string?> PickFileAsync(string title, string filter, string? initialDirectory = null) => throw new NotSupportedException();
        public Task<bool> ConfirmAsync(string title, string message, bool destructive = false,
            string confirmLabel = "OK", bool defaultToCancel = false, DialogIcon icon = DialogIcon.None) => throw new NotSupportedException();
        public Task<bool> ConfirmDestructiveAsync(string title, string effect, string confirmLabel) => throw new NotSupportedException();
        public Task ShowMessageAsync(string title, string message) => throw new NotSupportedException();
        public Task<string?> PromptAsync(string title, string message, string? defaultValue = null,
            string confirmLabel = "OK", bool primaryConfirm = false) => throw new NotSupportedException();
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

    /// <summary>Redirects <see cref="AppPaths"/> to a throwaway directory for the life of a test —
    /// must be set before the service provider (and its eagerly-loading <c>ProfileService</c>) is
    /// built.</summary>
    private sealed class IsolatedProfilesDir : IDisposable
    {
        public string Path { get; } = TestScratch.NewDirectory("historia-profile-default-");

        public IsolatedProfilesDir()
        {
            AppPaths.OverrideForTesting(Path);
        }

        public void Dispose()
        {
            AppPaths.OverrideForTesting(null);
            TestScratch.Delete(Path);
        }
    }

    private static ServiceProvider CreateServices(INotificationService notify, IDialogService dialogs)
    {
        var sc = new ServiceCollection();
        var settings = SettingsService.Load();
        sc.AddSingleton<ISettingsService>(settings);
        sc.AddSingleton<SettingsService>(settings);
        sc.AddSingleton<IUiDispatcher, Historia.Platform.UiDispatcher>();
        sc.AddSingleton(dialogs);
        sc.AddSingleton<IScrollDirectionProbe, Historia.Platform.ScrollDirectionProbe>();
        sc.AddSingleton<IRepoActionBus, RepoActionBus>();
        sc.AddSingleton<IRepoNetworkActionRunner, RepoNetworkActionRunner>();
        sc.AddSingleton<IAutoFetchHost, AutoFetchHost>();
        sc.AddSingleton<IAutoFetchService, AutoFetchService>();
        sc.AddSingleton<INavigationBus, NavigationBus>();
        sc.AddSingleton<IKeyboardCommandRouter, KeyboardCommandRouter>();
        sc.AddSingleton(notify);
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

    [AvaloniaFact]
    public async Task Cancel_leaves_default_unchanged_and_silent()
    {
        using var dir = new IsolatedProfilesDir();
        var notify = new CapturingNotificationService();
        var dialogs = new FakeDialogService { ChooseResult = -1 };
        await using var sp = CreateServices(notify, dialogs);

        var profiles = sp.GetRequiredService<IProfileService>();
        var created = await profiles.CreateProfileAsync(new GitProfileCreateRequest
        {
            DisplayName = "Work",
            GitUserName = "Work User",
            GitUserEmail = "work@example.com",
        });
        var defaultBefore = profiles.GetDefaultProfile()?.Id;

        var view = new ProfilesView(sp);
        try
        {
            await view.SetDefaultAsync(created);

            Assert.Equal(1, dialogs.ChooseCalls);
            Assert.Equal(defaultBefore, profiles.GetDefaultProfile()?.Id);
            Assert.NotEqual(created.Id, profiles.GetDefaultProfile()?.Id);
            Assert.Empty(notify.Published);
        }
        finally
        {
            view.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task Choosing_follow_default_still_sets_it()
    {
        using var dir = new IsolatedProfilesDir();
        var notify = new CapturingNotificationService();
        var dialogs = new FakeDialogService { ChooseResult = 0 };
        await using var sp = CreateServices(notify, dialogs);

        var profiles = sp.GetRequiredService<IProfileService>();
        var created = await profiles.CreateProfileAsync(new GitProfileCreateRequest
        {
            DisplayName = "Work",
            GitUserName = "Work User",
            GitUserEmail = "work@example.com",
        });

        var view = new ProfilesView(sp);
        try
        {
            await view.SetDefaultAsync(created);

            Assert.Equal(1, dialogs.ChooseCalls);
            Assert.Equal(created.Id, profiles.GetDefaultProfile()?.Id);
            var note = Assert.Single(notify.Published);
            Assert.Equal(NotificationSeverity.Info, note.Severity);
            Assert.Contains("is now the default profile", note.Message);
        }
        finally
        {
            view.Dispose();
        }
    }
}
