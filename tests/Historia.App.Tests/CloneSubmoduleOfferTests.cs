namespace HistoriaApp.Tests;

/// <summary>
/// Offer submodule init for the repo that was actually cloned, matching
/// on targetDirectory regardless of which tab is currently focused.
/// </summary>
public sealed class CloneSubmoduleOfferTests
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

    internal static ServiceProvider CreateServices()
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

    internal static string CreateTestRepo(string name)
    {
        var path = TestScratch.NewDirectory($"historia-clonetest-{name}-");
        TestGit.Run(path, ["init"], testIdentity: false);

        File.WriteAllText(Path.Combine(path, "init.txt"), "hello\n");
        TestGit.Run(path, ["add", "init.txt"], testIdentity: false);
        TestGit.Run(path, ["config", "user.name", "Test"], testIdentity: false);
        TestGit.Run(path, ["config", "user.email", "test@example.com"], testIdentity: false);
        TestGit.Run(path, ["commit", "-m", "init", "--no-gpg-sign"], testIdentity: false);

        return path;
    }

    [Fact]
    public void PathsEqual_handles_trailing_separators()
    {
        Assert.True(HomeViewModel.PathsEqual(@"C:\src\repo\", @"C:\src\repo"));
        Assert.True(HomeViewModel.PathsEqual(@"C:/src/repo/", @"C:/src/repo"));
    }

    [Fact]
    public void PathsEqual_handles_mixed_separators()
    {
        Assert.True(HomeViewModel.PathsEqual(@"C:\src\repo", "C:/src/repo"));
        Assert.True(HomeViewModel.PathsEqual(@"C:\src/repo\sub", "C:/src/repo/sub"));
    }

    [Fact]
    public void PathsEqual_handles_case_insensitivity_on_windows()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.True(HomeViewModel.PathsEqual(@"C:\Src\Repo", @"C:\src\repo"));
        }
    }

    [Fact]
    public void PathsEqual_rejects_sibling_paths()
    {
        Assert.False(HomeViewModel.PathsEqual(@"C:\src\repo2", @"C:\src\repo"));
        Assert.False(HomeViewModel.PathsEqual("C:/src/repo2", "C:/src/repo"));
    }

    [Fact]
    public async Task FindClonedRepository_ignores_the_focused_tab()
    {
        var repoPath1 = CreateTestRepo("r1");
        var repoPath2 = CreateTestRepo("r2");
        await using var sp = CreateServices();
        var ws = sp.GetRequiredService<IWorkspaceService>();

        try
        {
            var ctx1 = await ws.OpenAsync(repoPath1);
            var ctx2 = await ws.OpenAsync(repoPath2);

            // Focus tab 1; search for tab 2
            ws.Focus(ctx1);
            Assert.Same(ctx1, ws.Focused);

            var found = HomeViewModel.FindClonedRepository(ws.OpenRepositories, repoPath2);
            Assert.Same(ctx2, found);

            var notFound = HomeViewModel.FindClonedRepository(ws.OpenRepositories, TestScratch.NewPath("nonexistent-target-"));
            Assert.Null(notFound);

            await ws.CloseAsync(ctx1);
            await ws.CloseAsync(ctx2);
        }
        finally
        {
            TestScratch.Delete(repoPath1);
            TestScratch.Delete(repoPath2);
        }
    }
}
