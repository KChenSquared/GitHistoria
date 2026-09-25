namespace HistoriaApp.Tests;

[Trait("tier", "ui")]
public class CenterPaneControllerTests : IDisposable
{
    private readonly ConflictResolverViewTests.TestRepoFixture _fixture;
    private readonly ServiceProvider _sp;
    private readonly ConflictResolverViewTests.FakeConflictService _conflicts;
    private readonly ConflictResolverViewTests.FakeDialogService _dialogs;
    private readonly UnsavedWorkRegistry _unsavedRegistry;
    private readonly SyntaxThemeHost _syntaxHost;
    private readonly ConflictResolverHost _conflictHost;
    private readonly IWorkspaceService _workspace;
    private readonly RepositoryContext _context;

    private sealed class StubThemeService : IThemeService
    {
        public EffectiveTheme Effective { get; set; } = EffectiveTheme.Dark;
        public ThemeMode CurrentMode => ThemeMode.Dark;
        public bool HighContrastEnabled => Effective == EffectiveTheme.HighContrast;
        public string EffectiveCssThemeName => Effective == EffectiveTheme.Light ? "light" : "dark";
        public IReadOnlyDictionary<string, Rgb> Palette => HistoriaPalette.For(Effective);
        public IReadOnlyList<Rgb> LaneColors => [];

        public event EventHandler<ThemeChangedEventArgs>? ThemeChanged { add { } remove { } }

        public Task ApplyModeAsync(ThemeMode mode, CancellationToken ct = default) => Task.CompletedTask;
        public Task ApplyCustomThemeAsync(string themeId, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetHighContrastAsync(bool enabled, CancellationToken ct = default) => Task.CompletedTask;
    }

    public CenterPaneControllerTests()
    {
        _fixture = new ConflictResolverViewTests.TestRepoFixture();
        _conflicts = new ConflictResolverViewTests.FakeConflictService();
        _dialogs = new ConflictResolverViewTests.FakeDialogService();
        _unsavedRegistry = new UnsavedWorkRegistry();
        var themeService = new StubThemeService();
        _syntaxHost = new SyntaxThemeHost(themeService);

        var sc = new ServiceCollection();
        sc.AddSingleton<IConflictService>(_conflicts);
        sc.AddSingleton<IDialogService>(_dialogs);
        sc.AddSingleton<IUnsavedWorkRegistry>(_unsavedRegistry);
        sc.AddSingleton<IThemeService>(themeService);
        sc.AddSingleton(_syntaxHost);
        sc.AddSingleton<IUiDispatcher, Historia.Platform.UiDispatcher>();
        sc.AddSingleton<ConflictResolverHost>();

        sc.AddSingleton<IKeyboardCommandRouter, KeyboardCommandRouter>();
        sc.AddSingleton<INotificationService, NotificationService>();
        sc.AddSingleton<IRepoLayoutService, RepoLayoutService>();
        sc.AddSingleton<ISystemThemeProbe, ConflictResolverViewTests.FakeThemeProbe>();
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
        sc.AddSingleton<ICredentialVaultService, ConflictResolverViewTests.FakeVault>();
        sc.AddSingleton<IExternalLauncher, ExternalLauncher>();
        sc.AddSingleton<IRepoNetworkActionRunner, ConflictResolverViewTests.FakeNetworkRunner>();
        sc.AddSingleton<ISettingsService>(new ConflictResolverViewTests.FakeSettingsService());

        _sp = sc.BuildServiceProvider();
        _conflictHost = _sp.GetRequiredService<ConflictResolverHost>();
        _workspace = _sp.GetRequiredService<IWorkspaceService>();
        _context = _workspace.OpenAsync(_fixture.Path).GetAwaiter().GetResult();
        _workspace.Focus(_context);
    }

    [AvaloniaFact]
    public async Task Conflicts_show_resolver_in_centre_not_detail_pane()
    {
        _conflicts.Conflicts = [new ConflictFile { FilePath = "f.txt", Status = ConflictStatus.BothModified, IsBinary = false }];
        _conflicts.WorkingCopies["f.txt"] = new ConflictWorkingCopy { Path = "f.txt", Text = "some text", DiskHash = "h1" };
        _fixture.CreateConflict();
        await _context.State.RefreshStatusAsync();

        var centerHost = new ContentControl();
        var graph = new Border();
        using var controller = new CenterPaneController(centerHost, graph, _conflictHost, _workspace, _sp);

        Dispatcher.UIThread.RunJobs();

        Assert.True(_context.State.IsConflictSessionActive);
        Assert.IsType<ConflictResolverView>(centerHost.Content);
    }

