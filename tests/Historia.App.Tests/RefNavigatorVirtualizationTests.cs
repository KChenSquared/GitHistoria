namespace HistoriaApp.Tests;

/// <summary>
/// The ref navigator virtualizes its rows: a repository with thousands of branches, tags and
/// remote branches realizes only the rows in view, and scrolling realizes others in their place.
/// </summary>
[Trait("tier", "ui")]
public sealed class RefNavigatorVirtualizationTests
{
    private const int Refs = 3000;

    [AvaloniaFact]
    public void Thousands_of_refs_realize_only_the_rows_in_view()
    {
        using var host = new RefNavigatorTestHost(bulkRefs: Refs);
        host.ExpandAllSections();

        var vm = host.View.ViewModel;
        Assert.Equal($"Local Branches ({Refs + 1})", vm.Local.Title);
        Assert.Equal(Refs + 1, vm.Local.Rows.Count);
        Assert.Equal($"Tags ({Refs})", vm.Tags.Title);
        Assert.Equal(Refs, vm.Tags.Rows.Count);
        Assert.True(vm.Remotes.Rows.Count > Refs);

        var containers = host.View.GetVisualDescendants().OfType<ListBoxItem>().Count();
        Assert.InRange(containers, 1, 199);
        Assert.All(host.SectionLists(), list =>
            Assert.True(list.GetVisualDescendants().OfType<ListBoxItem>().Count() < 40,
                "each section's list realizes only its few visible rows"));
    }

    [AvaloniaFact]
    public void Scrolling_to_the_end_realizes_the_last_rows_and_still_only_a_few()
    {
        using var host = new RefNavigatorTestHost(bulkRefs: Refs);

        var local = host.SectionLists()[0];
        var last = host.View.ViewModel.Local.Rows.Count - 1;
        local.ScrollIntoView(last);
        host.Settle();

        var texts = local.GetVisualDescendants().OfType<ListBoxItem>()
            .SelectMany(i => i.GetVisualDescendants().OfType<TextBlock>())
            .Select(t => t.Text)
            .ToList();
        Assert.Contains($"branch-{Refs:D4}", texts);
        Assert.True(host.View.GetVisualDescendants().OfType<ListBoxItem>().Count() < 200);
    }

    [AvaloniaFact]
    public void Collapsing_a_big_section_drops_its_rows_and_containers()
    {
        using var host = new RefNavigatorTestHost(bulkRefs: Refs);
        var vm = host.View.ViewModel;

        vm.Local.ToggleCommand.Execute(null);
        host.Settle();

        Assert.Empty(vm.Local.Rows);
        Assert.Equal($"Local Branches ({Refs + 1})", vm.Local.Title);
        Assert.Empty(host.SectionLists()[0].GetVisualDescendants().OfType<ListBoxItem>());
    }
}

/// <summary>A shown <see cref="RefNavigatorView"/> over a real, tiny repository with the services it
/// needs. <c>bulkRefs</c> adds that many local branches, tags and remote branches through
/// <c>packed-refs</c>, which is far faster than creating them one by one.</summary>
internal sealed class RefNavigatorTestHost : IDisposable
{
    private readonly string _path;
    private readonly ServiceProvider _sp;
    private readonly RepositoryContext _context;
    private readonly Window _window;

    public RefNavigatorTestHost(
        int bulkRefs = 0,
        Action<Func<string[], string>>? configureRepo = null,
        Action<IServiceCollection>? configureServices = null)
    {
        _path = TestScratch.NewDirectory("historia-refnav-host-");
        string Run(params string[] args) => RunGit(_path, args);

        Run("init", "-b", "main");
        Run("config", "user.name", "Test");
        Run("config", "user.email", "test@example.com");
        Run("config", "commit.gpgsign", "false");
        File.WriteAllText(System.IO.Path.Combine(_path, "f.txt"), "init\n");
        Run("add", ".");
        Run("commit", "-m", "c1");
        Run("remote", "add", "origin", "https://example.com/repo.git");
        if (bulkRefs > 0)
            WritePackedRefs(Run("rev-parse", "HEAD").Trim(), bulkRefs);
        configureRepo?.Invoke(Run);

        var sc = new ServiceCollection();
        AddServices(sc);
        configureServices?.Invoke(sc);
        _sp = sc.BuildServiceProvider();

        var ws = _sp.GetRequiredService<IWorkspaceService>();
        _context = ws.OpenAsync(_path).GetAwaiter().GetResult();
        _context.MarkFirstGraphPaintDone();
        _context.State.RefreshRefsAsync().GetAwaiter().GetResult();
        ws.Focus(_context);
        View = new RefNavigatorView(_sp);
        _window = new Window { Width = 300, Height = 800, Content = View };
        _window.Show();
        Dispatcher.UIThread.RunJobs();
    }

