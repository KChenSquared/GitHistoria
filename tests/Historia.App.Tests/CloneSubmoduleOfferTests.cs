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

    private static string CreateTestRepo(string name)
    {
        var path = Path.Combine(Path.GetTempPath(), $"historia-clonetest-{name}-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        var psi = new System.Diagnostics.ProcessStartInfo("git")
        {
            WorkingDirectory = path,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("init");
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        using var p = System.Diagnostics.Process.Start(psi)!;
        p.WaitForExit();

        File.WriteAllText(Path.Combine(path, "init.txt"), "hello\n");
        var psiAdd = new System.Diagnostics.ProcessStartInfo("git")
        {
            WorkingDirectory = path,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psiAdd.ArgumentList.Add("add");
        psiAdd.ArgumentList.Add("init.txt");
        using var pAdd = System.Diagnostics.Process.Start(psiAdd)!;
        pAdd.WaitForExit();

        var psiCommit = new System.Diagnostics.ProcessStartInfo("git")
        {
            WorkingDirectory = path,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psiCommit.ArgumentList.Add("config");
        psiCommit.ArgumentList.Add("user.name");
        psiCommit.ArgumentList.Add("Test");
        using (var pc = System.Diagnostics.Process.Start(psiCommit)!) pc.WaitForExit();

        var psiEmail = new System.Diagnostics.ProcessStartInfo("git")
        {
            WorkingDirectory = path,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psiEmail.ArgumentList.Add("config");
        psiEmail.ArgumentList.Add("user.email");
        psiEmail.ArgumentList.Add("test@example.com");
        using (var pe = System.Diagnostics.Process.Start(psiEmail)!) pe.WaitForExit();

        var psiC = new System.Diagnostics.ProcessStartInfo("git")
        {
            WorkingDirectory = path,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psiC.ArgumentList.Add("commit");
        psiC.ArgumentList.Add("-m");
        psiC.ArgumentList.Add("init");
        psiC.ArgumentList.Add("--no-gpg-sign");
        using (var pCommit = System.Diagnostics.Process.Start(psiC)!) pCommit.WaitForExit();

        return path;
    }

    [Fact]
    public void PathsEqual_handles_trailing_separators()
    {
        Assert.True(HomeView.PathsEqual(@"C:\src\repo\", @"C:\src\repo"));
        Assert.True(HomeView.PathsEqual(@"C:/src/repo/", @"C:/src/repo"));
    }

    [Fact]
    public void PathsEqual_handles_mixed_separators()
    {
        Assert.True(HomeView.PathsEqual(@"C:\src\repo", "C:/src/repo"));
        Assert.True(HomeView.PathsEqual(@"C:\src/repo\sub", "C:/src/repo/sub"));
    }

    [Fact]
    public void PathsEqual_handles_case_insensitivity_on_windows()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.True(HomeView.PathsEqual(@"C:\Src\Repo", @"C:\src\repo"));
        }
    }

    [Fact]
    public void PathsEqual_rejects_sibling_paths()
    {
        Assert.False(HomeView.PathsEqual(@"C:\src\repo2", @"C:\src\repo"));
        Assert.False(HomeView.PathsEqual("C:/src/repo2", "C:/src/repo"));
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

            var found = HomeView.FindClonedRepository(ws.OpenRepositories, repoPath2);
            Assert.Same(ctx2, found);

            var notFound = HomeView.FindClonedRepository(ws.OpenRepositories, Path.Combine(Path.GetTempPath(), "nonexistent-target"));
            Assert.Null(notFound);

            await ws.CloseAsync(ctx1);
            await ws.CloseAsync(ctx2);
        }
        finally
        {
            try { if (Directory.Exists(repoPath1)) Directory.Delete(repoPath1, true); } catch { }
            try { if (Directory.Exists(repoPath2)) Directory.Delete(repoPath2, true); } catch { }
        }
    }
}
