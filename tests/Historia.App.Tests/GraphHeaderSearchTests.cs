using Avalonia.Headless;

namespace HistoriaApp.Tests;

[Trait("tier", "ui")]
public sealed class GraphHeaderSearchTests
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

        public TestRepoFixture(int extraBranches = 0)
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "historia-graphsearch-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
            RunGit(Path, "init");
            RunGit(Path, "config", "user.name", "Test");
            RunGit(Path, "config", "user.email", "test@example.com");

            var file = System.IO.Path.Combine(Path, "f.txt");
            File.WriteAllText(file, "init\n");
            RunGit(Path, "add", ".");
            RunGit(Path, "commit", "-m", "c1", "--no-gpg-sign");
            File.WriteAllText(file, "second\n");
            RunGit(Path, "add", ".");
            RunGit(Path, "commit", "-m", "c2", "--no-gpg-sign");

            RunGit(Path, "branch", "feature/auth-rework");
            RunGit(Path, "branch", "fix/authz-header");
            RunGit(Path, "tag", "v1.0.0");

            for (var i = 0; i < extraBranches; i++)
                RunGit(Path, "branch", $"extra/branch-{i:D2}");
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
        sc.AddSingleton<LibGit2Service>();
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

    private static Setup CreateGraph(int extraBranches = 0, double width = 1000, double height = 700)
    {
        var fixture = new TestRepoFixture(extraBranches);
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

        return new Setup(graph, sp, fixture, ctx, window);
    }

    private static HeaderIconRect IconRect(CommitGraphControl graph, GraphColumnKind kind)
        => GraphColumnInteraction.HeaderIconRectFor(
            graph.ResolvedColumnsForTesting, kind, graph.HScrollOffsetForTesting,
            CommitGraphControl.HeaderHeight, 14, 6)!.Value;

    private static Point Center(HeaderIconRect r) => new(r.X + r.Width / 2, r.Y + r.Height / 2);

    [AvaloniaFact]
    public void RefsIconClickOpensThePopupNotAResize()
    {
        using var s = CreateGraph();
        var rect = IconRect(s.Graph, GraphColumnKind.Refs);
        var before = s.Graph.ColumnsForTesting.ToList();

        s.Window.MouseDown(Center(rect), MouseButton.Left);
        s.Window.MouseUp(Center(rect), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.True(s.Graph.RefSearchPopup.IsOpen);
        Assert.Equal(before.Select(c => c.Width), s.Graph.ColumnsForTesting.Select(c => c.Width));
    }

    [AvaloniaFact]
    public void MessageIconClickTogglesTheQueryBar()
    {
        using var s = CreateGraph();
        Assert.False(s.Graph.QueryBarControl.IsVisible);

        var rect = IconRect(s.Graph, GraphColumnKind.Message);
        s.Window.MouseDown(Center(rect), MouseButton.Left);
        s.Window.MouseUp(Center(rect), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.True(s.Graph.QueryBarControl.IsVisible);

        s.Window.MouseDown(Center(rect), MouseButton.Left);
        s.Window.MouseUp(Center(rect), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.False(s.Graph.QueryBarControl.IsVisible);
    }

    [AvaloniaFact]
    public void HeaderIconClickDoesNotReorderColumns()
    {
        using var s = CreateGraph();
        var before = s.Graph.ColumnsForTesting.Select(c => c.Kind).ToList();
        var rect = IconRect(s.Graph, GraphColumnKind.Refs);

        s.Window.MouseDown(Center(rect), MouseButton.Left);
        s.Window.MouseMove(new Point(Center(rect).X + 10, Center(rect).Y));
        s.Window.MouseUp(new Point(Center(rect).X + 10, Center(rect).Y), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(before, s.Graph.ColumnsForTesting.Select(c => c.Kind));
    }

    [AvaloniaFact]
    public void RightClickOnTheIconStillOpensTheColumnMenu()
    {
        using var s = CreateGraph();
        var rect = IconRect(s.Graph, GraphColumnKind.Refs);

        s.Window.MouseDown(Center(rect), MouseButton.Right);
        s.Window.MouseUp(Center(rect), MouseButton.Right);
        Dispatcher.UIThread.RunJobs();

        Assert.NotNull(s.Graph.OpenMenuForTesting);
        Assert.False(s.Graph.RefSearchPopup.IsOpen);
    }

    [AvaloniaFact]
    public void PopupIsEmptyForAnEmptyQuery()
    {
        using var s = CreateGraph();
        var rect = IconRect(s.Graph, GraphColumnKind.Refs);
        s.Window.MouseDown(Center(rect), MouseButton.Left);
        s.Window.MouseUp(Center(rect), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.Empty(s.Graph.RefSearchItems.Children);
    }

    [AvaloniaFact]
    public void PopupRanksBestMatchFirst()
    {
        using var s = CreateGraph();
        var rect = IconRect(s.Graph, GraphColumnKind.Refs);
        s.Window.MouseDown(Center(rect), MouseButton.Left);
        s.Window.MouseUp(Center(rect), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        s.Graph.RefSearchBox.Text = "authz";
        Dispatcher.UIThread.RunJobs();

        var labels = s.Graph.RefSearchItems.Children
            .OfType<Border>()
            .Select(b => ((StackPanel)b.Child!).Children.OfType<TextBlock>().Single().Text)
            .ToList();

        Assert.NotEmpty(labels);
        Assert.Equal("fix/authz-header", labels[0]);
    }

    [AvaloniaFact]
    public void PopupShowsAtMostTenMatches()
    {
        using var s = CreateGraph(extraBranches: 40);
        var rect = IconRect(s.Graph, GraphColumnKind.Refs);
        s.Window.MouseDown(Center(rect), MouseButton.Left);
        s.Window.MouseUp(Center(rect), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        s.Graph.RefSearchBox.Text = "extra";
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(10, s.Graph.RefSearchItems.Children.Count);
    }

    [AvaloniaFact]
    public void ClickingAMatchSelectsAndCentresTheRow()
    {
        using var s = CreateGraph();
        var rect = IconRect(s.Graph, GraphColumnKind.Refs);
        s.Window.MouseDown(Center(rect), MouseButton.Left);
        s.Window.MouseUp(Center(rect), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        s.Graph.RefSearchBox.Text = "feature/auth-rework";
        Dispatcher.UIThread.RunJobs();

        var row = s.Graph.RefSearchItems.Children.OfType<Border>().Single();
        var p = new Avalonia.Input.Pointer(1, Avalonia.Input.PointerType.Mouse, true);
        var pe = new PointerPressedEventArgs(row, p, row, new Point(5, 5), 0,
            new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonPressed), KeyModifiers.None);
        row.RaiseEvent(pe);
        Dispatcher.UIThread.RunJobs();

        Assert.False(s.Graph.RefSearchPopup.IsOpen);
        Assert.NotNull(s.Context.State.SelectedCommitId);
    }

    [AvaloniaFact]
    public void EscapeClosesThePopupWithoutNavigating()
    {
        using var s = CreateGraph();
        var rect = IconRect(s.Graph, GraphColumnKind.Refs);
        s.Window.MouseDown(Center(rect), MouseButton.Left);
        s.Window.MouseUp(Center(rect), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        var before = s.Context.State.SelectedCommitId;
        s.Graph.RefSearchBox.Text = "feature";
        Dispatcher.UIThread.RunJobs();

        s.Graph.RefSearchBox.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
        Dispatcher.UIThread.RunJobs();

        Assert.False(s.Graph.RefSearchPopup.IsOpen);
        Assert.Equal(before, s.Context.State.SelectedCommitId);
    }

    [AvaloniaFact]
    public void NoCentringWhenContentFitsTheViewport()
    {
        using var s = CreateGraph(width: 1000, height: 2000);
        var rect = IconRect(s.Graph, GraphColumnKind.Refs);
        s.Window.MouseDown(Center(rect), MouseButton.Left);
        s.Window.MouseUp(Center(rect), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        s.Graph.RefSearchBox.Text = "feature/auth-rework";
        Dispatcher.UIThread.RunJobs();

        var row = s.Graph.RefSearchItems.Children.OfType<Border>().Single();
        var p = new Avalonia.Input.Pointer(1, Avalonia.Input.PointerType.Mouse, true);
        var pe = new PointerPressedEventArgs(row, p, row, new Point(5, 5), 0,
            new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonPressed), KeyModifiers.None);
        row.RaiseEvent(pe);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(0, s.Graph.ScrollOffset);
    }
}
