namespace HistoriaApp.Tests;

[Trait("tier", "ui")]
public class StagingPanelDiscardTests : IDisposable
{
    private readonly TestRepoFixture _fixture;
    private readonly ServiceProvider _sp;
    private readonly IWorkspaceService _workspace;
    private readonly StubDialogService _dialogs;
    private RepositoryContext _context;

    public StagingPanelDiscardTests()
    {
        _fixture = new TestRepoFixture();
        _dialogs = new StubDialogService();
        _sp = CreateServices(_dialogs);
        _sp.GetRequiredService<AvaloniaThemeApplier>();
        _workspace = _sp.GetRequiredService<IWorkspaceService>();
        _context = _workspace.OpenAsync(_fixture.Path).GetAwaiter().GetResult();
        _context.State.RefreshRefsAsync().GetAwaiter().GetResult();
        _workspace.Focus(_context);
    }

    public void Dispose()
    {
        try { _context.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
        try { _sp.Dispose(); } catch { }
        try { _fixture.Dispose(); } catch { }
    }

    [AvaloniaFact]
    public async Task Staged_row_menu_has_no_discard_item()
    {
        await File.WriteAllTextAsync(System.IO.Path.Combine(_fixture.Path, "staged.txt"), "staged\n");
        await File.WriteAllTextAsync(System.IO.Path.Combine(_fixture.Path, "changes.txt"), "changes\n");
        await _context.Gateway.StageAsync(["staged.txt"]);
        await _context.State.RefreshStatusAsync();

        var panel = new StagingPanelView(_sp, _context);
        Dispatcher.UIThread.RunJobs();

        var stagedRow = Assert.Single(panel.StagedRowsForTesting.Children.OfType<Border>());
        var stagedMenu = stagedRow.ContextMenu;
        Assert.NotNull(stagedMenu);
        var stagedItems = ((IEnumerable<Control>)stagedMenu.ItemsSource!).OfType<MenuItem>().ToList();
        Assert.DoesNotContain(stagedItems, mi => mi.Header?.ToString() == "Discard changes");
        Assert.Contains(stagedItems, mi => mi.Header?.ToString() == "Open file location");
        Assert.Contains(stagedItems, mi => mi.Header?.ToString() == "Copy path");

        var changesRow = Assert.Single(panel.ChangesRowsForTesting.Children.OfType<Border>());
        var changesMenu = changesRow.ContextMenu;
        Assert.NotNull(changesMenu);
        var changesItems = ((IEnumerable<Control>)changesMenu.ItemsSource!).OfType<MenuItem>().ToList();
        Assert.Contains(changesItems, mi => mi.Header?.ToString() == "Discard changes");
    }

    [AvaloniaFact]
    public async Task Discard_all_sends_old_path_of_a_paired_rename()
    {
        var aPath = System.IO.Path.Combine(_fixture.Path, "a.txt");
        var bPath = System.IO.Path.Combine(_fixture.Path, "b.txt");
        await File.WriteAllTextAsync(aPath, "same content across move\n");
        await _context.Gateway.StageAsync(["a.txt"]);
        await _context.Gateway.CommitAsync(new CommitRequest
        {
            Message = "add a.txt",
            AuthorName = "Dev",
            AuthorEmail = "dev@example.com",
        });

        File.Move(aPath, bPath);
        await _context.State.RefreshStatusAsync();

        var status = _context.State.Status;
        Assert.Single(status.Unstaged);
        Assert.Equal("b.txt", status.Unstaged[0].Path);
        Assert.Equal("a.txt", status.Unstaged[0].OldPath);

        var panel = new StagingPanelView(_sp, _context);
        Dispatcher.UIThread.RunJobs();

        await panel.DiscardAllChangesForTestingAsync();
        Dispatcher.UIThread.RunJobs();

        await _context.State.RefreshStatusAsync();
        Assert.False(_context.State.Status.IsDirty);
        Assert.True(File.Exists(aPath));
        Assert.False(File.Exists(bPath));
        Assert.Equal("same content across move", (await File.ReadAllTextAsync(aPath)).Trim());
    }

    private static ServiceProvider CreateServices(IDialogService dialogs)
    {
        var sc = new ServiceCollection();
        var settings = SettingsService.Load();
        sc.AddSingleton<ISettingsService>(settings);
        sc.AddSingleton<SettingsService>(settings);
        sc.AddSingleton<IUiDispatcher, UiDispatcher>();
        sc.AddSingleton<IDialogService>(dialogs);
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

    private sealed class TestRepoFixture : IDisposable
    {
        public string Path { get; private set; }

        public TestRepoFixture()
        {
            Path = NewPath();
            RepoTemplates.Initial(Path);
        }

        private static string NewPath() =>
            TestScratch.NewPath("historia-staging-discard-tests-");

        public void Dispose() => RepoTemplates.Delete(Path);
    }
}
