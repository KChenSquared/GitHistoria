namespace HistoriaApp.Tests;

[Trait("tier", "ui")]
public class RepositoryViewOpBannerTests : IDisposable
{
    private readonly ConflictResolverViewTests.TestRepoFixture _fixture;
    private readonly ServiceProvider _sp;
    private readonly RepositoryContext _context;
    private readonly OpBannerFakeConflictService _conflicts = new();
    private readonly ConflictResolverViewTests.FakeDialogService _dialogs = new();

    public RepositoryViewOpBannerTests()
    {
        _fixture = new ConflictResolverViewTests.TestRepoFixture();
        var sc = new ServiceCollection();
        sc.AddSingleton<IConflictService>(_conflicts);
        sc.AddSingleton<IDialogService>(_dialogs);
        sc.AddSingleton<INotificationService, NotificationService>();
        sc.AddSingleton<IWorkspaceService, WorkspaceService>();
        sc.AddSingleton<IRepoLayoutService, RepoLayoutService>();
        sc.AddSingleton<ISettingsService>(new ConflictResolverViewTests.FakeSettingsService());
        sc.AddSingleton<IStashService, StashService>();
        sc.AddSingleton<IRepoNetworkActionRunner, ConflictResolverViewTests.FakeNetworkRunner>();
        sc.AddSingleton<IGitCliService, GitCliService>();
        var locator = new GitExecutableLocator();
        locator.ResolveAsync(null).GetAwaiter().GetResult();
        sc.AddSingleton<IGitExecutableLocator>(locator);
        sc.AddSingleton<IKeyboardCommandRouter, KeyboardCommandRouter>();
        sc.AddSingleton<IExternalLauncher, ExternalLauncher>();
        sc.AddSingleton<IProfileService, ProfileService>();
        sc.AddSingleton<IRecentRepositoryService, RecentRepositoryService>();
        sc.AddSingleton<IRepoIdentityService, RepoIdentityService>();
        sc.AddSingleton<ICredentialVaultService, ConflictResolverViewTests.FakeVault>();
        sc.AddSingleton<IUiDispatcher, Historia.Platform.UiDispatcher>();
        sc.AddSingleton<LibGit2Service>();
        sc.AddSingleton<IBackgroundWorkGate, BackgroundWorkGate>();
        sc.AddSingleton<IRepoStateCache, RepoStateCache>();
        sc.AddSingleton<IGraphLayoutService, GraphLayoutService>();
        _sp = sc.BuildServiceProvider();

        var ws = _sp.GetRequiredService<IWorkspaceService>();
        _context = ws.OpenAsync(_fixture.Path).GetAwaiter().GetResult();
        ws.Focus(_context);
    }

    public void Dispose()
    {
        try { _context.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
        try { _sp.Dispose(); } catch { }
        try { _fixture.Dispose(); } catch { }
    }

    [AvaloniaFact]
    public async Task Continue_while_running_is_ignored_and_buttons_disabled()
    {
        _fixture.SetOperation(OperationState.Merging);
        await _context.State.RefreshStatusAsync();
        Dispatcher.UIThread.RunJobs();

        var repoView = new RepositoryView(_sp);
        var window = new Window { Width = 800, Height = 600, Content = repoView };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        try
        {
            Assert.True(repoView.OpBoxForTesting.IsVisible);
            Assert.True(repoView.OpContinueButtonForTesting.IsEnabled);
            Assert.True(repoView.OpAbortButtonForTesting.IsEnabled);

            var tcs = new TaskCompletionSource<bool>();
            _conflicts.ContinueTcs = tcs;

            repoView.OpContinueButtonForTesting.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();

            Assert.False(repoView.OpContinueButtonForTesting.IsEnabled);
            Assert.False(repoView.OpAbortButtonForTesting.IsEnabled);
            Assert.Equal(1, _conflicts.ContinueCalls);

            // Second click while busy
            repoView.OpContinueButtonForTesting.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(1, _conflicts.ContinueCalls);

            // Release running operation
            tcs.SetResult(true);
            Dispatcher.UIThread.RunJobs();

            Assert.True(repoView.OpContinueButtonForTesting.IsEnabled);
            Assert.True(repoView.OpAbortButtonForTesting.IsEnabled);
            Assert.Equal(1, _conflicts.ContinueCalls);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public async Task Abort_confirms_before_calling_service()
    {
        _fixture.SetOperation(OperationState.Merging);
        await _context.State.RefreshStatusAsync();
        Dispatcher.UIThread.RunJobs();

        var repoView = new RepositoryView(_sp);
        var window = new Window { Width = 800, Height = 600, Content = repoView };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        try
        {
            Assert.True(repoView.OpBoxForTesting.IsVisible);

            // User cancels destructive confirmation -> no abort call
            _dialogs.ConfirmDestructiveResult = false;
            repoView.OpAbortButtonForTesting.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();

            Assert.Single(_dialogs.ConfirmDestructiveCalls);
            Assert.Equal(0, _conflicts.AbortCalls);

            // User confirms -> abort called
            _dialogs.ConfirmDestructiveResult = true;
            repoView.OpAbortButtonForTesting.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(2, _dialogs.ConfirmDestructiveCalls.Count);
            Assert.Equal(1, _conflicts.AbortCalls);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    private sealed class OpBannerFakeConflictService : IConflictService
    {
        public TaskCompletionSource<bool>? ContinueTcs { get; set; }
        public int ContinueCalls { get; set; }
        public int AbortCalls { get; set; }

        public async Task ContinueOperationAsync(RepositoryContext context, CancellationToken ct = default)
        {
            ContinueCalls++;
            if (ContinueTcs != null)
                await ContinueTcs.Task;
        }

        public Task AbortOperationAsync(RepositoryContext context, CancellationToken ct = default)
        {
            AbortCalls++;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ConflictFile>> GetConflictsAsync(RepositoryContext context, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ConflictFile>>([]);

        public Task<ConflictWorkingCopy> LoadWorkingCopyAsync(RepositoryContext context, string filePath, CancellationToken ct = default)
            => Task.FromResult(new ConflictWorkingCopy { Path = filePath, DiskHash = "" });

        public Task<string> GetDiskHashAsync(RepositoryContext context, string filePath, CancellationToken ct = default)
            => Task.FromResult("");

        public Task<ResolutionSaveResult> SaveResolutionAsync(RepositoryContext context, string filePath, string text, TextFileFormat format, string expectedDiskHash, bool stage, CancellationToken ct = default)
            => Task.FromResult(new ResolutionSaveResult { Saved = true, DiskHash = "" });

        public Task ResolveWithSideAsync(RepositoryContext context, string filePath, ConflictSide side, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task RecreateConflictAsync(RepositoryContext context, string filePath, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<ConflictSideLabels> GetSideLabelsAsync(RepositoryContext context, CancellationToken ct = default)
            => Task.FromResult(new ConflictSideLabels { OursTitle = "Ours", TheirsTitle = "Theirs", OursDetail = "", TheirsDetail = "", Operation = OperationState.Merging, StopId = "" });

        public Task<bool> LaunchExternalMergeToolAsync(RepositoryContext context, string filePath, CancellationToken ct = default)
            => Task.FromResult(true);

        public Task SkipCurrentAsync(RepositoryContext context, CancellationToken ct = default)
            => Task.CompletedTask;
    }
}
