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
    private RepositoryContext _context;
    private readonly AiIntegrationsPanel _aiPanel = new();

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
        public bool CustomThemeActive => false;
        public Task SetCustomThemeEnabledAsync(bool enabled, CancellationToken ct = default) => Task.CompletedTask;
        public Task SelectCustomThemeAsync(string themeId, CancellationToken ct = default) => Task.CompletedTask;
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
        var settings = new ConflictResolverViewTests.FakeSettingsService();
        sc.AddSingleton<ISettingsService>(settings);
        sc.AddSingleton<IAiProfileService>(new AiProfileService(settings));
        sc.AddSingleton<IAiIntegrationsPanel>(_aiPanel);
        sc.AddSingleton<IAiClientFactory>(new AiTestSupport.Factory(new AiTestSupport.Client()));

        _sp = sc.BuildServiceProvider();
        _conflictHost = _sp.GetRequiredService<ConflictResolverHost>();
        _workspace = _sp.GetRequiredService<IWorkspaceService>();
        _context = _workspace.OpenAsync(_fixture.Path).GetAwaiter().GetResult();
        _workspace.Focus(_context);
    }

    /// <summary>Refreshes the tab's status and runs the posted UI work until
    /// <paramref name="condition"/> holds. A refresh that joins one the tab's file watcher already
    /// had running can return a snapshot taken before the test's last git command; the next
    /// refresh reads the repository again.</summary>
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

    /// <summary>Closes the tab, moves the fixture to a copy of the merge-conflict template and opens
    /// that, so no watcher sees the conflict being made and no refresh it starts overlaps the
    /// test's own. The merge stopped on the conflict, so the repository is also merging. Then
    /// waits for the conflict session.</summary>
    private async Task CreateConflictInFreshTabAsync()
    {
        _conflicts.Conflicts = [new ConflictFile { FilePath = "f.txt", Status = ConflictStatus.BothModified, IsBinary = false }];
        _conflicts.WorkingCopies["f.txt"] = new ConflictWorkingCopy { Path = "f.txt", Text = "some text", DiskHash = "h1" };

        var old = _context;
        await Task.Run(async () => await _workspace.CloseAsync(old));
        _fixture.LoadMergeConflict();
        _context = await Task.Run(() => _workspace.OpenAsync(_fixture.Path));
        _workspace.Focus(_context);

        await RefreshUntilAsync(() => _context.State.IsConflictSessionActive, "the conflict session to start");
    }

    [AvaloniaFact]
    public async Task Conflicts_show_resolver_in_centre_not_detail_pane()
    {
        await CreateConflictInFreshTabAsync();

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
        await CreateConflictInFreshTabAsync();

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
        await CreateConflictInFreshTabAsync();

        var centerHost = new ContentControl();
        var graph = new Border();
        using var controller = new CenterPaneController(centerHost, graph, _conflictHost, _workspace, _sp);

        Dispatcher.UIThread.RunJobs();
        Assert.NotNull(_conflictHost.TryGet(_context));
        Assert.IsType<ConflictResolverView>(centerHost.Content);

        _fixture.ClearOperation();
        _fixture.MarkResolved();
        await RefreshUntilAsync(() => !_context.State.IsConflictSessionActive, "the conflict session to end");

        Assert.Null(_conflictHost.TryGet(_context));
        Assert.Same(graph, centerHost.Content);
    }

    private async Task<(ContentControl CenterHost, Border Graph, CenterPaneController Controller, ConflictResolverView Resolver)> ShowResolverAsync()
    {
        await CreateConflictInFreshTabAsync();

        var centerHost = new ContentControl();
        var graph = new Border();
        var controller = new CenterPaneController(centerHost, graph, _conflictHost, _workspace, _sp);
        Dispatcher.UIThread.RunJobs();

        var resolver = Assert.IsType<ConflictResolverView>(centerHost.Content);
        await resolver.LoadAsync();
        return (centerHost, graph, controller, resolver);
    }

    [AvaloniaFact]
    public async Task Closed_resolver_shows_graph_and_keeps_the_view()
    {
        var (centerHost, graph, controller, resolver) = await ShowResolverAsync();
        using var _ = controller;
        resolver.ResultEditorForTesting.Document.Insert(0, "edit\n");
        Assert.True(resolver.HasUnsavedWork);

        _context.State.SetConflictResolverClosed(true);
        Dispatcher.UIThread.RunJobs();

        Assert.Same(graph, centerHost.Content);
        Assert.Same(resolver, _conflictHost.TryGet(_context));
        Assert.True(resolver.HasUnsavedWork);
        Assert.True(_context.State.IsConflictSessionActive);
    }

    [AvaloniaFact]
    public async Task Reopening_shows_the_same_resolver_instance()
    {
        var (centerHost, graph, controller, resolver) = await ShowResolverAsync();
        using var _ = controller;

        _context.State.SetConflictResolverClosed(true);
        Dispatcher.UIThread.RunJobs();
        Assert.Same(graph, centerHost.Content);

        _context.State.SetConflictResolverClosed(false);
        Dispatcher.UIThread.RunJobs();

        Assert.Same(resolver, centerHost.Content);
        Assert.Same(resolver, _conflictHost.TryGet(_context));
    }

    [AvaloniaFact]
    public async Task Closed_flag_cannot_hide_unsaved_work_after_the_session_ends()
    {
        var (centerHost, _, controller, resolver) = await ShowResolverAsync();
        using var _ = controller;
        resolver.ResultEditorForTesting.Document.Insert(0, "edit\n");

        _fixture.MarkResolved();
        _fixture.ClearOperation();
        await RefreshUntilAsync(() => !_context.State.IsConflictSessionActive, "the conflict session to end");

        _context.State.SetConflictResolverClosed(true);
        Dispatcher.UIThread.RunJobs();

        Assert.Same(resolver, centerHost.Content);
        Assert.True(resolver.HasUnsavedWork);
    }

    [AvaloniaFact]
    public async Task New_session_after_close_opens_resolver()
    {
        _conflicts.Conflicts = [new ConflictFile { FilePath = "f.txt", Status = ConflictStatus.BothModified, IsBinary = false }];
        _conflicts.WorkingCopies["f.txt"] = new ConflictWorkingCopy { Path = "f.txt", Text = "some text", DiskHash = "h1" };

        var centerHost = new ContentControl();
        var graph = new Border();
        using var controller = new CenterPaneController(centerHost, graph, _conflictHost, _workspace, _sp);
        Dispatcher.UIThread.RunJobs();
        Assert.Same(graph, centerHost.Content);

        _context.State.SetConflictResolverClosed(true);
        _fixture.CreateConflict();
        await RefreshUntilAsync(() => _context.State.IsConflictSessionActive, "the conflict session to start");

        Assert.False(_context.State.IsConflictResolverClosed);
        Assert.IsType<ConflictResolverView>(centerHost.Content);
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

    [AvaloniaFact]
    public async Task A_repeated_focus_request_keeps_the_view_and_scrolls_back_to_the_line()
    {
        var file = System.IO.Path.Combine(_fixture.Path, "big.txt");
        var lines = Enumerable.Range(1, 200).Select(i => $"line {i}").ToArray();
        File.WriteAllText(file, string.Join("\n", lines) + "\n");
        using (var repo = new LibGit2Sharp.Repository(_fixture.Path))
        {
            LibGit2Sharp.Commands.Stage(repo, "big.txt");
            var sig = new LibGit2Sharp.Signature("Test", "test@example.com", DateTimeOffset.Now);
            repo.Commit("big", sig, sig, new LibGit2Sharp.CommitOptions());
        }
        lines[4] = "line 5 changed";
        lines[169] = "line 170 changed";
        File.WriteAllText(file, string.Join("\n", lines) + "\n");

        var centerHost = new ContentControl();
        var window = new Window { Width = 900, Height = 160, Content = centerHost };
        window.Show();
        using var controller = new CenterPaneController(centerHost, new Border(), _conflictHost, _workspace, _sp);
        try
        {
            DiffRequest Request() => new() { Source = DiffSource.UnstagedChanges, Path = "big.txt", FocusLine = 170 };

            _context.State.SetOpenDiff(Request());
            // The first load waits on git behind the tab's opening work, which can take a while on a busy machine.
            Assert.True(await PumpUntilAsync(window, () => controller.ActiveDiffViewForTesting?.PresenterForTesting
                is CombinedDiffPresenter or SideBySideDiffPresenter, TimeSpan.FromSeconds(30)),
                $"the diff never loaded; showing {(controller.ActiveDiffViewForTesting?.PresenterForTesting as TextBlock)?.Text
                    ?? controller.ActiveDiffViewForTesting?.PresenterForTesting?.GetType().Name ?? "nothing"}");
            var view = controller.ActiveDiffViewForTesting!;
            var presenter = view.PresenterForTesting;
            (int Start, int End)? Range() => presenter switch
            {
                CombinedDiffPresenter c => c.VisibleLineRange,
                SideBySideDiffPresenter s => s.VisibleLineRange,
                _ => null,
            };
            var expected = view.ModelForTesting!.FindDisplayLineForNewLine(170, presenter is SideBySideDiffPresenter)!.Value;
            bool Showing(int line) => Range() is { } r && line >= r.Start && line <= r.End;
            Assert.True(await PumpUntilAsync(window, () => Showing(expected)), $"the first open never scrolled to {expected}; visible {Range()}");

            if (presenter is CombinedDiffPresenter combined)
                combined.ScrollToLine(1);
            else
                ((SideBySideDiffPresenter)presenter!).ScrollToLine(1);
            Assert.True(await PumpUntilAsync(window, () => Range()?.Start == 1), $"the view never scrolled to the top; visible {Range()}");

            _context.State.SetOpenDiff(Request());
            Assert.True(await PumpUntilAsync(window, () => Showing(expected)), $"the repeat never scrolled back to {expected}; visible {Range()}");
            Assert.Same(view, controller.ActiveDiffViewForTesting);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Runs posted UI work and layout until <paramref name="condition"/> holds, for at most
    /// <paramref name="timeout"/> (10 seconds by default).</summary>
    internal static async Task<bool> PumpUntilAsync(Window window, Func<bool> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (true)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            if (condition())
                return true;
            if (DateTime.UtcNow >= deadline)
                return false;
            await Task.Delay(20);
        }
    }

    [AvaloniaFact]
    public async Task A_closed_context_never_gets_a_resolver()
    {
        await Task.Run(async () => await _workspace.CloseAsync(_context));

        Assert.Null(_conflictHost.GetOrCreate(_context));
        Assert.Null(_conflictHost.TryGet(_context));
    }

    [AvaloniaFact]
    public async Task UpdateContent_for_a_closed_context_shows_the_graph()
    {
        await CreateConflictInFreshTabAsync();

        var centerHost = new ContentControl();
        var graph = new Border();
        using var controller = new CenterPaneController(centerHost, graph, _conflictHost, _workspace, _sp);
        Dispatcher.UIThread.RunJobs();
        Assert.IsType<ConflictResolverView>(centerHost.Content);

        // The tab closes, but the controller is still bound to it: a content update posted before
        // the close (or by a late status event) arrives after it.
        await Task.Run(async () => await _workspace.CloseAsync(_context));
        controller.UpdateContent();

        Assert.Same(graph, centerHost.Content);
        Assert.Null(_conflictHost.GetOrCreate(_context));
    }

    [AvaloniaFact]
    public void An_open_AI_window_beats_the_graph_and_works_with_no_repository()
    {
        var centerHost = new ContentControl();
        var graph = new Border();
        using var controller = new CenterPaneController(centerHost, graph, _conflictHost, workspace: null, _sp);
        Assert.Same(graph, centerHost.Content);

        _aiPanel.Open();
        Dispatcher.UIThread.RunJobs();

        Assert.IsType<AiIntegrationsView>(centerHost.Content);

        _aiPanel.Close();
        Dispatcher.UIThread.RunJobs();

        Assert.Same(graph, centerHost.Content);
        Assert.Null(controller.ActiveAiViewForTesting);
    }

    [AvaloniaFact]
    public void An_open_AI_window_beats_an_open_diff_and_closing_restores_the_diff()
    {
        var centerHost = new ContentControl();
        var graph = new Border();
        using var controller = new CenterPaneController(centerHost, graph, _conflictHost, _workspace, _sp);
        _context.State.SetOpenDiff(new DiffRequest { Source = DiffSource.UnstagedChanges, Path = "f.txt" });
        Dispatcher.UIThread.RunJobs();
        Assert.IsType<DiffView>(centerHost.Content);

        _aiPanel.Open();
        Dispatcher.UIThread.RunJobs();
        Assert.IsType<AiIntegrationsView>(centerHost.Content);

        _aiPanel.Close();
        Dispatcher.UIThread.RunJobs();

        var restored = Assert.IsType<DiffView>(centerHost.Content);
        Assert.Same(restored, controller.ActiveDiffViewForTesting);
        Assert.NotNull(_context.State.OpenDiff);
    }

    [AvaloniaFact]
    public async Task An_open_AI_window_beats_the_resolver_and_closing_returns_the_same_resolver()
    {
        var (centerHost, _, controller, resolver) = await ShowResolverAsync();
        using var _ = controller;
        resolver.ResultEditorForTesting.Document.Insert(0, "edit\n");

        _aiPanel.Open();
        Dispatcher.UIThread.RunJobs();
        Assert.IsType<AiIntegrationsView>(centerHost.Content);

        _aiPanel.Close();
        Dispatcher.UIThread.RunJobs();

        Assert.Same(resolver, centerHost.Content);
        Assert.True(resolver.HasUnsavedWork);
    }

    [AvaloniaFact]
    public void A_new_diff_while_the_AI_window_is_open_closes_it_and_shows_the_diff()
    {
        var centerHost = new ContentControl();
        var graph = new Border();
        using var controller = new CenterPaneController(centerHost, graph, _conflictHost, _workspace, _sp);
        _aiPanel.Open();
        Dispatcher.UIThread.RunJobs();
        Assert.IsType<AiIntegrationsView>(centerHost.Content);

        _context.State.SetOpenDiff(new DiffRequest { Source = DiffSource.UnstagedChanges, Path = "f.txt" });
        Dispatcher.UIThread.RunJobs();

        Assert.False(_aiPanel.IsOpen);
        Assert.IsType<DiffView>(centerHost.Content);
    }

    [AvaloniaFact]
    public void A_selected_conflict_path_while_the_AI_window_is_open_closes_it()
    {
        var centerHost = new ContentControl();
        var graph = new Border();
        using var controller = new CenterPaneController(centerHost, graph, _conflictHost, _workspace, _sp);
        _aiPanel.Open();
        Dispatcher.UIThread.RunJobs();

        _context.State.SetSelectedConflictPath("f.txt");
        Dispatcher.UIThread.RunJobs();

        Assert.False(_aiPanel.IsOpen);
        Assert.Same(graph, centerHost.Content);
    }

    [AvaloniaFact]
    public void Disposing_the_controller_unsubscribes_from_the_panel()
    {
        var centerHost = new ContentControl();
        var graph = new Border();
        var controller = new CenterPaneController(centerHost, graph, _conflictHost, _workspace, _sp);
        controller.Dispose();

        _aiPanel.Open();
        Dispatcher.UIThread.RunJobs();

        Assert.Null(controller.ActiveAiViewForTesting);
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
