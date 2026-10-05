namespace HistoriaApp.Tests;

/// <summary>Every configured remote gets a sidebar row with the remote context menu, even with no
/// remote-tracking branches; the old "Rename remote…" item is gone; stale tracking groups render
/// without a menu.</summary>
[Trait("tier", "ui")]
public sealed class RefNavigatorRemoteRowTests
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

    private sealed class RepoFixture : IDisposable
    {
        public string Path { get; }

        public RepoFixture()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "historia-remoterow-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
            RunGit("init");
            RunGit("config", "user.name", "Test");
            RunGit("config", "user.email", "test@example.com");
            File.WriteAllText(System.IO.Path.Combine(Path, "f.txt"), "init\n");
            RunGit("add", ".");
            RunGit("commit", "-m", "c1", "--no-gpg-sign");

            RunGit("remote", "add", "origin", "https://example.com/repo.git");
            RunGit("update-ref", "refs/remotes/origin/main", "HEAD");
            // Configured but never fetched: no remote-tracking branches.
            RunGit("remote", "add", "upstream", "https://example.com/upstream.git");
            // Tracking refs whose remote is not configured.
            RunGit("update-ref", "refs/remotes/ghost/main", "HEAD");
        }

        private void RunGit(params string[] args)
        {
            var psi = new System.Diagnostics.ProcessStartInfo("git")
            {
                WorkingDirectory = Path,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
            using var p = System.Diagnostics.Process.Start(psi)!;
            p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit();
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    foreach (var f in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories))
                    {
                        var attr = File.GetAttributes(f);
                        if ((attr & FileAttributes.ReadOnly) != 0)
                            File.SetAttributes(f, attr & ~FileAttributes.ReadOnly);
                    }
                    Directory.Delete(Path, true);
                }
            }
            catch { }
        }
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

    private static (RefNavigatorView View, ServiceProvider Sp, RepoFixture Fixture, RepositoryContext Context, Window Window) CreateView()
    {
        var fixture = new RepoFixture();
        var sp = CreateServices();
        var ws = sp.GetRequiredService<IWorkspaceService>();
        var ctx = ws.OpenAsync(fixture.Path).GetAwaiter().GetResult();
        ctx.MarkFirstGraphPaintDone();
        ctx.State.RefreshRefsAsync().GetAwaiter().GetResult();
        ws.Focus(ctx);
        var view = new RefNavigatorView(sp);
        var window = new Window { Width = 300, Height = 800, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (view, sp, fixture, ctx, window);
    }

    private static void Cleanup((RefNavigatorView View, ServiceProvider Sp, RepoFixture Fixture, RepositoryContext Context, Window Window) s)
    {
        try { s.Window.Close(); } catch { }
        try { s.View.Dispose(); } catch { }
        try { s.Context.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
        try { s.Sp.Dispose(); } catch { }
        try { s.Fixture.Dispose(); } catch { }
    }

    private static Border? FolderRowNamed(RefNavigatorView view, string name) =>
        view.GetVisualDescendants().OfType<Border>()
            .FirstOrDefault(b => b.Child is Grid { ColumnDefinitions.Count: 2 }
                && ToolTip.GetTip(b)?.ToString() == name
                && b.Height == 24);

    private static List<string> MenuHeaders(Border row) =>
        (row.ContextMenu!.ItemsSource as IEnumerable<Control>)!
            .OfType<MenuItem>().Select(m => m.Header!.ToString()!).ToList();

    [AvaloniaFact]
    public void Remote_without_tracking_branches_still_gets_a_row_and_menu()
    {
        var setup = CreateView();
        try
        {
            Assert.Equal(2, setup.Context.State.Remotes.Count);

            var row = FolderRowNamed(setup.View, "upstream");
            Assert.NotNull(row);
            Assert.NotNull(row!.ContextMenu);
            Assert.Equal(
                ["Open In Browser", "Fetch", "Prune", "Edit…", "Set Push URL…", "Delete…", "Copy URL"],
                MenuHeaders(row));

            var origin = FolderRowNamed(setup.View, "origin");
            Assert.NotNull(origin?.ContextMenu);
        }
        finally
        {
            Cleanup(setup);
        }
    }

    [AvaloniaFact]
    public void Rename_remote_item_is_gone()
    {
        var setup = CreateView();
        try
        {
            foreach (var name in new[] { "origin", "upstream" })
                Assert.DoesNotContain("Rename remote…", MenuHeaders(FolderRowNamed(setup.View, name)!));
        }
        finally
        {
            Cleanup(setup);
        }
    }

    [AvaloniaFact]
    public void Stale_tracking_group_renders_without_a_remote_menu()
    {
        var setup = CreateView();
        try
        {
            var row = FolderRowNamed(setup.View, "ghost");
            Assert.NotNull(row);
            Assert.Null(row!.ContextMenu);
        }
        finally
        {
            Cleanup(setup);
        }
    }
}
