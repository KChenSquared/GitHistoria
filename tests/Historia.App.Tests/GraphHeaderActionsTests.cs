namespace HistoriaApp.Tests;

/// <summary>The Graph column header's two right-aligned action icons — the cog (slot 0,
/// navigates to repo-settings) and the reset (slot 1, returns the graph to the left-most
/// horizontal position).</summary>
[Trait("tier", "ui")]
public sealed class GraphHeaderActionsTests
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

        public TestRepoFixture()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "historia-graphheader-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
            RunGit(Path, "init");
            RunGit(Path, "config", "user.name", "Test");
            RunGit(Path, "config", "user.email", "test@example.com");

            var file = System.IO.Path.Combine(Path, "f.txt");
            File.WriteAllText(file, "init\n");
            RunGit(Path, "add", ".");
            RunGit(Path, "commit", "-m", "c1", "--no-gpg-sign");
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
        RepositoryContext Context, Window Window, INavigationBus Nav) : IDisposable
    {
        public void Dispose()
        {
            try { Window.Close(); } catch { }
            try { Context.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
            try { Sp.Dispose(); } catch { }
            try { Fixture.Dispose(); } catch { }
        }
    }

    private static Setup CreateGraph(double width = 1000, double height = 700)
    {
        var fixture = new TestRepoFixture();
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

        for (var i = 0; i < 100 && graph.RowCountForTesting == 0; i++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }

        return new Setup(graph, sp, fixture, ctx, window, sp.GetRequiredService<INavigationBus>());
    }

    private static HeaderIconRect IconRect(CommitGraphControl graph, int slot)
        => GraphColumnInteraction.HeaderIconRectFor(
            graph.ResolvedColumnsForTesting, GraphColumnKind.Graph, graph.HScrollOffsetForTesting,
            CommitGraphControl.HeaderHeight, 14, 6, slot)!.Value;

    private static Point Center(HeaderIconRect r) => new(r.X + r.Width / 2, r.Y + r.Height / 2);

    [AvaloniaFact]
    public void CogClickNavigatesToRepoSettings()
    {
        using var s = CreateGraph();
        var rect = IconRect(s.Graph, slot: 0);
        var routes = new List<string>();
        s.Nav.NavigationRequested += r => routes.Add(r);

        s.Window.MouseDown(Center(rect), MouseButton.Left);
        s.Window.MouseUp(Center(rect), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.Contains(routes, r => r.StartsWith("repo-settings", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public void ResetClickReturnsScrollToTheLeftMostPositionWithoutTouchingVerticalOffset()
    {
        using var s = CreateGraph();
        s.Graph.SetScrollOffsetsForTesting(hScrollOffset: 50, graphPanOffset: 30);
        var verticalBefore = s.Graph.ScrollOffset;
        var widthsBefore = s.Graph.ColumnsForTesting.Select(c => c.Width).ToList();
        var rect = IconRect(s.Graph, slot: 1);

        s.Window.MouseDown(Center(rect), MouseButton.Left);
        s.Window.MouseUp(Center(rect), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(0, s.Graph.HScrollOffsetForTesting);
        Assert.Equal(0, s.Graph.GraphPanOffsetForTesting);
        Assert.Equal(verticalBefore, s.Graph.ScrollOffset);
        Assert.Equal(widthsBefore, s.Graph.ColumnsForTesting.Select(c => c.Width));
    }

    [AvaloniaFact]
    public void HeaderIconClickDoesNotBeginAColumnResize()
    {
        using var s = CreateGraph();
        var cog = IconRect(s.Graph, slot: 0);

        s.Window.MouseDown(Center(cog), MouseButton.Left);
        s.Window.MouseMove(new Point(Center(cog).X + 40, Center(cog).Y));
        s.Window.MouseUp(new Point(Center(cog).X + 40, Center(cog).Y), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(GraphColumnDefaults.All.First(c => c.Kind == GraphColumnKind.Graph).Width,
            s.Graph.ColumnsForTesting.First(c => c.Kind == GraphColumnKind.Graph).Width);
    }
}
