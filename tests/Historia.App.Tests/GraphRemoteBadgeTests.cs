namespace HistoriaApp.Tests;

/// <summary>
/// With two or more remotes configured, a branch badge on the graph draws one numbered cloud per
/// remote in number order, each with its own "n · name" tooltip; with one remote it keeps a
/// single plain cloud. The measured badge width (the connector start) matches what is drawn, and
/// adding a remote renumbers the clouds live.
/// </summary>
[Trait("tier", "ui")]
public sealed class GraphRemoteBadgeTests
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

    /// <summary>A few commits on <c>main</c>, with <c>main</c> also present on every named remote.</summary>
    private sealed class RepoFixture : IDisposable
    {
        public string Path { get; }

        public RepoFixture(params string[] remotes)
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "historia-remotebadge-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
            RunGit("init", "-q", "-b", "main");
            RunGit("config", "user.name", "Test");
            RunGit("config", "user.email", "test@example.com");
            for (var i = 0; i < 3; i++)
            {
                File.WriteAllText(System.IO.Path.Combine(Path, "f.txt"), $"{i}\n");
                RunGit("add", ".");
                RunGit("commit", "-q", "-m", $"c{i}", "--no-gpg-sign");
            }
            foreach (var r in remotes)
                AddRemote(r);
        }

        public void AddRemote(string name)
        {
            RunGit("remote", "add", name, $"git@example.com:o/{name}.git");
            RunGit("update-ref", $"refs/remotes/{name}/main", "HEAD");
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
            var err = p.StandardError.ReadToEndAsync();
            p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            if (p.ExitCode != 0)
                throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {err.Result}");
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
        CommitGraphControl Graph, ServiceProvider Sp, RepoFixture Repo, RepositoryContext Context, Window Window) : IDisposable
    {
        public void Dispose()
        {
            try { Window.Close(); } catch { }
            try { Context.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
            try { Sp.Dispose(); } catch { }
            try { Repo.Dispose(); } catch { }
        }
    }

    private static Setup CreateGraph(params string[] remotes)
    {
        var repo = new RepoFixture(remotes);
        var sp = CreateServices();
        var ws = sp.GetRequiredService<IWorkspaceService>();
        var ctx = ws.OpenAsync(repo.Path).GetAwaiter().GetResult();
        ctx.MarkFirstGraphPaintDone();
        ctx.MarkFirstFullLoadDone();
        ctx.State.RefreshRefsAsync().GetAwaiter().GetResult();

        var graph = new CommitGraphControl();
        graph.Initialize(sp);
        var window = new Window { Width = 1400, Height = 600, Content = graph };
        window.Show();
        ws.Focus(ctx);
        Dispatcher.UIThread.RunJobs();

        for (var i = 0; i < 500 && graph.RowCountForTesting < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }
        Assert.Equal(3, graph.RowCountForTesting);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        return new Setup(graph, sp, repo, ctx, window);
    }

    private static void RenderFrame(CommitGraphControl graph)
    {
        var size = new PixelSize((int)graph.Bounds.Width, (int)graph.Bounds.Height);
        using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(size);
        using var dc = bitmap.CreateDrawingContext();
        graph.Render(dc);
    }

    /// <summary>The remote clouds' hover rects, left to right.</summary>
    private static List<(Rect Rect, string Tip)> CloudRects(CommitGraphControl graph) =>
        graph.IconRectsForTesting.Where(r => r.Tip != "local").OrderBy(r => r.Rect.X).ToList();

    [AvaloniaFact]
    public void Two_remotes_draw_two_numbered_icon_rects_in_number_order()
    {
        using var s = CreateGraph("upstream", "origin");
        RenderFrame(s.Graph);

        var clouds = CloudRects(s.Graph);
        Assert.Equal(["1 · origin", "2 · upstream"], clouds.Select(c => c.Tip).ToList());
        Assert.All(clouds, c => Assert.Equal(16, c.Rect.Width));
    }

    [AvaloniaFact]
    public void Numbered_clouds_follow_number_order_not_ref_order()
    {
        // Refs sort bytewise (Upstream < alpha < origin); numbers sort case-insensitively.
        using var s = CreateGraph("origin", "Upstream", "alpha");
        RenderFrame(s.Graph);

        Assert.Equal(["1 · alpha", "2 · origin", "3 · Upstream"], CloudRects(s.Graph).Select(c => c.Tip).ToList());
    }

    [AvaloniaFact]
    public void Single_remote_draws_one_plain_cloud()
    {
        using var s = CreateGraph("origin");
        RenderFrame(s.Graph);

        var cloud = Assert.Single(CloudRects(s.Graph));
        Assert.Equal("origin", cloud.Tip);
        Assert.Equal(12, cloud.Rect.Width);
        Assert.False(s.Graph.RemoteNumberingForTesting.IsActive);
    }

    [AvaloniaFact]
    public void Badge_width_measure_matches_draw()
    {
        using var s = CreateGraph("origin", "upstream", "fork");
        RenderFrame(s.Graph);

        var pill = s.Graph.PillRectsForTesting.Single(p => p.Deco.Label == "main");
        Assert.Equal(3, pill.Deco.Remotes.Length);
        Assert.Equal(pill.Rect.Right, s.Graph.BadgeRightEdgesForTesting[pill.Row], 3);

        var clouds = CloudRects(s.Graph);
        Assert.Equal(3, clouds.Count);
        Assert.True(clouds[^1].Rect.Right <= pill.Rect.Right, "the cloud cluster must sit inside the badge");
        Assert.True(clouds[0].Rect.X >= pill.Rect.X, "the cloud cluster must sit inside the badge");
    }

    [AvaloniaFact]
    public void Adding_a_remote_renumbers_without_reopening()
    {
        using var s = CreateGraph("origin", "upstream");
        RenderFrame(s.Graph);
        Assert.Equal(["1 · origin", "2 · upstream"], CloudRects(s.Graph).Select(c => c.Tip).ToList());

        s.Repo.AddRemote("aardvark");
        s.Context.State.RefreshRefsAsync().GetAwaiter().GetResult();
        for (var i = 0; i < 200 && s.Graph.RemoteNumberingForTesting.Count < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }
        for (var i = 0; i < 200; i++)
        {
            Dispatcher.UIThread.RunJobs();
            RenderFrame(s.Graph);
            if (CloudRects(s.Graph).Count == 3)
                break;
            Thread.Sleep(10);
        }

        Assert.Equal(["1 · aardvark", "2 · origin", "3 · upstream"], CloudRects(s.Graph).Select(c => c.Tip).ToList());
    }
}
