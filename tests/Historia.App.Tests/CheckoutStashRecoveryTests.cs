namespace HistoriaApp.Tests;

/// <summary>
/// Verify BranchMenu.RunWithStashRecovery integration when a checkout conflict occurs —
/// ensures the classified UncommittedChanges error carries a "Stash &amp; retry" action that
/// successfully creates and switches to the branch.
/// </summary>
[Trait("tier", "ui")]
public sealed class CheckoutStashRecoveryTests
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

    private sealed class TestRepoFixture : IDisposable
    {
        public string Path { get; }

        public TestRepoFixture()
        {
            Path = TestScratch.NewDirectory("historia-stashrecovery-");
            RunGit(Path, "init", "-b", "main");
            RunGit(Path, "config", "user.name", "Test");
            RunGit(Path, "config", "user.email", "test@example.com");
            RunGit(Path, "config", "commit.gpgsign", "false");
        }

        public string CommitWithChange(string message, string file, string content)
        {
            WriteFile(file, content);
            RunGit(Path, "add", file);
            RunGit(Path, "commit", "-m", message, "--no-gpg-sign");
            return RunGit(Path, "rev-parse", "HEAD").Trim();
        }

        public void WriteFile(string file, string content)
        {
            var full = System.IO.Path.Combine(Path, file);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }

        private static string RunGit(string dir, params string[] args)
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
            var stdout = p.StandardOutput.ReadToEnd();
            var stderr = p.StandardError.ReadToEnd();
            p.WaitForExit(30_000);
            if (p.ExitCode != 0)
                throw new InvalidOperationException($"git failed ({p.ExitCode}): {stderr}");
            return stdout;
        }

        public void Dispose() => TestScratch.Delete(Path);
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
    public async Task Create_branch_conflict_offers_stash_and_retry()
    {
        using var fixture = new TestRepoFixture();
        var c1 = fixture.CommitWithChange("c1", "other.txt", "c1\n");
        fixture.CommitWithChange("c2", "other.txt", "c2\n");
        fixture.WriteFile("other.txt", "c2 edited\n");

        var notify = new CapturingNotificationService();
        var sp = CreateServices(notify);
        try
        {
            var ws = sp.GetRequiredService<IWorkspaceService>();
            var ctx = await ws.OpenAsync(fixture.Path);
            try
            {
                ctx.MarkFirstGraphPaintDone();
                ctx.MarkFirstFullLoadDone();
                await ctx.State.RefreshRefsAsync();

                var stash = sp.GetRequiredService<IStashService>();
                await BranchMenu.RunWithStashRecovery(
                    ctx, notify, stash,
                    () => ctx.Gateway.CreateBranchAsync("test-conflict", c1, checkout: true),
                    "Create branch");

                var n = Assert.Single(notify.Published);
                Assert.Equal(NotificationSeverity.Error, n.Severity);
                var action = Assert.Single(n.Actions);
                Assert.Equal("Stash & retry", action.Label);
                Assert.DoesNotContain("LibGit2Sharp", n.Message);
            }
            finally
            {
                await ws.CloseAsync(ctx);
            }
        }
        finally
        {
            await sp.DisposeAsync();
        }
    }

    [AvaloniaFact]
    public async Task Stash_and_retry_creates_the_branch()
    {
        using var fixture = new TestRepoFixture();
        var c1 = fixture.CommitWithChange("c1", "other.txt", "c1\n");
        fixture.CommitWithChange("c2", "other.txt", "c2\n");
        fixture.WriteFile("other.txt", "c2 edited\n");

        var notify = new CapturingNotificationService();
        var sp = CreateServices(notify);
        try
        {
            var ws = sp.GetRequiredService<IWorkspaceService>();
            var ctx = await ws.OpenAsync(fixture.Path);
            try
            {
                ctx.MarkFirstGraphPaintDone();
                ctx.MarkFirstFullLoadDone();
                await ctx.State.RefreshRefsAsync();

                var stash = sp.GetRequiredService<IStashService>();
                await BranchMenu.RunWithStashRecovery(
                    ctx, notify, stash,
                    () => ctx.Gateway.CreateBranchAsync("test-conflict", c1, checkout: true),
                    "Create branch");

                var n = Assert.Single(notify.Published);
                var action = Assert.Single(n.Actions);

                await action.Invoke();

                using (var r = new Repository(ctx.RepositoryPath))
                {
                    Assert.Equal("test-conflict", r.Head.FriendlyName);
                    Assert.Equal(c1, r.Head.Tip.Sha);
                }

                var stashes = await stash.ListAsync(ctx, ctx.Lifetime);
                Assert.Contains(stashes, s => s.Message.Contains("Historia: before Create branch"));
            }
            finally
            {
                await ws.CloseAsync(ctx);
            }
        }
        finally
        {
            await sp.DisposeAsync();
        }
    }
}
