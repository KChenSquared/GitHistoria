namespace HistoriaApp.Tests;

/// <summary>
/// Both file lists are virtualized: with thousands of changed files only the rows in view have
/// containers, and a refresh that swaps the list keeps the scroll position.
/// </summary>
[Trait("tier", "ui")]
public class StagingPanelVirtualizationTests : IDisposable
{
    private const int FileCount = 5000;

    private readonly string _repoPath;
    private readonly ServiceProvider _sp;
    private readonly RepositoryContext _context;
    private readonly List<Window> _windows = [];
    private readonly List<StagingPanelView> _views = [];

    public StagingPanelVirtualizationTests()
    {
        _repoPath = TestScratch.NewPath("historia-staging-virtualization-");
        RepoTemplates.Initial(_repoPath);

        _sp = CreateServices();
        _sp.GetRequiredService<AvaloniaThemeApplier>();
        var workspace = _sp.GetRequiredService<IWorkspaceService>();
        _context = workspace.OpenAsync(_repoPath).GetAwaiter().GetResult();
        workspace.Focus(_context);
    }

    public void Dispose()
    {
        foreach (var window in _windows)
            try { window.Close(); } catch { }
        foreach (var view in _views)
            try { view.Dispose(); } catch { }
        try { _context.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
        try { _sp.Dispose(); } catch { }
        try { RepoTemplates.Delete(_repoPath); } catch { }
    }

    private void WriteFiles(int count, string prefix = "file")
    {
        for (var i = 0; i < count; i++)
            File.WriteAllText(System.IO.Path.Combine(_repoPath, $"{prefix}{i:D4}.txt"), $"content {i}\n");
    }

    private StagingPanelView Show()
    {
        var panel = new StagingPanelView(_sp, _context);
        _views.Add(panel);
        var window = new Window { Width = 800, Height = 900, Content = panel };
        _windows.Add(window);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return panel;
    }

    private static int RealizedRows(ListBox list) => list.GetVisualDescendants().OfType<ListBoxItem>().Count();

    [AvaloniaFact]
    public async Task Five_thousand_changed_files_realize_fewer_than_200_row_containers()
    {
        WriteFiles(FileCount);
        await _context.State.RefreshStatusAsync();
        var panel = Show();

        Assert.Equal(FileCount, panel.ViewModel.Changes.Count);
        Assert.Equal($"Changes ({FileCount})", panel.ChangesHeaderText.Text);
        var realized = RealizedRows(panel.ChangesList);
        Assert.InRange(realized, 1, 199);
    }

    [AvaloniaFact]
    public async Task Five_thousand_staged_files_realize_fewer_than_200_row_containers()
    {
        WriteFiles(FileCount);
        RepoTemplates.Git(_repoPath, "add", ".");
        await _context.State.RefreshStatusAsync();
        var panel = Show();

        Assert.Equal(FileCount, panel.ViewModel.Staged.Count);
        Assert.Equal($"Staged ({FileCount})", panel.StagedHeaderText.Text);
        var realized = RealizedRows(panel.StagedList);
        Assert.InRange(realized, 1, 199);
    }

    [AvaloniaFact]
    public async Task Rows_scrolled_into_view_are_realized_as_the_list_scrolls()
    {
        WriteFiles(FileCount);
        await _context.State.RefreshStatusAsync();
        var panel = Show();
        var scroller = panel.ChangesList.GetVisualDescendants().OfType<ScrollViewer>().First();

        scroller.Offset = new Vector(0, 25 * 3000);
        Dispatcher.UIThread.RunJobs();

        var texts = panel.ChangesList.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
        Assert.Contains("file3000.txt", texts);
        Assert.DoesNotContain("file0000.txt", texts);
        Assert.InRange(RealizedRows(panel.ChangesList), 1, 199);
    }

    [AvaloniaFact]
    public async Task A_status_refresh_keeps_the_scroll_position_of_the_lists()
    {
        WriteFiles(FileCount);
        await _context.State.RefreshStatusAsync();
        var panel = Show();
        var scroller = panel.ChangesList.GetVisualDescendants().OfType<ScrollViewer>().First();
        scroller.Offset = new Vector(0, 25 * 2000);
        Dispatcher.UIThread.RunJobs();
        var before = scroller.Offset.Y;
        Assert.True(before > 0);

        File.WriteAllText(System.IO.Path.Combine(_repoPath, "zzz-new.txt"), "new\n");
        // A refresh that joins one the file watcher already started can answer with an older
        // snapshot, so refresh until the new file shows.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (panel.ViewModel.Changes.Count != FileCount + 1 && DateTime.UtcNow < deadline)
        {
            await _context.State.RefreshStatusAsync();
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(50);
        }

        Assert.Equal(FileCount + 1, panel.ViewModel.Changes.Count);
        Assert.Equal(before, scroller.Offset.Y);
    }

    private static ServiceProvider CreateServices()
    {
        var sc = new ServiceCollection();
        var settings = SettingsService.Load();
        sc.AddSingleton<ISettingsService>(settings);
        sc.AddSingleton<SettingsService>(settings);
        sc.AddSingleton<IUiDispatcher, UiDispatcher>();
        sc.AddSingleton<IDialogService, StubDialogService>();
        sc.AddSingleton<IScrollDirectionProbe, ScrollDirectionProbe>();
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
        sc.AddSingleton<AvaloniaThemeApplier>();
        return sc.BuildServiceProvider();
    }

    private sealed class StubDialogService : IDialogService
    {
        public Task<string?> PickFolderAsync(string title, string? initialDirectory = null) => Task.FromResult<string?>(null);
        public Task<string?> PickFileAsync(string title, string filter, string? initialDirectory = null) => Task.FromResult<string?>(null);
        public Task<bool> ConfirmAsync(string title, string message, bool destructive = false, string confirmLabel = "OK", bool defaultToCancel = false, DialogIcon icon = DialogIcon.None) => Task.FromResult(true);
        public Task<bool> ConfirmDestructiveAsync(string title, string effect, string confirmLabel) => Task.FromResult(true);
        public Task ShowMessageAsync(string title, string message) => Task.CompletedTask;
        public Task<string?> PromptAsync(string title, string message, string? defaultValue = null, string confirmLabel = "OK", bool primaryConfirm = false) => Task.FromResult<string?>(null);
        public Task<int> ChooseAsync(string title, string message, IReadOnlyList<string> options, int defaultIndex = 0, int destructiveIndex = -1, DialogIcon icon = DialogIcon.None) => Task.FromResult(0);
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
}
