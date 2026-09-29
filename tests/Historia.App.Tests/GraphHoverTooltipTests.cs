namespace HistoriaApp.Tests;

[Trait("tier", "ui")]
public sealed class GraphHoverTooltipTests
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

    private sealed class TestRepoFixture : IDisposable
    {
        public string Path { get; }

        public TestRepoFixture(int commitCount = 1)
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "historia-hover-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
            RunGit(Path, "init");
            RunGit(Path, "config", "user.name", "Test");
            RunGit(Path, "config", "user.email", "test@example.com");

            var file = System.IO.Path.Combine(Path, "f.txt");
            for (var i = 1; i <= commitCount; i++)
            {
                File.AppendAllText(file, $"line {i}\n");
                RunGit(Path, "add", ".");
                RunGit(Path, "commit", "-m", $"commit {i}\n\nDetailed body for commit {i}", "--no-gpg-sign");
            }
        }

        private static void RunGit(string dir, params string[] args)
        {
            var psi = new System.Diagnostics.ProcessStartInfo("git")
            {
                WorkingDirectory = dir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
            psi.Environment["GIT_AUTHOR_NAME"] = "Test";
            psi.Environment["GIT_AUTHOR_EMAIL"] = "test@example.com";
            psi.Environment["GIT_COMMITTER_NAME"] = "Test";
            psi.Environment["GIT_COMMITTER_EMAIL"] = "test@example.com";
            using var p = System.Diagnostics.Process.Start(psi)!;
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

    private sealed record Setup(
        CommitGraphControl Graph, ServiceProvider Sp, TestRepoFixture Fixture,
        RepositoryContext Context, Window Window) : IDisposable
    {
        public void Dispose()
        {
            try { Window.Close(); } catch { }
            try { Context.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
            try { Sp.Dispose(); } catch { }
            try { Fixture.Dispose(); } catch { }
        }
    }

    private static Setup CreateGraph(int commitCount = 1, double width = 1000, double height = 700)
    {
        var fixture = new TestRepoFixture(commitCount);
        var sp = CreateServices();
        var ws = sp.GetRequiredService<IWorkspaceService>();
        var ctx = ws.OpenAsync(fixture.Path).GetAwaiter().GetResult();
        ctx.MarkFirstGraphPaintDone();
        ctx.MarkFirstFullLoadDone();
        ctx.State.RefreshRefsAsync().GetAwaiter().GetResult();

        var graph = new CommitGraphControl();
        graph.Initialize(sp);
        var window = new Window { Width = width, Height = height, Content = graph };
        window.Show();
        ws.Focus(ctx);
        Dispatcher.UIThread.RunJobs();

        for (var i = 0; i < 100 && graph.RowCountForTesting < commitCount; i++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }

        return new Setup(graph, sp, fixture, ctx, window);
    }

    private static Point MessageCellPoint(CommitGraphControl graph, int row)
    {
        var messageCol = graph.ResolvedColumnsForTesting.First(c => c.Kind == GraphColumnKind.Message);
        var x = messageCol.Left - graph.HScrollOffsetForTesting + 20;
        var y = CommitGraphControl.HeaderHeight + row * graph.RowHeightValue + graph.RowHeightValue / 2;
        return new Point(x, y);
    }

    [AvaloniaFact]
    public void Hovering_a_message_cell_opens_no_tooltip_before_the_dwell()
    {
        using var s = CreateGraph();
        var p0 = MessageCellPoint(s.Graph, 0);

        Avalonia.Headless.HeadlessWindowExtensions.MouseMove(s.Window, p0);
        Dispatcher.UIThread.RunJobs();

        Assert.False(ToolTip.GetIsOpen(s.Graph));
        Assert.Null(s.Graph.ShownHoverTargetForTesting);
        Assert.NotNull(s.Graph.PendingHoverTargetForTesting);
        Assert.Equal(HoverTargetKind.Message, s.Graph.PendingHoverTargetForTesting!.Value.Kind);
        Assert.True(s.Graph.HoverDwellTimerRunningForTesting);
    }

    [AvaloniaFact]
    public void Hovering_a_message_cell_opens_its_tooltip_after_the_dwell()
    {
        using var s = CreateGraph();
        var p0 = MessageCellPoint(s.Graph, 0);

        Avalonia.Headless.HeadlessWindowExtensions.MouseMove(s.Window, p0);
        Dispatcher.UIThread.RunJobs();

        Assert.False(ToolTip.GetIsOpen(s.Graph));

        s.Graph.FireHoverDwellTimerForTesting();
        Dispatcher.UIThread.RunJobs();

        Assert.True(ToolTip.GetIsOpen(s.Graph));
        Assert.NotNull(s.Graph.ShownHoverTargetForTesting);
        Assert.Equal(HoverTargetKind.Message, s.Graph.ShownHoverTargetForTesting!.Value.Kind);
    }

    [AvaloniaFact]
    public void Sweeping_across_message_cells_starts_no_body_fetch()
    {
        using var s = CreateGraph(commitCount: 5);

        for (var r = 0; r < 5; r++)
        {
            var p = MessageCellPoint(s.Graph, r);
            Avalonia.Headless.HeadlessWindowExtensions.MouseMove(s.Window, p);
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(30); // well under 1 s dwell
        }

        // Before the 1s dwell elapses on the final position, no message fetch should have started or cached
        Assert.False(s.Graph.MsgFetchInFlightForTesting);
        Assert.Equal(0, s.Graph.MsgBodyCacheCountForTesting);
        Assert.False(ToolTip.GetIsOpen(s.Graph));
    }
}
