namespace HistoriaApp.Tests;

/// <summary>
/// How the network-action runner reports failures: a server-policy decline is an error toast
/// (never the non-fast-forward chooser or a force push), a non-fast-forward still opens the
/// chooser, the toast names the operation once, a request on a busy repository gets a notice
/// naming what is running while other repositories stay free, a request that finds auto-fetch
/// running waits for it (cancellably), and a request pinned to a repository runs there whichever
/// tab is focused.
/// </summary>
[Trait("tier", "ui")]
public sealed class RepoNetworkErrorRoutingTests
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

    /// <summary>Counts the push-rejection chooser and the destructive confirm; both answer Cancel.</summary>
    private sealed class CountingDialogService : IDialogService
    {
        public int ChooseCalls;
        public int ConfirmDestructiveCalls;

        public Task<int> ChooseAsync(string title, string message, IReadOnlyList<string> options,
            int defaultIndex = 0, int destructiveIndex = -1, DialogIcon icon = DialogIcon.None)
        {
            ChooseCalls++;
            return Task.FromResult(-1);
        }

        public Task<bool> ConfirmDestructiveAsync(string title, string effect, string confirmLabel)
        {
            ConfirmDestructiveCalls++;
            return Task.FromResult(false);
        }

        public Task<string?> PickFolderAsync(string title, string? initialDirectory = null) => throw new NotSupportedException();
        public Task<string?> PickFileAsync(string title, string filter, string? initialDirectory = null) => throw new NotSupportedException();
        public Task<bool> ConfirmAsync(string title, string message, bool destructive = false,
            string confirmLabel = "OK", bool defaultToCancel = false, DialogIcon icon = DialogIcon.None) => throw new NotSupportedException();
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

    /// <summary>A bare remote and a clone ("local") with one unpushed commit on <c>main</c>.
    /// <see cref="Diverge"/> makes the remote move on first (non-fast-forward);
    /// <see cref="DeclineEverything"/> installs a pre-receive hook that refuses every push.</summary>
    private sealed class PushFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "historia-neterr-" + Guid.NewGuid().ToString("N"));

        public string RemotePath { get; }
        public string LocalPath { get; }

        public PushFixture()
        {
            RemotePath = Path.Combine(_root, "remote.git");
            LocalPath = Path.Combine(_root, "local");
            Directory.CreateDirectory(RemotePath);
            RunGit(RemotePath, "init", "--bare");
            RunGit(_root, "clone", RemotePath, LocalPath);
            Configure(LocalPath);
            File.WriteAllText(Path.Combine(LocalPath, "f.txt"), "init\n");
            RunGit(LocalPath, "add", "f.txt");
            RunGit(LocalPath, "commit", "-m", "init");
            RunGit(LocalPath, "branch", "-M", "main");
            RunGit(LocalPath, "push", "-u", "origin", "main");
            File.WriteAllText(Path.Combine(LocalPath, "g.txt"), "local\n");
            RunGit(LocalPath, "add", "g.txt");
            RunGit(LocalPath, "commit", "-m", "local commit");
        }

        public PushFixture Diverge()
        {
            var other = Path.Combine(_root, "other");
            RunGit(_root, "clone", RemotePath, other);
            Configure(other);
            RunGit(other, "checkout", "main");
            File.WriteAllText(Path.Combine(other, "f.txt"), "other\n");
            RunGit(other, "add", "f.txt");
            RunGit(other, "commit", "-m", "other commit");
            RunGit(other, "push", "origin", "main");
            return this;
        }

        public PushFixture DeclineEverything()
        {
            var hook = Path.Combine(RemotePath, "hooks", "pre-receive");
            Directory.CreateDirectory(Path.GetDirectoryName(hook)!);
            File.WriteAllText(hook,
                "#!/bin/sh\n"
                + "echo 'error: File big.bin is 361.72 MB; this exceeds the file size limit of 100.00 MB' >&2\n"
                + "exit 1\n");
            return this;
        }

        public PushFixture PointOriginAt(string url)
        {
            RunGit(LocalPath, "remote", "set-url", "origin", url);
            return this;
        }

        private static void Configure(string dir)
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
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            Task.WaitAll(stdout, stderr);
            p.WaitForExit();
        }

        public void Dispose()
        {
            try
            {
                if (!Directory.Exists(_root)) return;
                foreach (var f in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                {
                    var attr = File.GetAttributes(f);
                    if ((attr & FileAttributes.ReadOnly) != 0)
                        File.SetAttributes(f, attr & ~FileAttributes.ReadOnly);
                }
                Directory.Delete(_root, true);
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

    private sealed class Harness : IAsyncDisposable
    {
        private readonly List<RepositoryContext> _opened = [];

        public CapturingNotificationService Notify { get; } = new();
        public CountingDialogService Dialogs { get; } = new();
        public ServiceProvider Services { get; }
        public IWorkspaceService Workspace => Services.GetRequiredService<IWorkspaceService>();
        public IRepoActionBus Bus => Services.GetRequiredService<IRepoActionBus>();
        public IRepoNetworkActionRunner Runner => Services.GetRequiredService<IRepoNetworkActionRunner>();

        public Harness() => Services = CreateServices(Notify, Dialogs);

        public async Task<RepositoryContext> OpenAsync(string path, bool focus = true)
        {
            var ctx = await Workspace.OpenAsync(path);
            _opened.Add(ctx);
            ctx.MarkFirstGraphPaintDone();
            ctx.MarkFirstFullLoadDone();
            await ctx.State.RefreshRefsAsync();
            if (focus) Workspace.Focus(ctx);
            return ctx;
        }

        /// <summary>Starts an action and waits for the runner's idle tick (subscribed first: a
        /// failure before the first real await raises it synchronously).</summary>
        public async Task RunToIdleAsync(Action start)
        {
            RepoNetworkProgress? final = null;
            void OnProgress(object? s, RepoNetworkProgress p) { if (p.Progress is null) final = p; }
            Runner.ProgressChanged += OnProgress;
            try
            {
                start();
                for (int i = 0; i < 1000 && final is null; i++)
                {
                    Dispatcher.UIThread.RunJobs();
                    await Task.Delay(20);
                }
            }
            finally
            {
                Runner.ProgressChanged -= OnProgress;
            }
            Assert.NotNull(final);
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var ctx in _opened)
                await Workspace.CloseAsync(ctx);
            await Services.DisposeAsync();
        }
    }

    [AvaloniaFact]
    public async Task A_declined_push_is_an_error_toast_never_the_rejection_chooser()
    {
        using var fixture = new PushFixture().DeclineEverything();
        await using var h = new Harness();
        await h.OpenAsync(fixture.LocalPath);

        await h.RunToIdleAsync(() => h.Bus.RequestNetworkAction(RepoNetworkAction.Push));

        Assert.Equal(0, h.Dialogs.ChooseCalls);
        Assert.Equal(0, h.Dialogs.ConfirmDestructiveCalls);
        var error = Assert.Single(h.Notify.Published, n => n.Severity == NotificationSeverity.Error);
        Assert.Equal("Push failed: the remote declined it — its reason is in Details.", error.Message);
        Assert.Equal(1, error.Message.Split("Push").Length - 1);
        Assert.StartsWith("error: File big.bin is 361.72 MB", error.Details);
    }

    [AvaloniaFact]
    public async Task A_non_fast_forward_push_on_the_checked_out_branch_still_opens_the_chooser()
    {
        using var fixture = new PushFixture().Diverge();
        await using var h = new Harness();
        await h.OpenAsync(fixture.LocalPath);

        await h.RunToIdleAsync(() => h.Bus.RequestNetworkAction(RepoNetworkAction.Push));

        Assert.Equal(1, h.Dialogs.ChooseCalls);
    }

    [AvaloniaFact]
    public async Task An_unreachable_push_names_the_operation_once()
    {
        using var fixture = new PushFixture().PointOriginAt("https://127.0.0.1:1/r.git");
        await using var h = new Harness();
        await h.OpenAsync(fixture.LocalPath);

        await h.RunToIdleAsync(() => h.Bus.RequestNetworkAction(RepoNetworkAction.Push));

        var error = Assert.Single(h.Notify.Published, n => n.Severity == NotificationSeverity.Error);
        Assert.StartsWith("Push failed: ", error.Message);
        Assert.DoesNotContain("Push failed: Push failed", error.Message);
    }

    [AvaloniaFact]
    public async Task A_second_request_on_a_busy_repo_gets_a_notice_naming_it_and_runs_nothing()
    {
        using var fixture = new PushFixture();
        await using var h = new Harness();
        var repo = await h.OpenAsync(fixture.LocalPath);

        await h.RunToIdleAsync(() =>
        {
            h.Bus.RequestNetworkAction(RepoNetworkAction.Fetch);
            h.Bus.RequestNetworkAction(RepoNetworkAction.Fetch);
        });

        var info = Assert.Single(h.Notify.Published, n => n.Severity == NotificationSeverity.Info);
        Assert.Equal($"Fetch on {repo.DisplayName} is still running.", info.Message);
        Assert.Single(h.Notify.Published, n => n.Severity == NotificationSeverity.Success);
    }

    [AvaloniaFact]
    public async Task A_busy_repo_does_not_block_a_fetch_on_another_repo()
    {
        using var busyFixture = new PushFixture();
        using var otherFixture = new PushFixture();
        await using var h = new Harness();
        var busy = await h.OpenAsync(busyFixture.LocalPath, focus: false);
        var other = await h.OpenAsync(otherFixture.LocalPath);

        Assert.True(busy.TryBeginNetworkOperation(new NetworkOperationInfo(NetworkOperationOwner.User, "Pull"), out var held));
        using (held)
        {
            await h.RunToIdleAsync(() => h.Bus.RequestNetworkAction(
                new RepoNetworkRequest(RepoNetworkAction.Fetch, null, null, Repo: other)));
        }

        Assert.DoesNotContain(h.Notify.Published, n => n.Severity == NotificationSeverity.Info);
        Assert.Single(h.Notify.Published, n => n.Severity == NotificationSeverity.Success);
    }

    [AvaloniaFact]
    public async Task A_lock_cleanup_on_the_repo_gets_a_notice_naming_it()
    {
        using var fixture = new PushFixture();
        await using var h = new Harness();
        var repo = await h.OpenAsync(fixture.LocalPath);

        Assert.True(repo.TryBeginNetworkOperation(new NetworkOperationInfo(NetworkOperationOwner.Maintenance, "Lock cleanup"), out var held));
        using (held)
        {
            // Refused before it starts, so there is no idle tick to wait for — wait for the notice.
            _ = h.Runner; // the runner subscribes to the bus when it is first resolved
            h.Bus.RequestNetworkAction(RepoNetworkAction.Fetch);
            for (var i = 0; i < 250 && !h.Notify.Published.Any(); i++)
            {
                Dispatcher.UIThread.RunJobs();
                await Task.Delay(20);
            }
        }

        var info = Assert.Single(h.Notify.Published, n => n.Severity == NotificationSeverity.Info);
        Assert.Equal($"Lock cleanup on {repo.DisplayName} is still running.", info.Message);
        Assert.DoesNotContain(h.Notify.Published, n => n.Severity == NotificationSeverity.Success);
    }

    [AvaloniaFact]
    public async Task A_request_during_auto_fetch_waits_for_it_then_runs()
    {
        using var fixture = new PushFixture();
        await using var h = new Harness();
        var repo = await h.OpenAsync(fixture.LocalPath);

        Assert.True(repo.TryBeginNetworkOperation(new NetworkOperationInfo(NetworkOperationOwner.AutoFetch, "Auto-fetch"), out var autoFetch));
        var phases = new List<string?>();
        void OnProgress(object? s, RepoNetworkProgress p)
        {
            phases.Add(p.Progress?.Phase);
            if (p.Progress?.Phase == "Waiting for auto-fetch…")
            {
                Assert.Equal("Waiting for auto-fetch…", h.Runner.CurrentProgress(repo)?.Phase);
                autoFetch.Dispose();
            }
        }
        h.Runner.ProgressChanged += OnProgress;
        try
        {
            await h.RunToIdleAsync(() => h.Bus.RequestNetworkAction(RepoNetworkAction.Fetch));
        }
        finally
        {
            h.Runner.ProgressChanged -= OnProgress;
        }

        Assert.Equal("Waiting for auto-fetch…", phases[0]);
        Assert.Contains("Fetch…", phases);
        Assert.Single(h.Notify.Published, n => n.Severity == NotificationSeverity.Success);
        Assert.Null(h.Runner.CurrentProgress(repo));
        Assert.Null(repo.CurrentNetworkOperation);
    }

    [AvaloniaFact]
    public async Task Cancelling_while_waiting_for_auto_fetch_ends_silently_without_running()
    {
        using var fixture = new PushFixture();
        await using var h = new Harness();
        var repo = await h.OpenAsync(fixture.LocalPath);

        Assert.True(repo.TryBeginNetworkOperation(new NetworkOperationInfo(NetworkOperationOwner.AutoFetch, "Auto-fetch"), out var autoFetch));
        using (autoFetch)
        {
            void CancelWhileWaiting(object? s, RepoNetworkProgress p)
            {
                if (p.Progress?.Phase == "Waiting for auto-fetch…")
                    h.Runner.Cancel(repo);
            }
            h.Runner.ProgressChanged += CancelWhileWaiting;
            try
            {
                await h.RunToIdleAsync(() => h.Bus.RequestNetworkAction(RepoNetworkAction.Fetch));
            }
            finally
            {
                h.Runner.ProgressChanged -= CancelWhileWaiting;
            }

            Assert.Empty(h.Notify.Published);
            Assert.Equal("Auto-fetch", repo.CurrentNetworkOperation?.Label);
        }
    }

    [AvaloniaFact]
    public async Task A_request_pinned_to_a_repo_runs_there_even_when_another_is_focused()
    {
        using var diverged = new PushFixture().Diverge();
        using var other = new PushFixture();
        await using var h = new Harness();
        var target = await h.OpenAsync(diverged.LocalPath, focus: false);
        var focused = await h.OpenAsync(other.LocalPath);
        Assert.Same(focused, h.Workspace.Focused);

        await h.RunToIdleAsync(() => h.Bus.RequestNetworkAction(
            new RepoNetworkRequest(RepoNetworkAction.Push, null, null, Repo: target)));

        // Only the diverged repository's push is rejected; the focused one would have succeeded.
        Assert.Equal(1, h.Dialogs.ChooseCalls);
        Assert.DoesNotContain(h.Notify.Published, n => n.Severity == NotificationSeverity.Success);
    }

    [AvaloniaFact]
    public async Task Commit_and_push_requests_a_push_of_the_panels_own_repository()
    {
        using var fixture = new PushFixture();
        await using var h = new Harness();
        var ctx = await h.OpenAsync(fixture.LocalPath);
        await ctx.State.RefreshAllAsync();

        var request = StagingPanelView.BuildPushRequest(ctx);

        Assert.NotNull(request);
        Assert.Equal(RepoNetworkAction.Push, request!.Value.Action);
        Assert.Equal("origin", request.Value.Remote);
        Assert.Equal("main", request.Value.Branch);
        Assert.Same(ctx, request.Value.Repo);
    }
}
