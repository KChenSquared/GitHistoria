namespace HistoriaApp.Tests;

/// <summary>The Graph column draws each lane's vertical pieces as merged runs, one line per
/// run rather than one per row per lane. Drawing per row again produces no error, only
/// slower frames, so the draw count is asserted here, along with the builder's
/// zero-growth second frame.</summary>
[Trait("tier", "ui")]
public sealed class GraphLaneRunDrawTests
{
    private const int Branches = 6;
    private const int CommitsPerBranch = 40;

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

    /// <summary><see cref="Branches"/> long-lived branches off one root, their commits
    /// interleaved by date so every branch keeps its own lane through the whole history.
    /// Built with one <c>git fast-import</c> run.</summary>
    private sealed class ParallelBranchesRepo : IDisposable
    {
        public string Path { get; }
        public int CommitCount => 1 + Branches * CommitsPerBranch;

        public ParallelBranchesRepo()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "historia-laneruns-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
            RunGit(Path, null, "init", "-q");

            var sb = new System.Text.StringBuilder();
            var when = 1_700_000_000L;
            AppendCommit(sb, "refs/heads/main", mark: 1, from: null, when, "root");
            var tip = new int[Branches];
            var mark = 2;
            for (var n = 0; n < CommitsPerBranch; n++)
            {
                for (var k = 0; k < Branches; k++)
                {
                    when += 60;
                    AppendCommit(sb, k == 0 ? "refs/heads/main" : $"refs/heads/b{k}", mark,
                        from: tip[k] == 0 ? 1 : tip[k], when, $"b{k} commit {n}");
                    tip[k] = mark++;
                }
            }
            sb.Append("done\n");
            RunGit(Path, sb.ToString(), "fast-import", "--quiet", "--done");
            RunGit(Path, null, "symbolic-ref", "HEAD", "refs/heads/main");
        }

        private static void AppendCommit(System.Text.StringBuilder sb, string branch, int mark, int? from, long when, string message)
        {
            sb.Append("commit ").Append(branch).Append('\n');
            sb.Append("mark :").Append(mark).Append('\n');
            sb.Append("committer Test <test@example.com> ").Append(when).Append(" +0000\n");
            sb.Append("data ").Append(System.Text.Encoding.UTF8.GetByteCount(message)).Append('\n').Append(message).Append('\n');
            if (from is { } f)
                sb.Append("from :").Append(f).Append('\n');
            sb.Append('\n');
        }

        private static void RunGit(string dir, string? stdin, params string[] args)
        {
            var psi = new System.Diagnostics.ProcessStartInfo("git")
            {
                WorkingDirectory = dir,
                RedirectStandardInput = stdin is not null,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
            using var p = System.Diagnostics.Process.Start(psi)!;
            if (stdin is not null)
            {
                p.StandardInput.Write(stdin);
                p.StandardInput.Close();
            }
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
        CommitGraphControl Graph, ServiceProvider Sp, ParallelBranchesRepo Repo,
        RepositoryContext Context, Window Window) : IDisposable
    {
        public void Dispose()
        {
            try { Window.Close(); } catch { }
            try { Context.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
            try { Sp.Dispose(); } catch { }
            try { Repo.Dispose(); } catch { }
        }
    }

    private static Setup CreateGraph(double width = 1200, double height = 1400)
    {
        var repo = new ParallelBranchesRepo();
        var sp = CreateServices();
        var ws = sp.GetRequiredService<IWorkspaceService>();
        var ctx = ws.OpenAsync(repo.Path).GetAwaiter().GetResult();
        ctx.MarkFirstGraphPaintDone();
        ctx.MarkFirstFullLoadDone();
        ctx.State.RefreshRefsAsync().GetAwaiter().GetResult();

        var graph = new CommitGraphControl();
        graph.Initialize(sp);
        var window = new Window { Width = width, Height = height, Content = graph };
        window.Show();
        ws.Focus(ctx);
        Dispatcher.UIThread.RunJobs();

        for (var i = 0; i < 500 && graph.RowCountForTesting < repo.CommitCount; i++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }
        Assert.Equal(repo.CommitCount, graph.RowCountForTesting);

        return new Setup(graph, sp, repo, ctx, window);
    }

    private static void RenderFrame(CommitGraphControl graph)
    {
        var size = new PixelSize((int)graph.Bounds.Width, (int)graph.Bounds.Height);
        using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(size);
        using var dc = bitmap.CreateDrawingContext();
        graph.Render(dc);
    }

    [AvaloniaFact]
    public void Parallel_branches_draw_one_run_per_lane_not_per_row()
    {
        using var s = CreateGraph();
        Assert.True(s.Graph.Bounds.Height > 30 * s.Graph.RowHeightValue,
            $"the viewport must show at least 30 rows (height {s.Graph.Bounds.Height}, row {s.Graph.RowHeightValue})");

        RenderFrame(s.Graph);

        Assert.True(s.Graph.LanePiecesSeenForTesting >= 30 * Branches,
            $"expected at least {30 * Branches} lane pieces fed in, saw {s.Graph.LanePiecesSeenForTesting}");
        Assert.InRange(s.Graph.LaneRunsDrawnForTesting, Branches, 3 * Branches);
    }

    [AvaloniaFact]
    public void Second_frame_records_zero_lane_run_growths()
    {
        using var s = CreateGraph();

        RenderFrame(s.Graph);
        s.Graph.ResetLaneRunCountersForTesting();
        RenderFrame(s.Graph);

        Assert.True(s.Graph.LaneRunsDrawnForTesting > 0);
        Assert.Equal(0, s.Graph.LaneRunGrowthsForTesting);
    }
}