    public RefNavigatorView View { get; }

    public Window Window => _window;

    public RepositoryContext Context => _context;

    public IServiceProvider Services => _sp;

    /// <summary>Runs git in the repository and returns what it printed.</summary>
    public string Git(params string[] args) => RunGit(_path, args);

    /// <summary>Lets the pending layout and render work finish, so virtualized rows are realized.</summary>
    public void Settle()
    {
        Dispatcher.UIThread.RunJobs();
        _window.UpdateLayout();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Opens the Tags section too (Local Branches and Remotes start open).</summary>
    public void ExpandAllSections()
    {
        View.ViewModel.Tags.ToggleCommand.Execute(null);
        Settle();
    }

    /// <summary>The section header borders, top to bottom.</summary>
    public IReadOnlyList<Border> SectionHeaders() =>
        View.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("section-header")).ToList();

    /// <summary>Each section's row list, top to bottom.</summary>
    public IReadOnlyList<ListBox> SectionLists() =>
        [.. SectionHeaders().Select(h => h.GetVisualAncestors().OfType<StackPanel>().First().Children.OfType<ListBox>().Single())];

    /// <summary>The root border of every realized row in <paramref name="list"/>, top to bottom.</summary>
    public static IReadOnlyList<Border> Rows(ListBox list) =>
        [.. list.GetVisualDescendants().OfType<ListBoxItem>()
            .OrderBy(i => i.Bounds.Y)
            .Select(i => (Border)i.Presenter!.Child!)];

    public void Dispose()
    {
        try { _window.Close(); } catch { }
        try { View.Dispose(); } catch { }
        try { _context.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
        try { _sp.Dispose(); } catch { }
        try { TestScratch.Delete(_path); } catch { }
    }

    private void WritePackedRefs(string sha, int count)
    {
        var lines = new List<string> { "# pack-refs with: peeled fully-peeled sorted " };
        for (var i = 1; i <= count; i++)
            lines.Add($"{sha} refs/heads/branch-{i:D4}");
        for (var i = 1; i <= count; i++)
            lines.Add($"{sha} refs/remotes/origin/remote-{i:D4}");
        for (var i = 1; i <= count; i++)
            lines.Add($"{sha} refs/tags/tag-{i:D4}");
        File.WriteAllText(System.IO.Path.Combine(_path, ".git", "packed-refs"), string.Join('\n', lines) + "\n");
    }

    private static string RunGit(string dir, string[] args)
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
        using var p = System.Diagnostics.Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        p.WaitForExit(30_000);
        Task.WaitAll(stdout, stderr);
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed ({p.ExitCode}): {stderr.Result}");
        return stdout.Result;
    }

    private static void AddServices(IServiceCollection sc)
    {
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
        sc.AddSingleton<ISystemThemeProbe, HostThemeProbe>();
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
        sc.AddSingleton<ICredentialVaultService, HostVault>();
        sc.AddSingleton<IExternalLauncher, ExternalLauncher>();
    }

    private sealed class HostThemeProbe : ISystemThemeProbe
    {
        public bool IsLightTheme => false;
        public bool IsHighContrast => false;
#pragma warning disable CS0067
        public event EventHandler? Changed;
#pragma warning restore CS0067
    }

    private sealed class HostVault : ICredentialVaultService
    {
        public Task StoreTokenAsync(string key, string token, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> RetrieveTokenAsync(string key, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task DeleteTokenAsync(string key, CancellationToken ct = default) => Task.CompletedTask;
    }
}
