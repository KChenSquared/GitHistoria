namespace HistoriaApp.Tests;

/// <summary>
/// The sidebar's REMOTES context menu: remote branch rows offer Delete… between Merge and Copy branch name,
/// clicking it requests a targeted DeleteRemoteBranch network action on the bus, slash-named remotes pass
/// the short branch name, origin/HEAD and unknown aliases have no Delete…, and local rows are unchanged.
/// </summary>
[Trait("tier", "ui")]
public sealed class SidebarRemoteBranchMenuTests
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
            Path = TestScratch.NewDirectory("historia-sidebar-remotemenu-");
            RunGit("init");
            RunGit("config", "user.name", "Test");
            RunGit("config", "user.email", "test@example.com");
            RunGit("config", "commit.gpgsign", "false");
            File.WriteAllText(System.IO.Path.Combine(Path, "f.txt"), "init\n");
            RunGit("add", ".");
            RunGit("commit", "-m", "init");
            RunGit("remote", "add", "origin", "https://example.com/repo.git");
            RunGit("update-ref", "refs/remotes/origin/main", "HEAD");
            RunGit("update-ref", "refs/remotes/origin/feat", "HEAD");
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

        public void Dispose() => TestScratch.Delete(Path);
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

    private sealed class Harness : IAsyncDisposable
    {
        public ServiceProvider Services { get; }
        public IWorkspaceService Workspace => Services.GetRequiredService<IWorkspaceService>();
        public IRepoActionBus Bus => Services.GetRequiredService<IRepoActionBus>();
        public IDialogService Dialogs => Services.GetRequiredService<IDialogService>();
        public INotificationService Notify => Services.GetRequiredService<INotificationService>();
        public IStashService Stash => Services.GetRequiredService<IStashService>();

        public Harness() => Services = CreateServices();

        public async Task<RepositoryContext> OpenAsync(string path)
        {
            var ctx = await Workspace.OpenAsync(path);
            ctx.MarkFirstGraphPaintDone();
            ctx.MarkFirstFullLoadDone();
            await ctx.State.RefreshRefsAsync();
            return ctx;
        }

        public async ValueTask DisposeAsync() => await Services.DisposeAsync();
    }

    private static Control ItemWithHeader(IEnumerable<Control> items, string header) =>
        items.First(i => i is MenuItem m && m.Header?.ToString() == header);

    private static void Click(Control item) =>
        ((MenuItem)item).RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));

    [AvaloniaFact]
    public async Task Remote_row_menu_has_delete_between_merge_and_copy()
    {
        using var fixture = new RepoFixture();
        await using var h = new Harness();
        var ctx = await h.OpenAsync(fixture.Path);

        var target = new BranchMenu.Target(
            Name: "feat",
            CopyName: "origin/feat",
            TrackingRef: "origin/feat",
            IsLocal: false,
            TipSha: "1234567890abcdef",
            CurrentBranch: "main",
            CounterpartRef: null,
            RemoteAlias: "origin");

        var items = BranchMenu.Build(target, ctx, h.Dialogs, h.Notify, h.Stash, h.Bus, _ => { });

        var headers = items.OfType<MenuItem>().Select(m => m.Header?.ToString()).ToList();
        var expected = new[]
        {
            "Checkout",
            "Create branch here…",
            "Create tag here…",
            "Merge into main branch",
            "Delete…",
            "Copy branch name",
        };
        Assert.Equal(expected, headers);
    }

    [AvaloniaFact]
    public async Task Delete_requests_a_targeted_remote_branch_delete()
    {
        using var fixture = new RepoFixture();
        await using var h = new Harness();
        var ctx = await h.OpenAsync(fixture.Path);

        var target = new BranchMenu.Target(
            Name: "feat",
            CopyName: "origin/feat",
            TrackingRef: "origin/feat",
            IsLocal: false,
            TipSha: "1234567890abcdef",
            CurrentBranch: "main",
            CounterpartRef: null,
            RemoteAlias: "origin");

        var requests = new List<RepoNetworkRequest>();
        h.Bus.NetworkActionRequested += (_, req) => requests.Add(req);

        var items = BranchMenu.Build(target, ctx, h.Dialogs, h.Notify, h.Stash, h.Bus, _ => { });
        var deleteItem = ItemWithHeader(items, "Delete…");
        Click(deleteItem);

        var req = Assert.Single(requests);
        Assert.Equal(RepoNetworkAction.DeleteRemoteBranch, req.Action);
        Assert.Equal("origin", req.Remote);
        Assert.Null(req.Branch);
        Assert.Equal("feat", req.RemoteBranch);
        Assert.Same(ctx, req.Repo);
    }

    [AvaloniaFact]
    public async Task Delete_on_a_slash_named_remote_passes_the_short_branch()
    {
        using var fixture = new RepoFixture();
        await using var h = new Harness();
        var ctx = await h.OpenAsync(fixture.Path);

        var target = new BranchMenu.Target(
            Name: "feat",
            CopyName: "team/fork/feat",
            TrackingRef: "team/fork/feat",
            IsLocal: false,
            TipSha: "1234567890abcdef",
            CurrentBranch: "main",
            CounterpartRef: null,
            RemoteAlias: "team/fork");

        var requests = new List<RepoNetworkRequest>();
        h.Bus.NetworkActionRequested += (_, req) => requests.Add(req);

        var items = BranchMenu.Build(target, ctx, h.Dialogs, h.Notify, h.Stash, h.Bus, _ => { });
        var deleteItem = ItemWithHeader(items, "Delete…");
        Click(deleteItem);

        var req = Assert.Single(requests);
        Assert.Equal(RepoNetworkAction.DeleteRemoteBranch, req.Action);
        Assert.Equal("team/fork", req.Remote);
        Assert.Null(req.Branch);
        Assert.Equal("feat", req.RemoteBranch);
        Assert.Same(ctx, req.Repo);
    }

    [AvaloniaFact]
    public async Task Remote_HEAD_row_has_no_delete()
    {
        using var fixture = new RepoFixture();
        await using var h = new Harness();
        var ctx = await h.OpenAsync(fixture.Path);

        var target = new BranchMenu.Target(
            Name: "HEAD",
            CopyName: "origin/HEAD",
            TrackingRef: "origin/HEAD",
            IsLocal: false,
            TipSha: "1234567890abcdef",
            CurrentBranch: "main",
            CounterpartRef: null,
            RemoteAlias: "origin");

        var items = BranchMenu.Build(target, ctx, h.Dialogs, h.Notify, h.Stash, h.Bus, _ => { });
        Assert.DoesNotContain(items.OfType<MenuItem>(), m => m.Header?.ToString() == "Delete…");
    }

    [AvaloniaFact]
    public async Task Unknown_alias_has_no_delete()
    {
        using var fixture = new RepoFixture();
        await using var h = new Harness();
        var ctx = await h.OpenAsync(fixture.Path);

        var target = new BranchMenu.Target(
            Name: "feat",
            CopyName: "feat",
            TrackingRef: "feat",
            IsLocal: false,
            TipSha: "1234567890abcdef",
            CurrentBranch: "main",
            CounterpartRef: null,
            RemoteAlias: null);

        var items = BranchMenu.Build(target, ctx, h.Dialogs, h.Notify, h.Stash, h.Bus, _ => { });
        Assert.DoesNotContain(items.OfType<MenuItem>(), m => m.Header?.ToString() == "Delete…");
    }

    [AvaloniaFact]
    public async Task Local_row_menu_is_unchanged()
    {
        using var fixture = new RepoFixture();
        await using var h = new Harness();
        var ctx = await h.OpenAsync(fixture.Path);

        var target = new BranchMenu.Target(
            Name: "feat",
            CopyName: "feat",
            TrackingRef: "feat",
            IsLocal: true,
            TipSha: "1234567890abcdef",
            CurrentBranch: "main",
            CounterpartRef: null,
            RemoteAlias: null);

        var requests = new List<RepoNetworkRequest>();
        h.Bus.NetworkActionRequested += (_, req) => requests.Add(req);

        var items = BranchMenu.Build(target, ctx, h.Dialogs, h.Notify, h.Stash, h.Bus, _ => { });

        Assert.Contains(items.OfType<MenuItem>(), m => m.Header?.ToString() == "Rename…");
        Assert.Contains(items.OfType<MenuItem>(), m => m.Header?.ToString() == "Delete…");
        Assert.Empty(requests);
    }
}
