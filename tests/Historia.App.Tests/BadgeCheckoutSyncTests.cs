namespace HistoriaApp.Tests;

/// <summary>
/// Double-clicking a remote badge switches to the local branch, then syncs only
/// after the checkout succeeded. If checkout fails, sync is never attempted.
/// </summary>
[Trait("tier", "ui")]
public sealed class BadgeCheckoutSyncTests
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

    private sealed class RemoteAndClonesFixture : IDisposable
    {
        public string RemotePath { get; }
        public string LocalPath { get; }
        public string MainTipSha { get; }
        public string RemoteFeatureTipSha { get; }

        public RemoteAndClonesFixture(bool dirtyLocalEdit = true)
        {
            var baseDir = Path.Combine(Path.GetTempPath(), "historia-badgecheckout-" + Guid.NewGuid().ToString("N"));
            RemotePath = Path.Combine(baseDir, "remote.git");
            LocalPath = Path.Combine(baseDir, "local");
            var clone2 = Path.Combine(baseDir, "clone2");

            Directory.CreateDirectory(RemotePath);
            RunGit(RemotePath, "init", "--bare");

            RunGit(baseDir, "clone", RemotePath, LocalPath);
            ConfigureGit(LocalPath);

            var sharedFile = Path.Combine(LocalPath, "shared.txt");
            File.WriteAllText(sharedFile, "main initial\n");
            RunGit(LocalPath, "add", "shared.txt");
            RunGit(LocalPath, "commit", "-m", "main commit", "--no-gpg-sign");
            RunGit(LocalPath, "branch", "-M", "main");
            RunGit(LocalPath, "push", "-u", "origin", "main");
            MainTipSha = RunGit(LocalPath, "rev-parse", "HEAD").Trim();

            RunGit(LocalPath, "checkout", "-b", "feature");
            File.WriteAllText(sharedFile, "feature commit 1\n");
            RunGit(LocalPath, "add", "shared.txt");
            RunGit(LocalPath, "commit", "-m", "feature commit 1", "--no-gpg-sign");
            RunGit(LocalPath, "push", "-u", "origin", "feature");
            RunGit(LocalPath, "checkout", "main");

            RunGit(baseDir, "clone", RemotePath, clone2);
            ConfigureGit(clone2);
            RunGit(clone2, "checkout", "feature");
            File.WriteAllText(Path.Combine(clone2, "feature_extra.txt"), "feature commit 2\n");
            RunGit(clone2, "add", "feature_extra.txt");
            RunGit(clone2, "commit", "-m", "feature commit 2", "--no-gpg-sign");
            RunGit(clone2, "push", "origin", "feature");
            RemoteFeatureTipSha = RunGit(clone2, "rev-parse", "HEAD").Trim();

            RunGit(LocalPath, "fetch", "origin");

            if (dirtyLocalEdit)
            {
                File.WriteAllText(sharedFile, "conflicting uncommitted edit\n");
            }
        }

        private static void ConfigureGit(string dir)
        {
            RunGit(dir, "config", "user.name", "Test");
            RunGit(dir, "config", "user.email", "test@example.com");
            RunGit(dir, "config", "commit.gpgsign", "false");
        }

        public static string RunGit(string dir, params string[] args)
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
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            return output;
        }

        public void Dispose()
        {
            var root = Path.GetDirectoryName(RemotePath);
            try
            {
                if (root is not null && Directory.Exists(root))
                {
                    foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                    {
                        var attr = File.GetAttributes(f);
                        if ((attr & FileAttributes.ReadOnly) != 0)
                            File.SetAttributes(f, attr & ~FileAttributes.ReadOnly);
                    }
                    Directory.Delete(root, true);
                }
            }
            catch { }
        }
    }

    private static ServiceProvider CreateServices(CapturingNotificationService notify)
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
        sc.AddSingleton<INotificationService>(notify);
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

    [AvaloniaFact]
    public async Task A_failed_checkout_never_attempts_the_pull()
    {
        using var fixture = new RemoteAndClonesFixture(dirtyLocalEdit: true);
        var notify = new CapturingNotificationService();
        await using var sp = CreateServices(notify);
        var ws = sp.GetRequiredService<IWorkspaceService>();

        var ctx = await ws.OpenAsync(fixture.LocalPath);
        ctx.MarkFirstGraphPaintDone();
        ctx.MarkFirstFullLoadDone();
        await ctx.State.RefreshRefsAsync();

        var graph = new CommitGraphControl();
        graph.Initialize(sp);
        var window = new Window { Width = 800, Height = 600, Content = graph };
        window.Show();
        ws.Focus(ctx);
        Dispatcher.UIThread.RunJobs();

        var deco = new GraphDecoration
        {
            RowIndex = 0,
            Type = DecorationType.RemoteBranch,
            Label = "feature",
            ColorIndex = 0,
            IsLocal = false,
            RemotesOrNull = ["origin"],
        };

        await graph.CheckoutLocalThenMaybeSyncAsync(ctx, deco, "feature");
        Dispatcher.UIThread.RunJobs();

        var currentHead = RemoteAndClonesFixture.RunGit(fixture.LocalPath, "rev-parse", "--abbrev-ref", "HEAD").Trim();
        Assert.Equal("main", currentHead);

        var mainSha = RemoteAndClonesFixture.RunGit(fixture.LocalPath, "rev-parse", "HEAD").Trim();
        Assert.Equal(fixture.MainTipSha, mainSha);

        var mergeHeadPath = Path.Combine(fixture.LocalPath, ".git", "MERGE_HEAD");
        Assert.False(File.Exists(mergeHeadPath));

        Assert.Equal(1, notify.Published.Count(n => n.Severity == NotificationSeverity.Error));
        Assert.DoesNotContain(notify.Published, n => n.Message.Contains("Pull", StringComparison.OrdinalIgnoreCase));

        window.Close();
        await ws.CloseAsync(ctx);
    }

    [AvaloniaFact]
    public async Task A_successful_checkout_still_fast_forwards()
    {
        using var fixture = new RemoteAndClonesFixture(dirtyLocalEdit: false);
        var notify = new CapturingNotificationService();
        await using var sp = CreateServices(notify);
        var ws = sp.GetRequiredService<IWorkspaceService>();

        var ctx = await ws.OpenAsync(fixture.LocalPath);
        ctx.MarkFirstGraphPaintDone();
        ctx.MarkFirstFullLoadDone();
        await ctx.State.RefreshRefsAsync();

        var graph = new CommitGraphControl();
        graph.Initialize(sp);
        var window = new Window { Width = 800, Height = 600, Content = graph };
        window.Show();
        ws.Focus(ctx);
        Dispatcher.UIThread.RunJobs();

        var deco = new GraphDecoration
        {
            RowIndex = 0,
            Type = DecorationType.RemoteBranch,
            Label = "feature",
            ColorIndex = 0,
            IsLocal = false,
            RemotesOrNull = ["origin"],
        };

        await graph.CheckoutLocalThenMaybeSyncAsync(ctx, deco, "feature");
        Dispatcher.UIThread.RunJobs();

        var currentHead = RemoteAndClonesFixture.RunGit(fixture.LocalPath, "rev-parse", "--abbrev-ref", "HEAD").Trim();
        Assert.Equal("feature", currentHead);

        var featureSha = RemoteAndClonesFixture.RunGit(fixture.LocalPath, "rev-parse", "HEAD").Trim();
        Assert.Equal(fixture.RemoteFeatureTipSha, featureSha);

        window.Close();
        await ws.CloseAsync(ctx);
    }
}
