namespace HistoriaApp.Tests;

/// <summary>
/// Declining the push-rejection modal, dismissing it a second time, and
/// cancelling an in-flight network op must never publish a "…cancelled." card that just restates
/// the outcome of the click the user just made — the decline is logged under the "declined"
/// dev-log category (a no-op unless dev logging is on) instead.
/// </summary>
[Trait("tier", "ui")]
public sealed class NetworkCancelSilenceTests
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

    /// <summary>Scripts <see cref="ChooseAsync"/> to a fixed choice (default -1: Cancel) and
    /// counts calls; every other member throws since this flow never reaches them.</summary>
    private sealed class FakeDialogService : IDialogService
    {
        public int ChooseCalls;
        public int ChooseResult = -1;

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

    /// <summary>A bare remote plus one clone ("local") pushed ahead of what "local" knows about —
    /// a second clone pushes first, then local commits without fetching, so local's own push is
    /// rejected as non-fast-forward.</summary>
    private sealed class RejectedPushFixture : IDisposable
    {
        public string RemotePath { get; }
        public string LocalPath { get; }

        public RejectedPushFixture()
        {
            var baseDir = Path.Combine(Path.GetTempPath(), "historia-netcancel-" + Guid.NewGuid().ToString("N"));
            RemotePath = Path.Combine(baseDir, "remote.git");
            LocalPath = Path.Combine(baseDir, "local");
            var other = Path.Combine(baseDir, "other");

            Directory.CreateDirectory(RemotePath);
            RunGit(RemotePath, "init", "--bare");

            RunGit(baseDir, "clone", RemotePath, LocalPath);
            ConfigureGit(LocalPath);
            File.WriteAllText(Path.Combine(LocalPath, "f.txt"), "init\n");
            RunGit(LocalPath, "add", "f.txt");
            RunGit(LocalPath, "commit", "-m", "init", "--no-gpg-sign");
            RunGit(LocalPath, "branch", "-M", "main");
            RunGit(LocalPath, "push", "-u", "origin", "main");

            RunGit(baseDir, "clone", RemotePath, other);
            ConfigureGit(other);
            RunGit(other, "checkout", "main");
            File.WriteAllText(Path.Combine(other, "f.txt"), "from other clone\n");
            RunGit(other, "add", "f.txt");
            RunGit(other, "commit", "-m", "other commit", "--no-gpg-sign");
            RunGit(other, "push", "origin", "main");

            // Local diverges without fetching the other clone's push, so pushing local's own
            // commit is rejected as non-fast-forward.
            File.WriteAllText(Path.Combine(LocalPath, "g.txt"), "local commit\n");
            RunGit(LocalPath, "add", "g.txt");
            RunGit(LocalPath, "commit", "-m", "local commit", "--no-gpg-sign");
        }

        private static void ConfigureGit(string dir)
        {
            RunGit(dir, "config", "user.name", "Test");
            RunGit(dir, "config", "user.email", "test@example.com");
            RunGit(dir, "config", "commit.gpgsign", "false");
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

    /// <summary>Starts an action and waits for the runner's idle tick. It subscribes before
    /// <paramref name="start"/> runs: an op that fails or is cancelled before its first real await
    /// raises the idle tick synchronously inside <c>RequestNetworkAction</c>.</summary>
    private static async Task<RepoNetworkProgress> WaitForIdleAsync(IRepoNetworkActionRunner runner, Action start)
    {
        RepoNetworkProgress? final = null;
        void OnProgress(object? s, RepoNetworkProgress p) { if (p.Progress is null) final = p; }
        runner.ProgressChanged += OnProgress;
        try
        {
            start();
            for (int i = 0; i < 200 && final is null; i++)
            {
                Dispatcher.UIThread.RunJobs();
                await Task.Delay(20);
            }
        }
        finally
        {
            runner.ProgressChanged -= OnProgress;
        }
        Assert.NotNull(final);
        return final!;
    }

    [AvaloniaFact]
    public async Task Cancelling_push_rejected_modal_shows_no_notification()
    {
        using var fixture = new RejectedPushFixture();
        var notify = new CapturingNotificationService();
        var dialogs = new FakeDialogService { ChooseResult = -1 };
        await using var sp = CreateServices(notify, dialogs);

        var ws = sp.GetRequiredService<IWorkspaceService>();
        var ctx = await ws.OpenAsync(fixture.LocalPath);
        try
        {
            ctx.MarkFirstGraphPaintDone();
            ctx.MarkFirstFullLoadDone();
            await ctx.State.RefreshRefsAsync();
            ws.Focus(ctx);

            var bus = sp.GetRequiredService<IRepoActionBus>();
            var runner = sp.GetRequiredService<IRepoNetworkActionRunner>();

            await WaitForIdleAsync(runner, () => bus.RequestNetworkAction(RepoNetworkAction.Push));

            Assert.Equal(1, dialogs.ChooseCalls);
            Assert.Empty(notify.Published);
        }
        finally
        {
            await ws.CloseAsync(ctx);
        }
    }

    [AvaloniaFact]
    public async Task Push_rejected_modal_reopens_on_second_push()
    {
        using var fixture = new RejectedPushFixture();
        var notify = new CapturingNotificationService();
        var dialogs = new FakeDialogService { ChooseResult = -1 };
        await using var sp = CreateServices(notify, dialogs);

        var ws = sp.GetRequiredService<IWorkspaceService>();
        var ctx = await ws.OpenAsync(fixture.LocalPath);
        try
        {
            ctx.MarkFirstGraphPaintDone();
            ctx.MarkFirstFullLoadDone();
            await ctx.State.RefreshRefsAsync();
            ws.Focus(ctx);

            var bus = sp.GetRequiredService<IRepoActionBus>();
            var runner = sp.GetRequiredService<IRepoNetworkActionRunner>();

            await WaitForIdleAsync(runner, () => bus.RequestNetworkAction(RepoNetworkAction.Push));
            await WaitForIdleAsync(runner, () => bus.RequestNetworkAction(RepoNetworkAction.Push));

            Assert.Equal(2, dialogs.ChooseCalls);
            Assert.Empty(notify.Published);
        }
        finally
        {
            await ws.CloseAsync(ctx);
        }
    }

    [AvaloniaFact]
    public async Task Cancelling_push_rejected_modal_is_logged_when_dev_log_enabled()
    {
        var stateDir = Path.Combine(Path.GetTempPath(), "historia-netcancel-log-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stateDir);
        Historia.Diagnostics.AppLog.Enabled = false;
        AppPaths.OverrideForTesting(stateDir);
        try
        {
            using var fixture = new RejectedPushFixture();
            var notify = new CapturingNotificationService();
            var dialogs = new FakeDialogService { ChooseResult = -1 };
            await using var sp = CreateServices(notify, dialogs);

            var ws = sp.GetRequiredService<IWorkspaceService>();
            var ctx = await ws.OpenAsync(fixture.LocalPath);
            try
            {
                ctx.MarkFirstGraphPaintDone();
                ctx.MarkFirstFullLoadDone();
                await ctx.State.RefreshRefsAsync();
                ws.Focus(ctx);

                Historia.Diagnostics.AppLog.Enabled = true;

                var bus = sp.GetRequiredService<IRepoActionBus>();
                var runner = sp.GetRequiredService<IRepoNetworkActionRunner>();

                await WaitForIdleAsync(runner, () => bus.RequestNetworkAction(RepoNetworkAction.Push));
            }
            finally
            {
                await ws.CloseAsync(ctx);
            }

            Historia.Diagnostics.AppLog.Enabled = false; // close the session writer before reading the file back
            var logFile = Assert.Single(Directory.GetFiles(AppPaths.LogsDirectory, "dev-*.log"));
            var text = File.ReadAllText(logFile);
            Assert.Contains("[declined]", text);
            Assert.Contains("Push rejected modal cancelled", text);
        }
        finally
        {
            Historia.Diagnostics.AppLog.Enabled = false;
            AppPaths.OverrideForTesting(null);
            try { Directory.Delete(stateDir, true); } catch { }
        }
    }

    [AvaloniaFact]
    public async Task Cancelled_network_op_shows_no_notification()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "historia-netcancel-fetch-" + Guid.NewGuid().ToString("N"));
        var remotePath = Path.Combine(baseDir, "remote.git");
        var localPath = Path.Combine(baseDir, "local");
        Directory.CreateDirectory(remotePath);
        RunGit(remotePath, "init", "--bare");
        RunGit(baseDir, "clone", remotePath, localPath);
        RunGit(localPath, "config", "user.name", "Test");
        RunGit(localPath, "config", "user.email", "test@example.com");
        File.WriteAllText(Path.Combine(localPath, "f.txt"), "init\n");
        RunGit(localPath, "add", "f.txt");
        RunGit(localPath, "commit", "-m", "init", "--no-gpg-sign");
        RunGit(localPath, "branch", "-M", "main");
        RunGit(localPath, "push", "-u", "origin", "main");

        var notify = new CapturingNotificationService();
        var dialogs = new FakeDialogService();
        await using var sp = CreateServices(notify, dialogs);
        var ws = sp.GetRequiredService<IWorkspaceService>();
        var ctx = await ws.OpenAsync(localPath);
        try
        {
            ctx.MarkFirstGraphPaintDone();
            ctx.MarkFirstFullLoadDone();
            await ctx.State.RefreshRefsAsync();
            ws.Focus(ctx);

            var bus = sp.GetRequiredService<IRepoActionBus>();
            var runner = sp.GetRequiredService<IRepoNetworkActionRunner>();

            // Cancel from the very first progress tick, before the fetch has a chance to
            // complete — the cancellation token is already live by the time that tick fires
            // (RunAsync assigns _cts, then raises this first tick, before any await).
            void CancelOnFirstTick(object? s, RepoNetworkProgress p)
            {
                if (p.Progress is not null)
                    runner.CancelCurrent();
            }
            runner.ProgressChanged += CancelOnFirstTick;
            try
            {
                await WaitForIdleAsync(runner, () => bus.RequestNetworkAction(RepoNetworkAction.Fetch));
            }
            finally
            {
                runner.ProgressChanged -= CancelOnFirstTick;
            }

            Assert.Empty(notify.Published);
        }
        finally
        {
            await ws.CloseAsync(ctx);
            try { Directory.Delete(baseDir, true); } catch { }
        }

        static void RunGit(string dir, params string[] args)
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
    }
}