    [AvaloniaFact]
    public async Task Open_diff_overlays_resolver_and_close_returns_to_same_instance_with_unsaved_edits()
    {
        _conflicts.Conflicts = [new ConflictFile { FilePath = "f.txt", Status = ConflictStatus.BothModified, IsBinary = false }];
        _conflicts.WorkingCopies["f.txt"] = new ConflictWorkingCopy { Path = "f.txt", Text = "some text", DiskHash = "h1" };
        _fixture.CreateConflict();
        await _context.State.RefreshStatusAsync();

        var centerHost = new ContentControl();
        var graph = new Border();
        using var controller = new CenterPaneController(centerHost, graph, _conflictHost, _workspace, _sp);

        Dispatcher.UIThread.RunJobs();

        var resolver = Assert.IsType<ConflictResolverView>(centerHost.Content);
        await resolver.LoadAsync();
        resolver.ResultEditorForTesting.Document.Insert(0, "edit\n");
        Assert.True(resolver.HasUnsavedWork);

        var diffReq = new DiffRequest { Source = DiffSource.UnstagedChanges, Path = "f.txt" };
        _context.State.SetOpenDiff(diffReq);
        Dispatcher.UIThread.RunJobs();

        Assert.IsType<DiffView>(centerHost.Content);

        _context.State.SetOpenDiff(null);
        Dispatcher.UIThread.RunJobs();

        var restored = Assert.IsType<ConflictResolverView>(centerHost.Content);
        Assert.Same(resolver, restored);
        Assert.True(restored.HasUnsavedWork);
    }

    [AvaloniaFact]
    public async Task Session_end_releases_resolver_and_restores_graph()
    {
        _conflicts.Conflicts = [new ConflictFile { FilePath = "f.txt", Status = ConflictStatus.BothModified, IsBinary = false }];
        _conflicts.WorkingCopies["f.txt"] = new ConflictWorkingCopy { Path = "f.txt", Text = "some text", DiskHash = "h1" };
        _fixture.SetOperation(OperationState.Merging);
        _fixture.CreateConflict();
        await _context.State.RefreshStatusAsync();

        var centerHost = new ContentControl();
        var graph = new Border();
        using var controller = new CenterPaneController(centerHost, graph, _conflictHost, _workspace, _sp);

        Dispatcher.UIThread.RunJobs();
        Assert.NotNull(_conflictHost.TryGet(_context));
        Assert.IsType<ConflictResolverView>(centerHost.Content);

        _fixture.ClearOperation();
        _fixture.MarkResolved();
        await _context.State.RefreshStatusAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.False(_context.State.IsConflictSessionActive);
        Assert.Null(_conflictHost.TryGet(_context));
        Assert.Same(graph, centerHost.Content);
    }

    [AvaloniaFact]
    public void Replaced_diff_view_is_disposed()
    {
        var centerHost = new ContentControl();
        var graph = new Border();
        using var controller = new CenterPaneController(centerHost, graph, _conflictHost, _workspace, _sp);

        var diffReq1 = new DiffRequest { Source = DiffSource.UnstagedChanges, Path = "f1.txt" };
        _context.State.SetOpenDiff(diffReq1);
        Dispatcher.UIThread.RunJobs();

        var diffView1 = controller.ActiveDiffViewForTesting;
        Assert.NotNull(diffView1);

        var diffReq2 = new DiffRequest { Source = DiffSource.UnstagedChanges, Path = "f2.txt" };
        _context.State.SetOpenDiff(diffReq2);
        Dispatcher.UIThread.RunJobs();

        var diffView2 = controller.ActiveDiffViewForTesting;
        Assert.NotNull(diffView2);
        Assert.NotSame(diffView1, diffView2);

        Assert.Null(diffView1.PresenterForTesting);
    }

    public void Dispose()
    {
        try { _conflictHost.Dispose(); } catch { }
        try { _syntaxHost.Dispose(); } catch { }
        try { _context.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
        try { _sp.Dispose(); } catch { }
        try { _fixture.Dispose(); } catch { }
    }
}
