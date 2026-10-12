namespace HistoriaApp.Tests;

[Trait("tier", "ui")]
public class StagingPanelConflictAbortTests : IDisposable
{
    private readonly ConflictResolverViewTests.TestRepoFixture _fixture;
    private readonly ServiceProvider _sp;
    private readonly RepositoryContext _context;
    private readonly AbortFakeConflictService _conflicts = new();
    private readonly ConflictResolverViewTests.FakeDialogService _dialogs = new();
    private readonly UnsavedWorkRegistry _unsavedWork = new();

    public StagingPanelConflictAbortTests()
    {
        _fixture = new ConflictResolverViewTests.TestRepoFixture();
        var sc = new ServiceCollection();
        sc.AddSingleton<IConflictService>(_conflicts);
        sc.AddSingleton<IDialogService>(_dialogs);
        sc.AddSingleton<IRepoActionBus, RepoActionBus>();
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
        sc.AddSingleton<IBackgroundWorkGate, BackgroundWorkGate>();
        sc.AddSingleton<IRepoStateCache, RepoStateCache>();
        sc.AddSingleton<IGraphLayoutService, GraphLayoutService>();
        sc.AddSingleton<IUnsavedWorkRegistry>(_unsavedWork);
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

    /// <summary>Refreshes the tab's status and runs posted UI work until <paramref name="condition"/>
    /// holds, for at most 10 seconds. After a git command on the open repository, a refresh that
    /// joins one its file watcher already started can return a snapshot taken before the command.</summary>
    private async Task RefreshUntilAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (true)
        {
            await _context.State.RefreshStatusAsync();
            Dispatcher.UIThread.RunJobs();
            if (condition())
                return;
            Assert.True(DateTime.UtcNow < deadline, $"Timed out waiting for {what}.");
            await Task.Delay(50);
        }
    }

    private async Task<(StagingPanelView Panel, Window Window)> ShowPanelAsync(OperationState operation = OperationState.Merging)
    {
        _fixture.CreateConflict();
        if (operation != OperationState.Merging)
            _fixture.SetOperation(operation);
        await RefreshUntilAsync(() => _context.State.Status.Conflicted.Count > 0
            && _context.State.PendingOperation == operation, $"a conflict during {operation}");

        var panel = new StagingPanelView(_sp, _context);
        var window = new Window { Width = 800, Height = 600, Content = panel };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (panel, window);
    }

    private static Button AbortButton(StagingPanelView panel) =>
        panel.ChangesList.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "ConflictAbort");

    private static void Click(Button button)
    {
        button.Command!.Execute(button.CommandParameter);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task Abort_sits_right_of_the_conflicted_header_during_a_merge()
    {
        Assert.Equal(OperationState.None, _context.State.PendingOperation);
        var (panel, window) = await ShowPanelAsync();
        try
        {
            Assert.Equal(OperationState.Merging, _context.State.PendingOperation);
            var abort = AbortButton(panel);
            var grid = Assert.IsType<Grid>(abort.Parent);

            Assert.True(abort.IsVisible);
            Assert.Equal(1, Grid.GetColumn(abort));
            Assert.Contains("danger", abort.Classes);

            var right = abort.TranslatePoint(new Point(abort.Bounds.Width, 0), grid)!.Value.X;
            Assert.InRange(grid.Bounds.Width - right, -0.5, 0.5);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Abort_is_the_height_of_the_header_text()
    {
        var (panel, window) = await ShowPanelAsync();
        try
        {
            var abort = AbortButton(panel);
            var grid = Assert.IsType<Grid>(abort.Parent);
            var text = grid.Children.OfType<TextBlock>().Single();

            Assert.Equal(text.FontSize, abort.FontSize);
            Assert.True(abort.Bounds.Height <= text.Bounds.Height + 2.5,
                $"Abort {abort.Bounds.Height} vs text {text.Bounds.Height}");
            var line = grid.Bounds.Height + grid.Margin.Top + grid.Margin.Bottom;
            Assert.True(line <= text.Bounds.Height + 4.5, $"Header line {line} vs text {text.Bounds.Height}");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Abort_hidden_for_conflicts_without_an_abortable_operation()
    {
        var (panel, window) = await ShowPanelAsync(OperationState.Bisecting);
        try
        {
            Assert.Equal(OperationState.Bisecting, _context.State.PendingOperation);
            Assert.False(AbortButton(panel).IsVisible);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Abort_shown_while_resolver_closed()
    {
        var (panel, window) = await ShowPanelAsync();
        try
        {
            _context.State.SetConflictResolverClosed(true);
            Dispatcher.UIThread.RunJobs();

            Assert.True(AbortButton(panel).IsVisible);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Abort_confirms_before_calling_service()
    {
        var (panel, window) = await ShowPanelAsync();
        var work = new FakeUnsavedWork(_context);
        using var registration = _unsavedWork.Register(work);
        try
        {
            _dialogs.ConfirmDestructiveResult = false;
            Click(AbortButton(panel));

            Assert.Single(_dialogs.ConfirmDestructiveCalls);
            Assert.Equal(0, _conflicts.AbortCalls);
            Assert.Equal(0, work.DiscardCalls);

            _dialogs.ConfirmDestructiveResult = true;
            Click(AbortButton(panel));

            Assert.Equal(2, _dialogs.ConfirmDestructiveCalls.Count);
            Assert.Equal(1, _conflicts.AbortCalls);
            Assert.Equal(1, work.DiscardCalls);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Abort_while_running_is_ignored()
    {
        var (panel, window) = await ShowPanelAsync();
        try
        {
            var tcs = new TaskCompletionSource<bool>();
            _conflicts.AbortTcs = tcs;

            Click(AbortButton(panel));
            Assert.Equal(1, _conflicts.AbortCalls);
            Assert.False(AbortButton(panel).IsEffectivelyEnabled);

            Click(AbortButton(panel));
            Assert.Equal(1, _conflicts.AbortCalls);
            Assert.Single(_dialogs.ConfirmDestructiveCalls);

            tcs.SetResult(true);
            Dispatcher.UIThread.RunJobs();
            Assert.True(AbortButton(panel).IsEffectivelyEnabled);
        }
        finally
        {
            window.Close();
        }
    }

    private sealed class FakeUnsavedWork(RepositoryContext context) : IUnsavedWork
    {
        public int DiscardCalls { get; private set; }
        public RepositoryContext Context { get; } = context;
        public string Description => "the conflict resolution in f.txt";
        public Task<bool> SaveAsync() => Task.FromResult(true);
        public void Discard() => DiscardCalls++;
    }

    private sealed class AbortFakeConflictService : IConflictService
    {
        public TaskCompletionSource<bool>? AbortTcs { get; set; }
        public int AbortCalls { get; private set; }

        public async Task AbortOperationAsync(RepositoryContext context, CancellationToken ct = default)
        {
            AbortCalls++;
            if (AbortTcs != null)
                await AbortTcs.Task;
        }

        public Task ContinueOperationAsync(RepositoryContext context, CancellationToken ct = default)
            => Task.CompletedTask;

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
