namespace HistoriaApp.Tests;

[Trait("tier", "ui")]
public class ConflictResolverViewTests : IDisposable
{
    private readonly TestRepoFixture _fixture;
    private readonly ServiceProvider _sp;
    private readonly FakeConflictService _conflicts;
    private readonly FakeDialogService _dialogs;
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

        public event EventHandler<ThemeChangedEventArgs>? ThemeChanged;

        public Task ApplyModeAsync(ThemeMode mode, CancellationToken ct = default) => Task.CompletedTask;
        public Task ApplyCustomThemeAsync(string themeId, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetHighContrastAsync(bool enabled, CancellationToken ct = default) => Task.CompletedTask;

        public void ChangeTheme(EffectiveTheme newTheme)
        {
            var old = Effective;
            Effective = newTheme;
            ThemeChanged?.Invoke(this, new ThemeChangedEventArgs(old, newTheme));
        }
    }

    public ConflictResolverViewTests()
    {
        _fixture = new TestRepoFixture();
        _conflicts = new FakeConflictService();
        _dialogs = new FakeDialogService();
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
        sc.AddSingleton<ISystemThemeProbe, FakeThemeProbe>();
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
        sc.AddSingleton<ICredentialVaultService, FakeVault>();
        sc.AddSingleton<IExternalLauncher, ExternalLauncher>();
        sc.AddSingleton<IRepoNetworkActionRunner, FakeNetworkRunner>();
        sc.AddSingleton<ISettingsService>(new FakeSettingsService());

        _sp = sc.BuildServiceProvider();
        _conflictHost = _sp.GetRequiredService<ConflictResolverHost>();
        var ws = _sp.GetRequiredService<IWorkspaceService>();
        _workspace = ws;
        _context = ws.OpenAsync(_fixture.Path).GetAwaiter().GetResult();
        ws.Focus(_context);
    }

    public void Dispose()
    {
        try { _conflictHost.Dispose(); } catch { }
        try { _syntaxHost.Dispose(); } catch { }
        try { _context.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
        try { _sp.Dispose(); } catch { }
        try { _fixture.Dispose(); } catch { }
    }

    private const string TwoConflictText =
        "header\n" +
        "<<<<<<< HEAD\n" +
        "ours 1\n" +
        "=======\n" +
        "theirs 1\n" +
        ">>>>>>> branch\n" +
        "middle\n" +
        "<<<<<<< HEAD\n" +
        "ours 2\n" +
        "=======\n" +
        "theirs 2\n" +
        ">>>>>>> branch\n" +
        "footer\n";

    [AvaloniaFact]
    public async Task Result_shows_working_file_text_with_markers()
    {
        _conflicts.Conflicts = [new ConflictFile { FilePath = "f.txt", Status = ConflictStatus.BothModified, IsBinary = false }];
        _conflicts.WorkingCopies["f.txt"] = new ConflictWorkingCopy
        {
            Path = "f.txt",
            Text = TwoConflictText,
            DiskHash = "hash1"
        };

        var view = new ConflictResolverView(_sp, _context);
        await view.LoadAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.Contains("<<<<<<< HEAD", view.ResultEditorForTesting.Text);
        Assert.Contains("theirs 1", view.ResultEditorForTesting.Text);
        Assert.Contains("middle", view.ResultEditorForTesting.Text);
    }

    [AvaloniaFact]
    public async Task Use_theirs_on_region_0_replaces_block_and_undo_restores()
    {
        _conflicts.Conflicts = [new ConflictFile { FilePath = "f.txt", Status = ConflictStatus.BothModified, IsBinary = false }];
        _conflicts.WorkingCopies["f.txt"] = new ConflictWorkingCopy
        {
            Path = "f.txt",
            Text = TwoConflictText,
            DiskHash = "hash1"
        };

        var view = new ConflictResolverView(_sp, _context);
        await view.LoadAsync();
        Dispatcher.UIThread.RunJobs();

        var initialText = view.ResultEditorForTesting.Text;
        var r0 = view.CurrentParseForTesting!.Regions[0];
        view.ApplyRegionAction(r0, ConflictResolverView.RegionActionChoice.Theirs);

        Assert.DoesNotContain("ours 1", view.ResultEditorForTesting.Text);
        Assert.Contains("theirs 1", view.ResultEditorForTesting.Text);

        view.ResultEditorForTesting.Document.UndoStack.Undo();
        Assert.Equal(initialText, view.ResultEditorForTesting.Text);
    }

    [AvaloniaFact]
    public async Task Ours_and_theirs_panes_have_same_line_count_as_result()
    {
        _conflicts.Conflicts = [new ConflictFile { FilePath = "f.txt", Status = ConflictStatus.BothModified, IsBinary = false }];
        _conflicts.WorkingCopies["f.txt"] = new ConflictWorkingCopy
        {
            Path = "f.txt",
            Text = TwoConflictText,
            DiskHash = "hash1"
        };

        var view = new ConflictResolverView(_sp, _context);
        await view.LoadAsync();
        Dispatcher.UIThread.RunJobs();

        int resultLines = view.ResultEditorForTesting.Document.LineCount;
        Assert.Equal(resultLines, view.OursEditorForTesting.Document.LineCount);
        Assert.Equal(resultLines, view.TheirsEditorForTesting.Document.LineCount);

        // Edit result
        view.ApplyRegionAction(view.CurrentParseForTesting!.Regions[0], ConflictResolverView.RegionActionChoice.Ours);
        resultLines = view.ResultEditorForTesting.Document.LineCount;
        Assert.Equal(resultLines, view.OursEditorForTesting.Document.LineCount);
        Assert.Equal(resultLines, view.TheirsEditorForTesting.Document.LineCount);
    }

    [AvaloniaFact]
    public async Task Remaining_count_updates_as_regions_resolved()
    {
        _conflicts.Conflicts = [new ConflictFile { FilePath = "f.txt", Status = ConflictStatus.BothModified, IsBinary = false }];
        _conflicts.WorkingCopies["f.txt"] = new ConflictWorkingCopy
        {
            Path = "f.txt",
            Text = TwoConflictText,
            DiskHash = "hash1"
        };

        var view = new ConflictResolverView(_sp, _context);
        await view.LoadAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(2, view.CurrentParseForTesting!.Regions.Count);

        view.ApplyRegionAction(view.CurrentParseForTesting.Regions[0], ConflictResolverView.RegionActionChoice.Ours);
        Assert.Single(view.CurrentParseForTesting.Regions);

        view.ApplyRegionAction(view.CurrentParseForTesting.Regions[0], ConflictResolverView.RegionActionChoice.Theirs);
        Assert.Empty(view.CurrentParseForTesting.Regions);
    }

    [AvaloniaFact]
    public async Task Typing_registers_unsaved_work_and_save_unregisters()
    {
        _conflicts.Conflicts = [new ConflictFile { FilePath = "f.txt", Status = ConflictStatus.BothModified, IsBinary = false }];
        _conflicts.WorkingCopies["f.txt"] = new ConflictWorkingCopy
        {
            Path = "f.txt",
            Text = TwoConflictText,
            DiskHash = "hash1"
        };

        var view = new ConflictResolverView(_sp, _context);
        await view.LoadAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.False(view.HasUnsavedWork);

        view.ResultEditorForTesting.Document.Insert(0, "custom edit\n");
        Dispatcher.UIThread.RunJobs();

        Assert.True(view.HasUnsavedWork);

        await view.SaveCurrentFileAsync(stage: false);
        Assert.False(view.HasUnsavedWork);
    }

    [AvaloniaFact]
    public async Task Status_changed_while_dirty_keeps_buffer()
    {
        _conflicts.Conflicts = [new ConflictFile { FilePath = "f.txt", Status = ConflictStatus.BothModified, IsBinary = false }];
        _conflicts.WorkingCopies["f.txt"] = new ConflictWorkingCopy
        {
            Path = "f.txt",
            Text = TwoConflictText,
            DiskHash = "hash1"
        };

        var view = new ConflictResolverView(_sp, _context);
        await view.LoadAsync();
        Dispatcher.UIThread.RunJobs();

        view.ResultEditorForTesting.Document.Insert(0, "dirty line\n");
        Assert.True(view.HasUnsavedWork);

        // Outside change occurs
        _conflicts.WorkingCopies["f.txt"] = new ConflictWorkingCopy
        {
            Path = "f.txt",
            Text = "outside change",
            DiskHash = "hash2"
        };

        await _context.State.RefreshStatusAsync();
        Dispatcher.UIThread.RunJobs();
        await Task.Delay(100);
        Dispatcher.UIThread.RunJobs();

        Assert.Contains("dirty line", view.ResultEditorForTesting.Text);
        Assert.True(view.ChangedOnDiskBannerForTesting.IsVisible);
    }

    [AvaloniaFact]
    public async Task Take_ours_with_dialog_returning_false_never_calls_service()
    {
        _conflicts.Conflicts = [new ConflictFile { FilePath = "f.txt", Status = ConflictStatus.BothModified, IsBinary = false }];
        _conflicts.WorkingCopies["f.txt"] = new ConflictWorkingCopy
        {
            Path = "f.txt",
            Text = TwoConflictText,
            DiskHash = "hash1"
        };

        var view = new ConflictResolverView(_sp, _context);
        await view.LoadAsync();
        Dispatcher.UIThread.RunJobs();

        _dialogs.ConfirmDestructiveResult = false;
        await view.TakeOursCurrentAsync();

        Assert.Empty(_conflicts.ResolvedWithSideCalls);
    }

    [AvaloniaFact]
    public async Task Skip_button_visible_for_rebasing_hidden_for_merging_op_bar_hidden_for_none()
    {
        _conflicts.Conflicts = [new ConflictFile { FilePath = "f.txt", Status = ConflictStatus.BothModified, IsBinary = false }];
        _conflicts.WorkingCopies["f.txt"] = new ConflictWorkingCopy { Path = "f.txt", Text = TwoConflictText, DiskHash = "hash1" };

        var view = new ConflictResolverView(_sp, _context);

        _fixture.SetOperation(OperationState.Rebasing);
        await _context.State.RefreshStatusAsync();
        await view.LoadAsync();
        Dispatcher.UIThread.RunJobs();
        Assert.True(view.OpBarForTesting.IsVisible);
        Assert.True(view.SkipButtonForTesting.IsVisible);

        _fixture.SetOperation(OperationState.Merging);
        await _context.State.RefreshStatusAsync();
        await view.LoadAsync();
        Dispatcher.UIThread.RunJobs();
        Assert.True(view.OpBarForTesting.IsVisible);
        Assert.False(view.SkipButtonForTesting.IsVisible);

        _fixture.ClearOperation();
        await _context.State.RefreshStatusAsync();
        await view.LoadAsync();
        Dispatcher.UIThread.RunJobs();
        Assert.False(view.OpBarForTesting.IsVisible);
    }

    [AvaloniaFact]
    public async Task Last_file_resolved_with_pending_operation_keeps_resolver_open_for_continue()
    {
        _conflicts.Conflicts = [new ConflictFile { FilePath = "f.txt", Status = ConflictStatus.BothModified, IsBinary = false }];
        _conflicts.WorkingCopies["f.txt"] = new ConflictWorkingCopy { Path = "f.txt", Text = TwoConflictText, DiskHash = "hash1" };

        _fixture.SetOperation(OperationState.Merging);
        _fixture.CreateConflict();
        await _context.State.RefreshStatusAsync();

        var centerHost = new ContentControl();
        var graph = new Border();
        using var controller = new CenterPaneController(centerHost, graph, _conflictHost, _workspace, _sp);
        Dispatcher.UIThread.RunJobs();

        var resolver = _conflictHost.TryGet(_context);
        Assert.NotNull(resolver);
        Assert.True(_context.State.KeepConflictResolverOpen);
        Assert.True(_context.State.IsConflictSessionActive);
        Assert.Same(resolver, centerHost.Content);

        // Mark resolved
        _fixture.MarkResolved();
        await _context.State.RefreshStatusAsync();
        Dispatcher.UIThread.RunJobs();

        // Center pane should still retain the resolver because KeepConflictResolverOpen is true and op is Merging
        Assert.NotNull(_conflictHost.TryGet(_context));
        Assert.True(_context.State.IsConflictSessionActive);
        Assert.Same(resolver, centerHost.Content);
    }

    [AvaloniaFact]
    public async Task Continue_that_stops_on_new_conflicts_keeps_same_resolver_instance()
    {
        _conflicts.Conflicts = [new ConflictFile { FilePath = "f.txt", Status = ConflictStatus.BothModified, IsBinary = false }];
        _conflicts.WorkingCopies["f.txt"] = new ConflictWorkingCopy { Path = "f.txt", Text = TwoConflictText, DiskHash = "hash1" };

        _fixture.SetOperation(OperationState.Rebasing);
        _fixture.CreateConflict();
        await _context.State.RefreshStatusAsync();

        var centerHost = new ContentControl();
        var graph = new Border();
        using var controller = new CenterPaneController(centerHost, graph, _conflictHost, _workspace, _sp);
        Dispatcher.UIThread.RunJobs();

        var firstInstance = _conflictHost.TryGet(_context);
        Assert.NotNull(firstInstance);
        Assert.Same(firstInstance, centerHost.Content);

        // Fake Continue that stops on new conflicts
        _conflicts.OnContinueAsync = async () =>
        {
            _fixture.SetOperation(OperationState.Rebasing);
            await _context.State.RefreshStatusAsync();
        };

        await firstInstance.ContinueAsync();
        Dispatcher.UIThread.RunJobs();

        var currentInstance = _conflictHost.TryGet(_context);
        Assert.Same(firstInstance, currentInstance);
        Assert.Same(currentInstance, centerHost.Content);
    }

    [AvaloniaFact]
    public async Task Bisecting_with_0_conflicts_shows_op_banner_without_continue_button()
    {
        var repoView = new RepositoryView(_sp);
        var window = new Window { Content = repoView };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        _fixture.SetOperation(OperationState.Bisecting);
        await _context.State.RefreshStatusAsync();
        await Task.Delay(100);
        Dispatcher.UIThread.RunJobs();

        Assert.True(repoView.OpBoxForTesting.IsVisible);
        Assert.False(repoView.OpContinueButtonForTesting.IsEffectivelyVisible);

        window.Close();
    }

    [AvaloniaFact]
    public async Task Dirty_resolver_shows_external_ended_banner_when_op_ends_outside_historia()
    {
        _conflicts.Conflicts = [new ConflictFile { FilePath = "f.txt", Status = ConflictStatus.BothModified, IsBinary = false }];
        _conflicts.WorkingCopies["f.txt"] = new ConflictWorkingCopy { Path = "f.txt", Text = TwoConflictText, DiskHash = "hash1" };

        _fixture.SetOperation(OperationState.Merging);
        await _context.State.RefreshStatusAsync();

        var view = new ConflictResolverView(_sp, _context);
        await view.LoadAsync();
        Dispatcher.UIThread.RunJobs();

        view.ResultEditorForTesting.Document.Insert(0, "edit\n");
        Assert.True(view.HasUnsavedWork);

        _fixture.ClearOperation();
        await _context.State.RefreshStatusAsync();
        Dispatcher.UIThread.RunJobs();
        await Task.Delay(100);
        Dispatcher.UIThread.RunJobs();

        Assert.True(view.ExternalEndedBannerForTesting.IsVisible);
    }

    [AvaloniaFact]
    public async Task Region_action_right_after_typing_acts_on_fresh_offsets()
    {
        _conflicts.Conflicts = [new ConflictFile { FilePath = "f.txt", Status = ConflictStatus.BothModified, IsBinary = false }];
        _conflicts.WorkingCopies["f.txt"] = new ConflictWorkingCopy { Path = "f.txt", Text = TwoConflictText, DiskHash = "hash1" };

        var view = new ConflictResolverView(_sp, _context);
        await view.LoadAsync();
        Dispatcher.UIThread.RunJobs();

        var r0 = view.CurrentParseForTesting!.Regions[0];
        // Insert text above region 0 without waiting for debounce
        view.ResultEditorForTesting.Document.Insert(0, "new top line\n");

        // Immediately trigger region action
        view.ApplyRegionAction(r0, ConflictResolverView.RegionActionChoice.Theirs);

        Assert.Contains("new top line", view.ResultEditorForTesting.Text);
        Assert.Contains("theirs 1", view.ResultEditorForTesting.Text);
        Assert.DoesNotContain("ours 1", view.ResultEditorForTesting.Text);
    }

    [AvaloniaFact]
    public async Task Damaged_markers_detected_when_separator_deleted()
    {
        const string malformed =
            "<<<<<<< HEAD\n" +
            "ours\n" +
            "theirs\n" +
            ">>>>>>> branch\n";

        _conflicts.Conflicts = [new ConflictFile { FilePath = "f.txt", Status = ConflictStatus.BothModified, IsBinary = false }];
        _conflicts.WorkingCopies["f.txt"] = new ConflictWorkingCopy { Path = "f.txt", Text = malformed, DiskHash = "hash1" };

        var view = new ConflictResolverView(_sp, _context);
        await view.LoadAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.Contains("damaged", view.DamagedMarkersHintForTesting.Text);
    }

    [AvaloniaFact]
    public async Task Status_changed_while_save_in_flight_skips_disk_check()
    {
        _conflicts.Conflicts = [new ConflictFile { FilePath = "f.txt", Status = ConflictStatus.BothModified, IsBinary = false }];
        _conflicts.WorkingCopies["f.txt"] = new ConflictWorkingCopy { Path = "f.txt", Text = TwoConflictText, DiskHash = "hash1" };

        var view = new ConflictResolverView(_sp, _context);
        await view.LoadAsync();
        Dispatcher.UIThread.RunJobs();

        _conflicts.OnBeforeSaveAsync = async () =>
        {
            await _context.State.RefreshStatusAsync();
        };

        await view.SaveCurrentFileAsync(stage: false);
        Dispatcher.UIThread.RunJobs();

        Assert.False(view.ChangedOnDiskBannerForTesting.IsVisible);
    }

    [AvaloniaFact]
    public async Task Submodule_conflict_shows_file_card_only()
    {
        _conflicts.Conflicts = [new ConflictFile { FilePath = "sub", Status = ConflictStatus.BothModified, IsBinary = false, IsSubmodule = true }];
        _conflicts.WorkingCopies["sub"] = new ConflictWorkingCopy { Path = "sub", DiskHash = "hashSub" };

        var view = new ConflictResolverView(_sp, _context);
        await view.LoadAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.IsType<StackPanel>(view.EditorAreaForTesting.Content);
    }

    [AvaloniaFact]
    public async Task Typing_character_in_common_line_updates_panes_incrementally()
    {
        _conflicts.Conflicts = [new ConflictFile { FilePath = "f.txt", Status = ConflictStatus.BothModified, IsBinary = false }];
        _conflicts.WorkingCopies["f.txt"] = new ConflictWorkingCopy { Path = "f.txt", Text = TwoConflictText, DiskHash = "hash1" };

        var view = new ConflictResolverView(_sp, _context);
        await view.LoadAsync();
        Dispatcher.UIThread.RunJobs();

        // Type character in line 0 ("header")
        view.ResultEditorForTesting.Document.Insert(6, "!");
        await Task.Delay(250);
        Dispatcher.UIThread.RunJobs();

        Assert.Contains("header!", view.OursEditorForTesting.Document.Text);
        Assert.Contains("header!", view.TheirsEditorForTesting.Document.Text);
    }

    [AvaloniaFact]
    public async Task Save_after_file_changed_on_disk_shows_banner_and_keep_mine_updates_hash()
    {
        _conflicts.Conflicts = [new ConflictFile { FilePath = "f.txt", Status = ConflictStatus.BothModified, IsBinary = false }];
        _conflicts.WorkingCopies["f.txt"] = new ConflictWorkingCopy { Path = "f.txt", Text = TwoConflictText, DiskHash = "hash1" };

        var view = new ConflictResolverView(_sp, _context);
        await view.LoadAsync();
        Dispatcher.UIThread.RunJobs();

        view.ResultEditorForTesting.Document.Insert(0, "edit\n");
        _conflicts.SaveResult = new ResolutionSaveResult { Saved = false, DiskHash = "hash-new-disk" };
        _conflicts.WorkingCopies["f.txt"] = new ConflictWorkingCopy { Path = "f.txt", Text = TwoConflictText, DiskHash = "hash-new-disk" };

        var saved = await view.SaveCurrentFileAsync(stage: false);
        Assert.False(saved);
        Assert.True(view.ChangedOnDiskBannerForTesting.IsVisible);

        // Keep mine
        await view.KeepMineAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.False(view.ChangedOnDiskBannerForTesting.IsVisible);
    }

    [AvaloniaFact]
    public async Task External_tool_with_dirty_buffer_prompts_and_cancels_if_rejected()
    {
        _conflicts.Conflicts = [new ConflictFile { FilePath = "f.txt", Status = ConflictStatus.BothModified, IsBinary = false }];
        _conflicts.WorkingCopies["f.txt"] = new ConflictWorkingCopy { Path = "f.txt", Text = TwoConflictText, DiskHash = "hash1" };

        var view = new ConflictResolverView(_sp, _context);
        await view.LoadAsync();
        Dispatcher.UIThread.RunJobs();

        view.ResultEditorForTesting.Document.Insert(0, "dirty\n");
        _dialogs.ChooseResult = -1; // Cancel

        await view.LaunchExternalToolAsync();

        Assert.Empty(_conflicts.ExternalToolCalls);
    }

    [AvaloniaFact]
    public async Task Take_theirs_confirmed_on_dirty_buffer_reloads_and_unregisters_unsaved()
    {
        _conflicts.Conflicts = [
            new ConflictFile { FilePath = "f.txt", Status = ConflictStatus.BothModified, IsBinary = false },
            new ConflictFile { FilePath = "g.txt", Status = ConflictStatus.BothModified, IsBinary = false }
        ];
        _conflicts.WorkingCopies["f.txt"] = new ConflictWorkingCopy { Path = "f.txt", Text = TwoConflictText, DiskHash = "hash1" };
        _conflicts.WorkingCopies["g.txt"] = new ConflictWorkingCopy { Path = "g.txt", Text = "g text", DiskHash = "hash-g" };

        var view = new ConflictResolverView(_sp, _context);
        await view.LoadAsync();
        Dispatcher.UIThread.RunJobs();

        view.ResultEditorForTesting.Document.Insert(0, "dirty\n");
        Assert.True(view.HasUnsavedWork);

        _dialogs.ConfirmDestructiveResult = true;

        await view.TakeTheirsCurrentAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.False(view.HasUnsavedWork);
        Assert.Equal("g.txt", view.CurrentFileForTesting?.FilePath);
        Assert.Equal("g text", view.ResultEditorForTesting.Text);
    }

    [AvaloniaFact]
    public async Task Dirty_buffer_survives_repository_view_reconstruction()
    {
        _conflicts.Conflicts = [new ConflictFile { FilePath = "f.txt", Status = ConflictStatus.BothModified, IsBinary = false }];
        _conflicts.WorkingCopies["f.txt"] = new ConflictWorkingCopy { Path = "f.txt", Text = TwoConflictText, DiskHash = "hash1" };

        _fixture.CreateConflict();
        await _context.State.RefreshStatusAsync();

        var centerHost = new ContentControl();
        var graph = new Border();
        var controller1 = new CenterPaneController(centerHost, graph, _conflictHost, _workspace, _sp);
        var repoView1 = new RepositoryView(_sp);
        var window = new Window { Content = repoView1 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var resolver = _conflictHost.TryGet(_context);
        Assert.NotNull(resolver);
        resolver.ResultEditorForTesting.Document.Insert(0, "preserved edit\n");
        Assert.True(resolver.HasUnsavedWork);

        // Dispose first RepositoryView (e.g. Settings navigation)
        repoView1.Dispose();
        controller1.Dispose();
        window.Content = null;
        Dispatcher.UIThread.RunJobs();

        // Resolver in host is not disposed
        var cachedResolver = _conflictHost.TryGet(_context);
        Assert.NotNull(cachedResolver);
        Assert.Same(resolver, cachedResolver);
        Assert.True(cachedResolver.HasUnsavedWork);

        // Mount second RepositoryView
        var controller2 = new CenterPaneController(centerHost, graph, _conflictHost, _workspace, _sp);
        var repoView2 = new RepositoryView(_sp);
        window.Content = repoView2;
        Dispatcher.UIThread.RunJobs();

        Assert.Contains("preserved edit", cachedResolver.ResultEditorForTesting.Text);
        Assert.True(cachedResolver.HasUnsavedWork);

        controller2.Dispose();
        window.Close();
    }

    [AvaloniaFact]
    public async Task Closing_context_disposes_resolver_on_thread_pool_thread_safely()
    {
        _conflicts.Conflicts = [new ConflictFile { FilePath = "f.txt", Status = ConflictStatus.BothModified, IsBinary = false }];
        _conflicts.WorkingCopies["f.txt"] = new ConflictWorkingCopy { Path = "f.txt", Text = TwoConflictText, DiskHash = "hash1" };

        var resolver = _conflictHost.GetOrCreate(_context);
        await resolver.LoadAsync();
        Dispatcher.UIThread.RunJobs();

        var ws = _sp.GetRequiredService<IWorkspaceService>();
        await Task.Run(async () =>
        {
            await ws.CloseAsync(_context);
        });

        Dispatcher.UIThread.RunJobs();
        await Task.Delay(100);
        Dispatcher.UIThread.RunJobs();

        Assert.Null(_conflictHost.TryGet(_context));
    }

    [AvaloniaFact]
    public async Task Stop_id_change_with_zero_conflicts_clears_keep_open()
    {
        _conflicts.Conflicts = [new ConflictFile { FilePath = "f.txt", Status = ConflictStatus.BothModified, IsBinary = false }];
        _conflicts.WorkingCopies["f.txt"] = new ConflictWorkingCopy { Path = "f.txt", Text = TwoConflictText, DiskHash = "hash1" };
        _conflicts.SideLabels = _conflicts.SideLabels with { StopId = "stop-1" };

        var view = new ConflictResolverView(_sp, _context);
        await view.LoadAsync();
        _context.State.SetKeepConflictResolverOpen(true);
        Dispatcher.UIThread.RunJobs();

        // Terminal continue moves sequencer to next stop with 0 conflicts
        _conflicts.SideLabels = _conflicts.SideLabels with { StopId = "stop-2" };
        _conflicts.Conflicts = [];
        await _context.State.RefreshStatusAsync();

        await Task.Delay(100);
        Dispatcher.UIThread.RunJobs();

        Assert.False(_context.State.KeepConflictResolverOpen);
    }

    [AvaloniaFact]
    public async Task Resolver_has_no_file_list()
    {
        _conflicts.Conflicts = [new ConflictFile { FilePath = "f.txt", Status = ConflictStatus.BothModified, IsBinary = false }];
        _conflicts.WorkingCopies["f.txt"] = new ConflictWorkingCopy { Path = "f.txt", Text = TwoConflictText, DiskHash = "hash1" };

        var view = new ConflictResolverView(_sp, _context);
        await view.LoadAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.Empty(view.GetLogicalDescendants().OfType<ListBox>());
    }

    [AvaloniaFact]
    public async Task Setting_SelectedConflictPath_switches_file()
    {
        _conflicts.Conflicts = [
            new ConflictFile { FilePath = "fileA.txt", Status = ConflictStatus.BothModified, IsBinary = false },
            new ConflictFile { FilePath = "fileB.txt", Status = ConflictStatus.BothModified, IsBinary = false }
        ];
        _conflicts.WorkingCopies["fileA.txt"] = new ConflictWorkingCopy { Path = "fileA.txt", Text = "TEXT A", DiskHash = "hashA" };
        _conflicts.WorkingCopies["fileB.txt"] = new ConflictWorkingCopy { Path = "fileB.txt", Text = "TEXT B", DiskHash = "hashB" };

        var view = new ConflictResolverView(_sp, _context);
        await view.LoadAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("fileA.txt", view.CurrentFileForTesting?.FilePath);
        Assert.Equal("TEXT A", view.ResultEditorForTesting.Text);

        _context.State.SetSelectedConflictPath("fileB.txt");
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("fileB.txt", view.CurrentFileForTesting?.FilePath);
        Assert.Equal("TEXT B", view.ResultEditorForTesting.Text);
    }

    [AvaloniaFact]
    public async Task Selecting_other_path_with_unsaved_edits_prompts_and_cancel_restores_selection()
    {
        _conflicts.Conflicts = [
            new ConflictFile { FilePath = "fileA.txt", Status = ConflictStatus.BothModified, IsBinary = false },
            new ConflictFile { FilePath = "fileB.txt", Status = ConflictStatus.BothModified, IsBinary = false }
        ];
        _conflicts.WorkingCopies["fileA.txt"] = new ConflictWorkingCopy { Path = "fileA.txt", Text = "TEXT A", DiskHash = "hashA" };
        _conflicts.WorkingCopies["fileB.txt"] = new ConflictWorkingCopy { Path = "fileB.txt", Text = "TEXT B", DiskHash = "hashB" };

        var view = new ConflictResolverView(_sp, _context);
        await view.LoadAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("fileA.txt", view.CurrentFileForTesting?.FilePath);

        view.ResultEditorForTesting.Text = "unsaved edits";
        Assert.True(view.HasUnsavedWork);

        _dialogs.ChooseResult = -1; // Cancel

        _context.State.SetSelectedConflictPath("fileB.txt");
        Dispatcher.UIThread.RunJobs();

        Assert.Single(_dialogs.ChooseCalls);
        Assert.Equal("fileA.txt", view.CurrentFileForTesting?.FilePath);
        Assert.Equal("fileA.txt", _context.State.SelectedConflictPath);
    }

    [AvaloniaFact]
    public async Task Resolving_a_file_advances_to_next_conflict_and_publishes_its_path()
    {
        _conflicts.Conflicts = [
            new ConflictFile { FilePath = "fileA.txt", Status = ConflictStatus.BothModified, IsBinary = false },
            new ConflictFile { FilePath = "fileB.txt", Status = ConflictStatus.BothModified, IsBinary = false }
        ];
        _conflicts.WorkingCopies["fileA.txt"] = new ConflictWorkingCopy { Path = "fileA.txt", Text = "TEXT A", DiskHash = "hashA" };
        _conflicts.WorkingCopies["fileB.txt"] = new ConflictWorkingCopy { Path = "fileB.txt", Text = "TEXT B", DiskHash = "hashB" };

        var view = new ConflictResolverView(_sp, _context);
        await view.LoadAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("fileA.txt", view.CurrentFileForTesting?.FilePath);
        Assert.Equal("fileA.txt", _context.State.SelectedConflictPath);

        await view.SaveCurrentFileAsync(stage: true);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("fileB.txt", view.CurrentFileForTesting?.FilePath);
        Assert.Equal("fileB.txt", _context.State.SelectedConflictPath);
    }

    [AvaloniaFact]
    public async Task Resolving_last_file_shows_all_resolved_card_not_editor()
    {
        _conflicts.Conflicts = [
            new ConflictFile { FilePath = "fileA.txt", Status = ConflictStatus.BothModified, IsBinary = false }
        ];
        _conflicts.WorkingCopies["fileA.txt"] = new ConflictWorkingCopy { Path = "fileA.txt", Text = "TEXT A", DiskHash = "hashA" };

        var view = new ConflictResolverView(_sp, _context);
        await view.LoadAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("fileA.txt", view.CurrentFileForTesting?.FilePath);

        await view.SaveCurrentFileAsync(stage: true);
        Dispatcher.UIThread.RunJobs();

        Assert.Null(view.CurrentFileForTesting);
        Assert.Null(_context.State.SelectedConflictPath);
        Assert.IsType<StackPanel>(view.EditorAreaForTesting.Content);
        var card = (StackPanel)view.EditorAreaForTesting.Content;
        Assert.Contains(card.Children.OfType<TextBlock>(), tb => tb.Text?.Contains("All conflicts resolved") == true);
    }

    [AvaloniaFact]
    public async Task Rapid_switch_A_then_B_ends_on_B()
    {
        var tcsA = new TaskCompletionSource<bool>();
        _conflicts.Conflicts = [
            new ConflictFile { FilePath = "a.txt", Status = ConflictStatus.BothModified, IsBinary = false },
            new ConflictFile { FilePath = "b.txt", Status = ConflictStatus.BothModified, IsBinary = false }
        ];
        _conflicts.WorkingCopies["a.txt"] = new ConflictWorkingCopy { Path = "a.txt", Text = "TEXT A", DiskHash = "hashA" };
        _conflicts.WorkingCopies["b.txt"] = new ConflictWorkingCopy { Path = "b.txt", Text = "TEXT B", DiskHash = "hashB" };

        _conflicts.OnLoadWorkingCopyAsync = async (path, ct) =>
        {
            if (path == "a.txt")
            {
                await tcsA.Task;
            }
        };

        var view = new ConflictResolverView(_sp, _context);
        // Start switch to A
        _context.State.SetSelectedConflictPath("a.txt");
        Dispatcher.UIThread.RunJobs();

        // While A is waiting on tcsA, switch to B
        _context.State.SetSelectedConflictPath("b.txt");
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("b.txt", view.CurrentFileForTesting?.FilePath);
        Assert.Equal("TEXT B", view.ResultEditorForTesting.Text);
        Assert.Equal("b.txt", _context.State.SelectedConflictPath);

        // Now release A
        tcsA.SetResult(true);
        Dispatcher.UIThread.RunJobs();

        // State remains B!
        Assert.Equal("b.txt", view.CurrentFileForTesting?.FilePath);
        Assert.Equal("TEXT B", view.ResultEditorForTesting.Text);
        Assert.Equal("b.txt", _context.State.SelectedConflictPath);
    }

    [AvaloniaFact]
    public void Showing_resolver_raises_SyntaxThemeHost_LiveCount_and_detaching_brings_it_down()
    {
        _conflicts.Conflicts = [new ConflictFile { FilePath = "f.txt", Status = ConflictStatus.BothModified, IsBinary = false }];
        _conflicts.WorkingCopies["f.txt"] = new ConflictWorkingCopy { Path = "f.txt", Text = TwoConflictText, DiskHash = "hash1" };

        var resolver = _conflictHost.GetOrCreate(_context);
        Assert.Equal(0, _syntaxHost.LiveCount);

        var window = new Window { Content = resolver };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(3, _syntaxHost.LiveCount);

        window.Content = null;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(0, _syntaxHost.LiveCount);

        window.Close();
    }

    [AvaloniaFact]
    public async Task Region_action_in_crlf_file_keeps_crlf()
    {
        string crlfConflict = "<<<<<<< HEAD\r\nours\r\n=======\r\ntheirs\r\n>>>>>>> branch\r\n";
        _conflicts.Conflicts = [new ConflictFile { FilePath = "f.txt", Status = ConflictStatus.BothModified, IsBinary = false }];
        _conflicts.WorkingCopies["f.txt"] = new ConflictWorkingCopy
        {
            Path = "f.txt",
            Text = crlfConflict,
            DiskHash = "hash1"
        };

        var view = new ConflictResolverView(_sp, _context);
        await view.LoadAsync();
        Dispatcher.UIThread.RunJobs();

        var r0 = view.CurrentParseForTesting!.Regions[0];
        view.ApplyRegionAction(r0, ConflictResolverView.RegionActionChoice.Ours);

        string result = view.ResultEditorForTesting.Text;
        Assert.Equal("ours\r\n", result);
        Assert.DoesNotContain("\r\r\n", result);
        Assert.DoesNotContain("\n\r\n", result);
    }

    [AvaloniaFact]
    public async Task Use_empty_side_leaves_no_blank_line()
    {
        string text = "before\n<<<<<<< HEAD\n=======\ntheirs line\n>>>>>>> branch\nafter\n";
        _conflicts.Conflicts = [new ConflictFile { FilePath = "f.txt", Status = ConflictStatus.BothModified, IsBinary = false }];
        _conflicts.WorkingCopies["f.txt"] = new ConflictWorkingCopy
        {
            Path = "f.txt",
            Text = text,
            DiskHash = "hash1"
        };

        var view = new ConflictResolverView(_sp, _context);
        await view.LoadAsync();
        Dispatcher.UIThread.RunJobs();

        var r0 = view.CurrentParseForTesting!.Regions[0];
        view.ApplyRegionAction(r0, ConflictResolverView.RegionActionChoice.Ours);

        string result = view.ResultEditorForTesting.Text;
        Assert.Equal("before\nafter\n", result);
    }

    [AvaloniaFact]
    public async Task Marker_size_attribute_is_used_for_parsing()
    {
        string textWith9Markers = "<<<<<<<<< HEAD\nours line\n=========\ntheirs line\n>>>>>>>>> branch\n";
        _conflicts.Conflicts = [new ConflictFile { FilePath = "f.txt", Status = ConflictStatus.BothModified, IsBinary = false }];
        _conflicts.WorkingCopies["f.txt"] = new ConflictWorkingCopy
        {
            Path = "f.txt",
            Text = textWith9Markers,
            MarkerSize = 9,
            DiskHash = "hash1"
        };

        var view = new ConflictResolverView(_sp, _context);
        await view.LoadAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.NotNull(view.CurrentParseForTesting);
        Assert.Single(view.CurrentParseForTesting.Regions);
        Assert.Empty(view.CurrentParseForTesting.MalformedAt);
    }

    [AvaloniaFact]
    public async Task Pane_lines_are_not_rebuilt_per_render()
    {
        _conflicts.Conflicts = [new ConflictFile { FilePath = "f.txt", Status = ConflictStatus.BothModified, IsBinary = false }];
        _conflicts.WorkingCopies["f.txt"] = new ConflictWorkingCopy
        {
            Path = "f.txt",
            Text = TwoConflictText,
            DiskHash = "hash1"
        };

        var view = new ConflictResolverView(_sp, _context);
        var window = new Window { Content = view, Width = 800, Height = 600 };
        window.Show();
        await view.LoadAsync();
        Dispatcher.UIThread.RunJobs();

        int countAfterLoad = ConflictPaneBuilder.BuildCallCount;
        Assert.True(countAfterLoad > 0);

        // Render twice
        view.OursEditorForTesting.TextArea.TextView.Redraw();
        view.TheirsEditorForTesting.TextArea.TextView.Redraw();
        Dispatcher.UIThread.RunJobs();

        view.OursEditorForTesting.TextArea.TextView.Redraw();
        view.TheirsEditorForTesting.TextArea.TextView.Redraw();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(countAfterLoad, ConflictPaneBuilder.BuildCallCount);

        window.Close();
    }

    [AvaloniaFact]
    public async Task Continue_with_all_resolved_runs_to_completion_without_cancellation()
    {
        _conflicts.Conflicts = [new ConflictFile { FilePath = "f.txt", Status = ConflictStatus.BothModified, IsBinary = false }];
        _conflicts.WorkingCopies["f.txt"] = new ConflictWorkingCopy { Path = "f.txt", Text = TwoConflictText, DiskHash = "hash1" };

        var view = new ConflictResolverView(_sp, _context);
        await view.LoadAsync();
        Dispatcher.UIThread.RunJobs();

        CancellationToken capturedToken = default;
        bool wasCancelledDuringRun = false;
        _conflicts.OnContinueWithTokenAsync = async (ct) =>
        {
            capturedToken = ct;
            await Task.Delay(50);
            wasCancelledDuringRun = ct.IsCancellationRequested;
        };

        var continueTask = view.ContinueAsync();
        view.Dispose(); // Disposing the view while continue is in flight must NOT cancel the token
        await continueTask;

        Assert.False(wasCancelledDuringRun);
        Assert.False(capturedToken.IsCancellationRequested);
    }

    [AvaloniaFact]
    public async Task Next_stop_updates_labels()
    {
        _conflicts.Conflicts = [new ConflictFile { FilePath = "f.txt", Status = ConflictStatus.BothModified, IsBinary = false }];
        _conflicts.WorkingCopies["f.txt"] = new ConflictWorkingCopy { Path = "f.txt", Text = TwoConflictText, DiskHash = "hash1" };
        _conflicts.SideLabels = _conflicts.SideLabels with { StopId = "stop-1", OursTitle = "Ours1", TheirsTitle = "Theirs1" };

        var view = new ConflictResolverView(_sp, _context);
        await view.LoadAsync();
        Dispatcher.UIThread.RunJobs();

        // Advance stop
        _conflicts.SideLabels = _conflicts.SideLabels with { StopId = "stop-2", OursTitle = "Ours2", TheirsTitle = "Theirs2" };
        await _context.State.RefreshStatusAsync();
        await Task.Delay(50);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("Ours: Ours2", view.GetLogicalDescendants().OfType<TextBlock>().FirstOrDefault(t => t.Text?.StartsWith("Ours:") == true)?.Text);
    }

    [AvaloniaFact]
    public async Task Same_path_reconflicting_at_next_stop_is_editable()
    {
        _conflicts.Conflicts = [new ConflictFile { FilePath = "f.txt", Status = ConflictStatus.BothModified, IsBinary = false }];
        _conflicts.WorkingCopies["f.txt"] = new ConflictWorkingCopy { Path = "f.txt", Text = TwoConflictText, DiskHash = "hash1" };
        _conflicts.SideLabels = _conflicts.SideLabels with { StopId = "stop-1" };

        var view = new ConflictResolverView(_sp, _context);
        await view.LoadAsync();
        Dispatcher.UIThread.RunJobs();

        // Mark resolved in stop 1
        await view.SaveCurrentFileAsync(stage: true);
        Dispatcher.UIThread.RunJobs();

        // Advance to stop 2, where f.txt is conflicted again
        _conflicts.SideLabels = _conflicts.SideLabels with { StopId = "stop-2" };
        _conflicts.Conflicts = [new ConflictFile { FilePath = "f.txt", Status = ConflictStatus.BothModified, IsBinary = false }];
        _conflicts.WorkingCopies["f.txt"] = new ConflictWorkingCopy { Path = "f.txt", Text = TwoConflictText, DiskHash = "hash2" };
        await _context.State.RefreshStatusAsync();
        await Task.Delay(50);
        Dispatcher.UIThread.RunJobs();

        Assert.False(view.ResultEditorForTesting.IsReadOnly);
    }

    [AvaloniaFact]
    public async Task Skip_requires_destructive_confirm()
    {
        _conflicts.Conflicts = [new ConflictFile { FilePath = "f.txt", Status = ConflictStatus.BothModified, IsBinary = false }];
        _conflicts.WorkingCopies["f.txt"] = new ConflictWorkingCopy { Path = "f.txt", Text = TwoConflictText, DiskHash = "hash1" };
        _conflicts.SideLabels = _conflicts.SideLabels with { TheirsDetail = "abc1234 Add feature" };

        var view = new ConflictResolverView(_sp, _context);
        await view.LoadAsync();
        Dispatcher.UIThread.RunJobs();

        // Cancel confirmation
        _dialogs.ConfirmDestructiveResult = false;
        await view.SkipAsync();
        Assert.Single(_dialogs.ConfirmDestructiveCalls);
        Assert.Equal("Skip commit", _dialogs.ConfirmDestructiveCalls[0].Title);
        Assert.Contains("abc1234 Add feature", _dialogs.ConfirmDestructiveCalls[0].Effect);
        Assert.Equal("Skip", _dialogs.ConfirmDestructiveCalls[0].ConfirmLabel);
        Assert.Equal(0, _conflicts.SkipCalls);

        // Accept confirmation
        _dialogs.ConfirmDestructiveResult = true;
        await view.SkipAsync();
        Assert.Equal(1, _conflicts.SkipCalls);
    }

    [AvaloniaFact]
    public async Task Take_deleted_side_names_delete_effect()
    {
        // Test DeletedByUs + Ours -> Delete effect
        _conflicts.Conflicts = [new ConflictFile { FilePath = "f.txt", Status = ConflictStatus.DeletedByUs, IsBinary = false }];
        _conflicts.WorkingCopies["f.txt"] = new ConflictWorkingCopy { Path = "f.txt", Text = TwoConflictText, DiskHash = "hash1" };
        _conflicts.SideLabels = _conflicts.SideLabels with { OursTitle = "HEAD" };

        var view = new ConflictResolverView(_sp, _context);
        await view.LoadAsync();
        Dispatcher.UIThread.RunJobs();

        _dialogs.ConfirmDestructiveCalls.Clear();
        _dialogs.ConfirmDestructiveResult = false;

        await view.TakeOursCurrentAsync();
        Assert.Single(_dialogs.ConfirmDestructiveCalls);
        Assert.Equal("Delete f.txt", _dialogs.ConfirmDestructiveCalls[0].Title);
        Assert.Contains("Delete f.txt — HEAD deleted it", _dialogs.ConfirmDestructiveCalls[0].Effect);
        Assert.Equal("Delete", _dialogs.ConfirmDestructiveCalls[0].ConfirmLabel);

        // Test DeletedByThem + Theirs -> Delete effect
        _conflicts.Conflicts = [new ConflictFile { FilePath = "g.txt", Status = ConflictStatus.DeletedByThem, IsBinary = false }];
        _conflicts.WorkingCopies["g.txt"] = new ConflictWorkingCopy { Path = "g.txt", Text = TwoConflictText, DiskHash = "hash2" };
        _conflicts.SideLabels = _conflicts.SideLabels with { TheirsTitle = "feature" };

        await view.LoadAsync();
        Dispatcher.UIThread.RunJobs();

        _dialogs.ConfirmDestructiveCalls.Clear();
        await view.TakeTheirsCurrentAsync();
        Assert.Single(_dialogs.ConfirmDestructiveCalls);
        Assert.Equal("Delete g.txt", _dialogs.ConfirmDestructiveCalls[0].Title);
        Assert.Contains("Delete g.txt — feature deleted it", _dialogs.ConfirmDestructiveCalls[0].Effect);
        Assert.Equal("Delete", _dialogs.ConfirmDestructiveCalls[0].ConfirmLabel);
    }

    [AvaloniaFact]
    public async Task Keep_mine_requires_destructive_confirm()
    {
        _conflicts.Conflicts = [new ConflictFile { FilePath = "f.txt", Status = ConflictStatus.BothModified, IsBinary = false }];
        _conflicts.WorkingCopies["f.txt"] = new ConflictWorkingCopy { Path = "f.txt", Text = TwoConflictText, DiskHash = "hash1" };

        var view = new ConflictResolverView(_sp, _context);
        await view.LoadAsync();
        Dispatcher.UIThread.RunJobs();

        _dialogs.ConfirmDestructiveCalls.Clear();
        _dialogs.ConfirmDestructiveResult = true;

        _conflicts.WorkingCopies["f.txt"] = new ConflictWorkingCopy { Path = "f.txt", Text = TwoConflictText, DiskHash = "hash-from-disk" };
        await view.KeepMineAsync();

        Assert.Single(_dialogs.ConfirmDestructiveCalls);
        Assert.Equal("Keep my version", _dialogs.ConfirmDestructiveCalls[0].Title);
        Assert.Contains("Your next save will overwrite f.txt on disk", _dialogs.ConfirmDestructiveCalls[0].Effect);
        Assert.Equal("Keep mine", _dialogs.ConfirmDestructiveCalls[0].ConfirmLabel);
    }

    [AvaloniaFact]
    public async Task StopChange_with_unsaved_edits_keeps_text_and_shows_banner()
    {
        _conflicts.Conflicts = [new ConflictFile { FilePath = "f.txt", Status = ConflictStatus.BothModified, IsBinary = false }];
        _conflicts.WorkingCopies["f.txt"] = new ConflictWorkingCopy { Path = "f.txt", Text = TwoConflictText, DiskHash = "hash1" };
        _conflicts.SideLabels = _conflicts.SideLabels with { StopId = "stop-1" };

        var view = new ConflictResolverView(_sp, _context);
        await view.LoadAsync();
        Dispatcher.UIThread.RunJobs();

        view.ResultEditorForTesting.Document.Insert(0, "unsaved edit\n");
        Assert.True(view.HasUnsavedWork);

        _conflicts.SideLabels = _conflicts.SideLabels with { StopId = "stop-2" };
        _conflicts.WorkingCopies["f.txt"] = new ConflictWorkingCopy { Path = "f.txt", Text = "different disk text", DiskHash = "hash2" };

        await _context.State.RefreshStatusAsync();
        await Task.Delay(100);
        Dispatcher.UIThread.RunJobs();

        Assert.Contains("unsaved edit", view.ResultEditorForTesting.Text);
        Assert.True(view.ChangedOnDiskBannerForTesting.IsVisible);
    }

    [AvaloniaFact]
    public async Task StopChange_with_unsaved_edits_to_other_file_keeps_text()
    {
        _conflicts.Conflicts = [new ConflictFile { FilePath = "f.txt", Status = ConflictStatus.BothModified, IsBinary = false }];
        _conflicts.WorkingCopies["f.txt"] = new ConflictWorkingCopy { Path = "f.txt", Text = TwoConflictText, DiskHash = "hash1" };
        _conflicts.SideLabels = _conflicts.SideLabels with { StopId = "stop-1" };

        var view = new ConflictResolverView(_sp, _context);
        await view.LoadAsync();
        Dispatcher.UIThread.RunJobs();

        view.ResultEditorForTesting.Document.Insert(0, "unsaved edit in f\n");
        Assert.True(view.HasUnsavedWork);

        _conflicts.Conflicts = [new ConflictFile { FilePath = "g.txt", Status = ConflictStatus.BothModified, IsBinary = false }];
        _conflicts.WorkingCopies["g.txt"] = new ConflictWorkingCopy { Path = "g.txt", Text = "g content", DiskHash = "hash-g" };
        _conflicts.SideLabels = _conflicts.SideLabels with { StopId = "stop-2" };

        await _context.State.RefreshStatusAsync();
        await Task.Delay(100);
        Dispatcher.UIThread.RunJobs();

        Assert.Contains("unsaved edit in f", view.ResultEditorForTesting.Text);
        Assert.Equal("f.txt", view.CurrentFileForTesting?.FilePath);
        Assert.True(view.ChangedOnDiskBannerForTesting.IsVisible);
    }

    [AvaloniaFact]
    public async Task StopChange_without_unsaved_edits_reloads()
    {
        _conflicts.Conflicts = [new ConflictFile { FilePath = "f.txt", Status = ConflictStatus.BothModified, IsBinary = false }];
        _conflicts.WorkingCopies["f.txt"] = new ConflictWorkingCopy { Path = "f.txt", Text = TwoConflictText, DiskHash = "hash1" };
        _conflicts.SideLabels = _conflicts.SideLabels with { StopId = "stop-1" };

        var view = new ConflictResolverView(_sp, _context);
        await view.LoadAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.False(view.HasUnsavedWork);

        _conflicts.SideLabels = _conflicts.SideLabels with { StopId = "stop-2" };
        _conflicts.WorkingCopies["f.txt"] = new ConflictWorkingCopy { Path = "f.txt", Text = "reloaded stop 2 text", DiskHash = "hash2" };

        await _context.State.RefreshStatusAsync();
        await Task.Delay(100);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("reloaded stop 2 text", view.ResultEditorForTesting.Text);
        Assert.False(view.ChangedOnDiskBannerForTesting.IsVisible);
    }

    [AvaloniaFact]
    public async Task Continue_with_unsaved_edits_prompts_choice_0_saves_and_continues()
    {
        _conflicts.Conflicts = [new ConflictFile { FilePath = "f.txt", Status = ConflictStatus.BothModified, IsBinary = false }];
        _conflicts.WorkingCopies["f.txt"] = new ConflictWorkingCopy { Path = "f.txt", Text = TwoConflictText, DiskHash = "hash1" };

        var view = new ConflictResolverView(_sp, _context);
        await view.LoadAsync();
        Dispatcher.UIThread.RunJobs();

        view.ResultEditorForTesting.Document.Insert(0, "dirty\n");
        Assert.True(view.HasUnsavedWork);

        _dialogs.ChooseResult = 0; // Save & mark resolved
        _dialogs.ConfirmResult = true; // Confirm remaining conflicts if prompted
        _conflicts.SaveResolutionCalls.Clear();
        _conflicts.ContinueCalls = 0;

        await view.ContinueAsync();

        Assert.Single(_dialogs.ChooseCalls);
        Assert.Single(_conflicts.SaveResolutionCalls);
        Assert.True(_conflicts.SaveResolutionCalls[0].Stage);
        Assert.Equal(1, _conflicts.ContinueCalls);
    }

    [AvaloniaFact]
    public async Task Continue_with_unsaved_edits_prompts_choice_1_discards_reloads_disk_text_and_continues()
    {
        _conflicts.Conflicts = [new ConflictFile { FilePath = "f.txt", Status = ConflictStatus.BothModified, IsBinary = false }];
        _conflicts.WorkingCopies["f.txt"] = new ConflictWorkingCopy { Path = "f.txt", Text = "clean disk text", DiskHash = "hash1" };

        var view = new ConflictResolverView(_sp, _context);
        await view.LoadAsync();
        Dispatcher.UIThread.RunJobs();

        view.ResultEditorForTesting.Document.Insert(0, "dirty\n");
        Assert.True(view.HasUnsavedWork);

        _dialogs.ChooseResult = 1; // Discard changes
        _conflicts.SaveResolutionCalls.Clear();
        _conflicts.ContinueCalls = 0;

        await view.ContinueAsync();

        Assert.Single(_dialogs.ChooseCalls);
        Assert.Empty(_conflicts.SaveResolutionCalls);
        Assert.Equal("clean disk text", view.ResultEditorForTesting.Text);
        Assert.False(view.HasUnsavedWork);
        Assert.Equal(1, _conflicts.ContinueCalls);
    }

    [AvaloniaFact]
    public async Task Continue_with_unsaved_edits_prompts_choice_negative_1_cancels()
    {
        _conflicts.Conflicts = [new ConflictFile { FilePath = "f.txt", Status = ConflictStatus.BothModified, IsBinary = false }];
        _conflicts.WorkingCopies["f.txt"] = new ConflictWorkingCopy { Path = "f.txt", Text = "disk text", DiskHash = "hash1" };

        var view = new ConflictResolverView(_sp, _context);
        await view.LoadAsync();
        Dispatcher.UIThread.RunJobs();

        view.ResultEditorForTesting.Document.Insert(0, "dirty\n");
        Assert.True(view.HasUnsavedWork);

        _dialogs.ChooseResult = -1; // Cancel
        _conflicts.ContinueCalls = 0;

        await view.ContinueAsync();

        Assert.Single(_dialogs.ChooseCalls);
        Assert.Equal(0, _conflicts.ContinueCalls);
        Assert.True(view.HasUnsavedWork);
        Assert.Contains("dirty", view.ResultEditorForTesting.Text);
    }

    [AvaloniaFact]
    public async Task Continue_discard_then_failed_continue_leaves_disk_text_in_editor()
    {
        _conflicts.Conflicts = [new ConflictFile { FilePath = "f.txt", Status = ConflictStatus.BothModified, IsBinary = false }];
        _conflicts.WorkingCopies["f.txt"] = new ConflictWorkingCopy { Path = "f.txt", Text = "clean disk text", DiskHash = "hash1" };

        var view = new ConflictResolverView(_sp, _context);
        await view.LoadAsync();
        Dispatcher.UIThread.RunJobs();

        view.ResultEditorForTesting.Document.Insert(0, "dirty\n");
        Assert.True(view.HasUnsavedWork);

        _dialogs.ChooseResult = 1; // Discard
        _conflicts.OnContinueAsync = () => throw new Exception("Continue failed on git error");

        await view.ContinueAsync();

        Assert.Equal("clean disk text", view.ResultEditorForTesting.Text);
        Assert.False(view.HasUnsavedWork);
    }

    [AvaloniaFact]
    public async Task StopChange_refresh_does_not_prompt()
    {
        _conflicts.Conflicts = [new ConflictFile { FilePath = "f.txt", Status = ConflictStatus.BothModified, IsBinary = false }];
        _conflicts.WorkingCopies["f.txt"] = new ConflictWorkingCopy { Path = "f.txt", Text = TwoConflictText, DiskHash = "hash1" };
        _conflicts.SideLabels = _conflicts.SideLabels with { StopId = "stop-1" };

        var view = new ConflictResolverView(_sp, _context);
        await view.LoadAsync();
        Dispatcher.UIThread.RunJobs();

        view.ResultEditorForTesting.Document.Insert(0, "dirty edits\n");
        Assert.True(view.HasUnsavedWork);

        _dialogs.ChooseCalls.Clear();

        // Stop change where current file drops out of the list
        _conflicts.Conflicts = [new ConflictFile { FilePath = "other.txt", Status = ConflictStatus.BothModified, IsBinary = false }];
        _conflicts.WorkingCopies["other.txt"] = new ConflictWorkingCopy { Path = "other.txt", Text = "other text", DiskHash = "hash-other" };
        _conflicts.SideLabels = _conflicts.SideLabels with { StopId = "stop-2" };

        await _context.State.RefreshStatusAsync();
        await Task.Delay(100);
        Dispatcher.UIThread.RunJobs();

        Assert.Empty(_dialogs.ChooseCalls);
        Assert.Contains("dirty edits", view.ResultEditorForTesting.Text);
        Assert.True(view.HasUnsavedWork);
    }

    [AvaloniaFact]
    public async Task Skip_clears_unsaved_registration_on_success()
    {
        _conflicts.Conflicts = [new ConflictFile { FilePath = "f.txt", Status = ConflictStatus.BothModified, IsBinary = false }];
        _conflicts.WorkingCopies["f.txt"] = new ConflictWorkingCopy { Path = "f.txt", Text = TwoConflictText, DiskHash = "hash1" };

        var view = new ConflictResolverView(_sp, _context);
        await view.LoadAsync();
        Dispatcher.UIThread.RunJobs();

        view.ResultEditorForTesting.Document.Insert(0, "dirty\n");
        Assert.True(view.HasUnsavedWork);

        _dialogs.ConfirmDestructiveResult = true;
        await view.SkipAsync();

        Assert.False(view.HasUnsavedWork);
        Assert.False(view.ChangedOnDiskBannerForTesting.IsVisible);
        Assert.Equal(1, _conflicts.SkipCalls);
    }

    [AvaloniaFact]
    public async Task Abort_clears_unsaved_registration_on_success()
    {
        _conflicts.Conflicts = [new ConflictFile { FilePath = "f.txt", Status = ConflictStatus.BothModified, IsBinary = false }];
        _conflicts.WorkingCopies["f.txt"] = new ConflictWorkingCopy { Path = "f.txt", Text = TwoConflictText, DiskHash = "hash1" };

        var view = new ConflictResolverView(_sp, _context);
        await view.LoadAsync();
        Dispatcher.UIThread.RunJobs();

        view.ResultEditorForTesting.Document.Insert(0, "dirty\n");
        Assert.True(view.HasUnsavedWork);

        _dialogs.ConfirmDestructiveCalls.Clear();
        _dialogs.ConfirmDestructiveResult = true;
        _conflicts.AbortCalls = 0;

        await view.AbortAsync();

        Assert.False(view.HasUnsavedWork);
        Assert.False(view.ChangedOnDiskBannerForTesting.IsVisible);
        Assert.Equal(1, _conflicts.AbortCalls);
        Assert.Single(_dialogs.ConfirmDestructiveCalls);
        Assert.Contains("Conflict resolutions and unsaved edits made so far will be discarded.", _dialogs.ConfirmDestructiveCalls[0].Effect);
    }

    [AvaloniaFact]
    public async Task Busy_disables_action_buttons_and_ignores_reentry()
    {
        _conflicts.Conflicts = [new ConflictFile { FilePath = "f.txt", Status = ConflictStatus.BothModified, IsBinary = false }];
        _conflicts.WorkingCopies["f.txt"] = new ConflictWorkingCopy
        {
            Path = "f.txt",
            Text = TwoConflictText,
            DiskHash = "hash1",
            Format = new TextFileFormat(System.Text.Encoding.UTF8, false),
        };

        var view = new ConflictResolverView(_sp, _context);
        await view.LoadAsync();
        Dispatcher.UIThread.RunJobs();

        var tcs = new TaskCompletionSource<bool>();
        _conflicts.OnContinueAsync = () => tcs.Task;
        _conflicts.ContinueCalls = 0;

        var continueTask = view.ContinueAsync();

        Assert.True(view.IsBusy);
        Assert.NotEmpty(view.ActionButtonsForTesting);
        Assert.All(view.ActionButtonsForTesting, btn => Assert.False(btn.IsEnabled));
        Assert.NotEmpty(view.TextActionButtonsForTesting);
        Assert.All(view.TextActionButtonsForTesting, btn => Assert.False(btn.IsEnabled));

        // Re-entry calls should be ignored while busy
        await view.ContinueAsync();
        await view.SkipAsync();
        await view.AbortAsync();
        var saveResult = await view.SaveCurrentFileAsync(false);
        Assert.False(saveResult);

        Assert.Equal(1, _conflicts.ContinueCalls);
        Assert.Empty(_conflicts.SaveResolutionCalls);

        tcs.SetResult(true);
        await continueTask;

        Assert.False(view.IsBusy);
        Assert.All(view.ActionButtonsForTesting, btn => Assert.True(btn.IsEnabled));
        Assert.All(view.TextActionButtonsForTesting, btn => Assert.True(btn.IsEnabled));
    }

    [AvaloniaFact]
    public async Task External_change_check_uses_GetDiskHash_not_LoadWorkingCopy()
    {
        _conflicts.Conflicts = [new ConflictFile { FilePath = "f.txt", Status = ConflictStatus.BothModified, IsBinary = false }];
        _conflicts.WorkingCopies["f.txt"] = new ConflictWorkingCopy { Path = "f.txt", Text = TwoConflictText, DiskHash = "hash1" };

        var view = new ConflictResolverView(_sp, _context);
        await view.LoadAsync();
        Dispatcher.UIThread.RunJobs();

        _conflicts.GetDiskHashCalls = 0;
        _conflicts.LoadWorkingCopyCalls = 0;

        await _context.State.RefreshStatusAsync();
        Dispatcher.UIThread.RunJobs();
        await Task.Delay(100);
        Dispatcher.UIThread.RunJobs();

        Assert.True(_conflicts.GetDiskHashCalls >= 1);
        Assert.Equal(0, _conflicts.LoadWorkingCopyCalls);
    }

    [AvaloniaFact]
    public async Task Overlapping_status_changes_run_refresh_at_most_twice()
    {
        _conflicts.Conflicts = [new ConflictFile { FilePath = "f.txt", Status = ConflictStatus.BothModified, IsBinary = false }];
        _conflicts.WorkingCopies["f.txt"] = new ConflictWorkingCopy { Path = "f.txt", Text = TwoConflictText, DiskHash = "hash1" };

        var view = new ConflictResolverView(_sp, _context);
        await view.LoadAsync();
        Dispatcher.UIThread.RunJobs();

        var tcs = new TaskCompletionSource<IReadOnlyList<ConflictFile>>();
        int callCount = 0;
        _conflicts.OnGetConflictsAsync = () =>
        {
            callCount++;
            if (callCount == 1)
            {
                return tcs.Task;
            }
            return Task.FromResult(_conflicts.Conflicts);
        };

        // Fire status change 1 and wait for coalesce timer (50ms)
        await _context.State.RefreshStatusAsync();
        Dispatcher.UIThread.RunJobs();
        await Task.Delay(100);
        Dispatcher.UIThread.RunJobs();

        // Refresh 1 is now in-flight, awaiting tcs.Task in GetConflictsAsync
        Assert.Equal(1, callCount);

        // Fire status changes 2 and 3 while 1 is in-flight
        await _context.State.RefreshStatusAsync();
        Dispatcher.UIThread.RunJobs();
        await Task.Delay(100);
        Dispatcher.UIThread.RunJobs();

        await _context.State.RefreshStatusAsync();
        Dispatcher.UIThread.RunJobs();
        await Task.Delay(100);
        Dispatcher.UIThread.RunJobs();

        // Still only 1 call to GetConflictsAsync because refresh is in flight and extra ones folded into pending
        Assert.Equal(1, callCount);

        // Complete the in-flight refresh 1
        tcs.SetResult(_conflicts.Conflicts);
        Dispatcher.UIThread.RunJobs();
        await Task.Delay(50);
        Dispatcher.UIThread.RunJobs();

        // Now the second (pending) refresh should have run and finished
        Assert.Equal(2, callCount);
    }

    [AvaloniaFact]
    public async Task SwitchToFile_builds_panes_once()
    {
        _conflicts.Conflicts = [
            new ConflictFile { FilePath = "f.txt", Status = ConflictStatus.BothModified, IsBinary = false },
            new ConflictFile { FilePath = "g.txt", Status = ConflictStatus.BothModified, IsBinary = false }
        ];
        _conflicts.WorkingCopies["f.txt"] = new ConflictWorkingCopy { Path = "f.txt", Text = TwoConflictText, DiskHash = "hash1" };
        _conflicts.WorkingCopies["g.txt"] = new ConflictWorkingCopy { Path = "g.txt", Text = TwoConflictText, DiskHash = "hash2" };

        var view = new ConflictResolverView(_sp, _context);
        await view.LoadAsync();
        Dispatcher.UIThread.RunJobs();

        await Task.Delay(250);
        Dispatcher.UIThread.RunJobs();

        int initialCount = ConflictPaneBuilder.BuildCallCount;

        _context.State.SetSelectedConflictPath("g.txt");
        Dispatcher.UIThread.RunJobs();

        // Wait well past debounce (150ms)
        await Task.Delay(300);
        Dispatcher.UIThread.RunJobs();

        int finalCount = ConflictPaneBuilder.BuildCallCount;
        Assert.Equal(2, finalCount - initialCount);
    }

    [AvaloniaFact]
    public async Task Region_analysis_arrives_after_switch_and_is_dropped_if_document_changed()
    {
        _conflicts.Conflicts = [
            new ConflictFile { FilePath = "f.txt", Status = ConflictStatus.BothModified, IsBinary = false },
            new ConflictFile { FilePath = "g.txt", Status = ConflictStatus.BothModified, IsBinary = false }
        ];
        _conflicts.WorkingCopies["f.txt"] = new ConflictWorkingCopy { Path = "f.txt", Text = TwoConflictText, DiskHash = "hash1" };
        _conflicts.WorkingCopies["g.txt"] = new ConflictWorkingCopy { Path = "g.txt", Text = TwoConflictText, DiskHash = "hash2" };

        var view = new ConflictResolverView(_sp, _context);
        await view.LoadAsync();
        // Immediately after load, carried analyses is empty
        Assert.NotNull(view.SnapshotForTesting);
        Assert.Empty(view.SnapshotForTesting.Analyses);

        // Part 1: Region analysis arrives after switch
        for (int i = 0; i < 50 && (view.SnapshotForTesting?.Analyses.Count ?? 0) == 0; i++)
        {
            await Task.Delay(20);
            Dispatcher.UIThread.RunJobs();
        }
        Assert.Equal(2, view.SnapshotForTesting!.Analyses.Count);

        // Part 2: Switch to g.txt and immediately change document so version moves
        // The switch runs as a dispatcher job and starts region analysis on the thread pool, whose
        // result is posted back to the same queue. Queue the edit right behind the switch so it is
        // always ahead of that post, instead of racing the analysis after RunJobs returns.
        var versionBeforeSwitch = view.ResultEditorForTesting.Document.Version;
        int analysesAtSwitch = -1;
        _context.State.SetSelectedConflictPath("g.txt");
        Dispatcher.UIThread.Post(() =>
        {
            // Right after the switch the snapshot carries no analyses
            analysesAtSwitch = view.SnapshotForTesting!.Analyses.Count;

            // Move document version before background analysis posts back
            view.ResultEditorForTesting.Document.Insert(0, "immediate change\n");
        });
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0, analysesAtSwitch);

        // Wait a short time for the switch background task to finish and post to dispatcher
        await Task.Delay(30);
        Dispatcher.UIThread.RunJobs();

        // The background analysis that targeted the pre-edit version was dropped (version moved),
        // and debounce (150ms) hasn't fired yet, so snapshot analyses is still empty!
        Assert.Empty(view.SnapshotForTesting!.Analyses);

        // After waiting past debounce (150ms), the debounced analysis arrives
        await Task.Delay(200);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(2, view.SnapshotForTesting!.Analyses.Count);
        Assert.True(view.ResultEditorForTesting.Document.Version.CompareAge(versionBeforeSwitch) > 0);
    }

    [AvaloniaFact]
    public async Task Region_action_keeps_highlights_of_untouched_regions()
    {
        _conflicts.Conflicts = [new ConflictFile { FilePath = "f.txt", Status = ConflictStatus.BothModified, IsBinary = false }];
        _conflicts.WorkingCopies["f.txt"] = new ConflictWorkingCopy { Path = "f.txt", Text = TwoConflictText, DiskHash = "hash1" };

        var view = new ConflictResolverView(_sp, _context);
        await view.LoadAsync();
        Dispatcher.UIThread.RunJobs();

        for (int i = 0; i < 50 && (view.SnapshotForTesting?.Analyses.Count ?? 0) < 2; i++)
        {
            await Task.Delay(20);
            Dispatcher.UIThread.RunJobs();
        }

        Assert.NotNull(view.SnapshotForTesting);
        Assert.Equal(2, view.SnapshotForTesting.Analyses.Count);

        var parse = view.CurrentParseForTesting!;
        Assert.Equal(2, parse.Regions.Count);

        // Apply "Use ours" on Region 1
        view.ApplyRegionAction(parse.Regions[0], ConflictResolverView.RegionActionChoice.Ours);

        // Immediately after ApplyRegionAction (before any background parse finishes)
        var snapshot = view.SnapshotForTesting;
        Assert.NotNull(snapshot);
        Assert.Single(snapshot.Analyses);
        Assert.Equal("theirs 2", snapshot.Analyses[0].Region.TheirsLines[0]);
        Assert.NotEmpty(snapshot.Analyses[0].OursHighlights);
    }

    [AvaloniaFact]
    public async Task File_card_disables_text_actions()
    {
        _conflicts.Conflicts = [new ConflictFile { FilePath = "binary.png", Status = ConflictStatus.BothModified, IsBinary = true }];
        _conflicts.WorkingCopies["binary.png"] = new ConflictWorkingCopy
        {
            Path = "binary.png",
            IsBinary = true,
            DiskHash = "hash1"
        };

        var view = new ConflictResolverView(_sp, _context);
        await view.LoadAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.False(view.IsEditableForTesting);
        Assert.NotEmpty(view.TextActionButtonsForTesting);
        Assert.All(view.TextActionButtonsForTesting, b => Assert.False(b.IsEnabled));
    }

    [AvaloniaFact]
    public async Task File_card_keeps_take_recreate_and_external_toolbar_actions_enabled()
    {
        _conflicts.Conflicts = [new ConflictFile { FilePath = "binary.png", Status = ConflictStatus.BothModified, IsBinary = true }];
        _conflicts.WorkingCopies["binary.png"] = new ConflictWorkingCopy
        {
            Path = "binary.png",
            IsBinary = true,
            DiskHash = "hash1"
        };

        var view = new ConflictResolverView(_sp, _context);
        await view.LoadAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.False(view.IsEditableForTesting);
        Assert.NotEmpty(view.TextActionButtonsForTesting);
        Assert.All(view.TextActionButtonsForTesting, b => Assert.False(b.IsEnabled));

        var targetLabels = new[] { "Take ours", "Take theirs", "Re-create with base", "External tool" };
        var toolbarActions = view.ActionButtonsForTesting
            .Where(b => targetLabels.Contains(b.Content as string))
            .ToList();
        Assert.Equal(4, toolbarActions.Count);
        Assert.All(toolbarActions, b => Assert.True(b.IsEnabled));
    }

    [AvaloniaFact]
    public async Task Toolbar_recreate_on_a_card_asks_for_confirmation_and_recreates()
    {
        _conflicts.Conflicts = [new ConflictFile { FilePath = "binary.png", Status = ConflictStatus.BothModified, IsBinary = true }];
        _conflicts.WorkingCopies["binary.png"] = new ConflictWorkingCopy
        {
            Path = "binary.png",
            IsBinary = true,
            DiskHash = "hash1"
        };

        var view = new ConflictResolverView(_sp, _context);
        await view.LoadAsync();
        Dispatcher.UIThread.RunJobs();

        _dialogs.ConfirmDestructiveResult = true;
        var recreateButton = view.ActionButtonsForTesting.Single(b => b.Content as string == "Re-create with base");
        Assert.True(recreateButton.IsEnabled);
        recreateButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (_conflicts.RecreatedCalls.Count == 0 && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }

        Assert.Single(_dialogs.ConfirmDestructiveCalls);
        Assert.Equal("Re-create conflict", _dialogs.ConfirmDestructiveCalls[0].Title);
        Assert.Single(_conflicts.RecreatedCalls);
        Assert.Equal("binary.png", _conflicts.RecreatedCalls[0]);
    }

    [AvaloniaFact]
    public async Task Save_on_file_card_never_calls_SaveResolutionAsync()
    {
        _conflicts.Conflicts = [new ConflictFile { FilePath = "binary.png", Status = ConflictStatus.BothModified, IsBinary = true }];
        _conflicts.WorkingCopies["binary.png"] = new ConflictWorkingCopy
        {
            Path = "binary.png",
            IsBinary = true,
            DiskHash = "hash1"
        };

        var view = new ConflictResolverView(_sp, _context);
        await view.LoadAsync();
        Dispatcher.UIThread.RunJobs();

        var saved = await view.SaveCurrentFileAsync(stage: false);
        Assert.False(saved);
        Assert.Empty(_conflicts.SaveResolutionCalls);
    }

    [AvaloniaFact]
    public async Task All_resolved_card_disables_text_actions()
    {
        _conflicts.Conflicts = [];

        var view = new ConflictResolverView(_sp, _context);
        await view.LoadAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.False(view.IsEditableForTesting);
        Assert.NotEmpty(view.TextActionButtonsForTesting);
        Assert.All(view.TextActionButtonsForTesting, b => Assert.False(b.IsEnabled));
    }

    [AvaloniaFact]
    public async Task Switching_to_file_card_clears_parse_and_count()
    {
        var textFile = new ConflictFile { FilePath = "f.txt", Status = ConflictStatus.BothModified, IsBinary = false };
        var binFile = new ConflictFile { FilePath = "bin.png", Status = ConflictStatus.BothModified, IsBinary = true };
        _conflicts.Conflicts = [textFile, binFile];
        _conflicts.WorkingCopies["f.txt"] = new ConflictWorkingCopy
        {
            Path = "f.txt",
            Text = TwoConflictText,
            DiskHash = "hash1"
        };
        _conflicts.WorkingCopies["bin.png"] = new ConflictWorkingCopy
        {
            Path = "bin.png",
            IsBinary = true,
            DiskHash = "hash2"
        };

        var view = new ConflictResolverView(_sp, _context);
        await view.LoadAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.True(view.IsEditableForTesting);
        Assert.NotNull(view.CurrentParseForTesting);
        Assert.True(view.ConflictsCountTextForTesting.IsVisible);

        _context.State.SetSelectedConflictPath("bin.png");
        Dispatcher.UIThread.RunJobs();

        Assert.False(view.IsEditableForTesting);
        Assert.Null(view.CurrentParseForTesting);
        Assert.False(view.ConflictsCountTextForTesting.IsVisible);
    }

    [AvaloniaFact]
    public async Task Stage_error_adopts_new_hash_notifies_and_stays_on_file()
    {
        var f1 = new ConflictFile { FilePath = "f1.txt", Status = ConflictStatus.BothModified, IsBinary = false };
        var f2 = new ConflictFile { FilePath = "f2.txt", Status = ConflictStatus.BothModified, IsBinary = false };
        _conflicts.Conflicts = [f1, f2];
        _conflicts.WorkingCopies["f1.txt"] = new ConflictWorkingCopy
        {
            Path = "f1.txt",
            Text = TwoConflictText,
            DiskHash = "hash1",
            Format = new TextFileFormat(System.Text.Encoding.UTF8, false)
        };
        _conflicts.WorkingCopies["f2.txt"] = new ConflictWorkingCopy
        {
            Path = "f2.txt",
            Text = TwoConflictText,
            DiskHash = "hash2",
            Format = new TextFileFormat(System.Text.Encoding.UTF8, false)
        };
        _conflicts.SaveResult = new ResolutionSaveResult
        {
            Saved = true,
            DiskHash = "h2",
            StageError = new HistoriaGitException(GitErrorKind.Unknown, "git add failed")
        };

        var view = new ConflictResolverView(_sp, _context);
        await view.LoadAsync();
        Dispatcher.UIThread.RunJobs();

        var saved = await view.SaveCurrentFileAsync(stage: true);
        Dispatcher.UIThread.RunJobs();

        Assert.False(saved);
        Assert.Equal("h2", view.DiskHashForTesting);
        var notifications = _sp.GetRequiredService<INotificationService>().Current;
        Assert.Contains(notifications, n => n.Severity == NotificationSeverity.Error && n.Message.Contains("git add failed"));
        Assert.Equal("f1.txt", view.CurrentFileForTesting?.FilePath);
    }

    [AvaloniaFact]
    public async Task Stage_error_message_says_the_file_was_saved_and_carries_details()
    {
        var f1 = new ConflictFile { FilePath = "f1.txt", Status = ConflictStatus.BothModified, IsBinary = false };
        _conflicts.Conflicts = [f1];
        _conflicts.WorkingCopies["f1.txt"] = new ConflictWorkingCopy
        {
            Path = "f1.txt",
            Text = TwoConflictText,
            DiskHash = "hash1",
            Format = new TextFileFormat(System.Text.Encoding.UTF8, false)
        };
        _conflicts.SaveResult = new ResolutionSaveResult
        {
            Saved = true,
            DiskHash = "h2",
            StageError = new HistoriaGitException(GitErrorKind.Unknown, "git add failed", details: "fatal: lockfile present")
        };

        var view = new ConflictResolverView(_sp, _context);
        await view.LoadAsync();
        Dispatcher.UIThread.RunJobs();

        var saved = await view.SaveCurrentFileAsync(stage: true);
        Dispatcher.UIThread.RunJobs();

        Assert.False(saved);
        var notifications = _sp.GetRequiredService<INotificationService>().Current;
        var errNotification = notifications.FirstOrDefault(n => n.Severity == NotificationSeverity.Error && n.Message.StartsWith("Saved f1.txt, but could not mark it resolved"));
        Assert.NotNull(errNotification);
        Assert.False(string.IsNullOrEmpty(errNotification.Details));
        Assert.Contains("fatal: lockfile present", errNotification.Details);
    }

    [AvaloniaFact]
    public async Task Edits_during_save_stay_unsaved()
    {
        var f1 = new ConflictFile { FilePath = "f1.txt", Status = ConflictStatus.BothModified, IsBinary = false };
        var f2 = new ConflictFile { FilePath = "f2.txt", Status = ConflictStatus.BothModified, IsBinary = false };
        _conflicts.Conflicts = [f1, f2];
        _conflicts.WorkingCopies["f1.txt"] = new ConflictWorkingCopy
        {
            Path = "f1.txt",
            Text = TwoConflictText,
            DiskHash = "hash1",
            Format = new TextFileFormat(System.Text.Encoding.UTF8, false)
        };
        _conflicts.WorkingCopies["f2.txt"] = new ConflictWorkingCopy
        {
            Path = "f2.txt",
            Text = TwoConflictText,
            DiskHash = "hash2",
            Format = new TextFileFormat(System.Text.Encoding.UTF8, false)
        };
        _conflicts.SaveResult = new ResolutionSaveResult { Saved = true, DiskHash = "hash1-saved" };

        var view = new ConflictResolverView(_sp, _context);
        await view.LoadAsync();
        Dispatcher.UIThread.RunJobs();

        var gate = new TaskCompletionSource<bool>();
        _conflicts.SaveGate = gate;

        var saveTask = view.SaveCurrentFileAsync(stage: true);

        // While save is in flight, mutate document text
        view.ResultEditorForTesting.Document.Insert(0, "// extra edit\n");
        Dispatcher.UIThread.RunJobs();

        // Release save gate
        gate.SetResult(true);
        var saved = await saveTask;
        Dispatcher.UIThread.RunJobs();

        Assert.True(saved);
        Assert.True(view.HasUnsavedWork);
        Assert.Equal("f1.txt", view.CurrentFileForTesting?.FilePath);
    }

    [AvaloniaFact]
    public async Task Dispose_during_status_refresh_publishes_no_error()
    {
        var f1 = new ConflictFile { FilePath = "f1.txt", Status = ConflictStatus.BothModified, IsBinary = false };
        _conflicts.Conflicts = [f1];
        _conflicts.WorkingCopies["f1.txt"] = new ConflictWorkingCopy { Path = "f1.txt", Text = TwoConflictText, DiskHash = "hash1" };

        var view = new ConflictResolverView(_sp, _context);
        await view.LoadAsync();
        Dispatcher.UIThread.RunJobs();

        var gate = new TaskCompletionSource<ConflictSideLabels>();
        _conflicts.OnGetSideLabelsAsync = () => gate.Task;

        _ = _context.State.RefreshStatusAsync();
        Dispatcher.UIThread.RunJobs();

        view.Dispose();
        gate.SetResult(_conflicts.SideLabels);
        Dispatcher.UIThread.RunJobs();

        var notifications = _sp.GetRequiredService<INotificationService>().Current;
        Assert.DoesNotContain(notifications, n => n.Severity == NotificationSeverity.Error);
    }

    [AvaloniaFact]
    public async Task Skip_label_failure_publishes_error_and_does_not_skip()
    {
        var f1 = new ConflictFile { FilePath = "f1.txt", Status = ConflictStatus.BothModified, IsBinary = false };
        _conflicts.Conflicts = [f1];
        _conflicts.WorkingCopies["f1.txt"] = new ConflictWorkingCopy { Path = "f1.txt", Text = TwoConflictText, DiskHash = "hash1" };

        var view = new ConflictResolverView(_sp, _context);
        await view.LoadAsync();
        Dispatcher.UIThread.RunJobs();

        _conflicts.OnGetSideLabelsAsync = () => throw new InvalidOperationException("Failed to read commit labels");

        await view.SkipAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(0, _conflicts.SkipCalls);
        var notifications = _sp.GetRequiredService<INotificationService>().Current;
        Assert.Contains(notifications, n => n.Severity == NotificationSeverity.Error && n.Message.Contains("Failed to read commit labels"));
    }

    [AvaloniaFact]
    public async Task Continue_unsaved_prompt_uses_warning_chooser()
    {
        var f1 = new ConflictFile { FilePath = "f1.txt", Status = ConflictStatus.BothModified, IsBinary = false };
        _conflicts.Conflicts = [f1];
        _conflicts.WorkingCopies["f1.txt"] = new ConflictWorkingCopy
        {
            Path = "f1.txt",
            Text = TwoConflictText,
            DiskHash = "hash1",
            Format = new TextFileFormat(System.Text.Encoding.UTF8, false)
        };

        var view = new ConflictResolverView(_sp, _context);
        await view.LoadAsync();
        Dispatcher.UIThread.RunJobs();

        // Mutate document to create unsaved work
        view.ResultEditorForTesting.Document.Insert(0, "unsaved change\n");
        Dispatcher.UIThread.RunJobs();
        Assert.True(view.HasUnsavedWork);

        _dialogs.ChooseResult = 2; // Cancel

        await view.ContinueAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.Single(_dialogs.ChooseCalls);
        Assert.Equal(DialogIcon.Warning, _dialogs.LastChooseIcon);
        Assert.Equal(1, _dialogs.LastChooseDestructiveIndex);
    }

    #region Fakes

    internal sealed class FakeConflictService : IConflictService
    {
        public IReadOnlyList<ConflictFile> Conflicts { get; set; } = [];
        public Dictionary<string, ConflictWorkingCopy> WorkingCopies { get; } = new(StringComparer.Ordinal);
        public ConflictSideLabels SideLabels { get; set; } = new()
        {
            OursTitle = "Ours",
            OursDetail = "Ours detail",
            TheirsTitle = "Theirs",
            TheirsDetail = "Theirs detail",
            Operation = OperationState.Merging,
            StopId = "stop-init"
        };
        public ResolutionSaveResult SaveResult { get; set; } = new() { Saved = true, DiskHash = "saved-hash" };
        public TaskCompletionSource<bool>? SaveGate { get; set; }
        public Func<Task<ConflictSideLabels>>? OnGetSideLabelsAsync { get; set; }
        public Func<Task>? OnBeforeSaveAsync { get; set; }
        public Func<Task>? OnContinueAsync { get; set; }
        public Func<string, CancellationToken, Task>? OnLoadWorkingCopyAsync { get; set; }
        public Func<Task<IReadOnlyList<ConflictFile>>>? OnGetConflictsAsync { get; set; }
        public int GetConflictsCalls { get; set; }
        public bool AutoRemoveResolvedConflicts { get; set; } = true;
        public List<(string FilePath, string Text, bool Stage)> SaveResolutionCalls { get; } = [];
        public int ContinueCalls { get; set; }
        public int AbortCalls { get; set; }
        public List<(string Path, ConflictSide Side)> ResolvedWithSideCalls { get; } = [];
        public List<string> RecreatedCalls { get; } = [];
        public List<string> ExternalToolCalls { get; } = [];

        public async Task<IReadOnlyList<ConflictFile>> GetConflictsAsync(RepositoryContext context, CancellationToken ct = default)
        {
            GetConflictsCalls++;
            if (OnGetConflictsAsync != null)
                return await OnGetConflictsAsync();
            return Conflicts;
        }

        public string? DiskHash { get; set; }
        public int GetDiskHashCalls { get; set; }
        public int LoadWorkingCopyCalls { get; set; }

        public Task<string> GetDiskHashAsync(RepositoryContext context, string filePath, CancellationToken ct = default)
        {
            GetDiskHashCalls++;
            if (DiskHash != null)
                return Task.FromResult(DiskHash);
            if (WorkingCopies.TryGetValue(filePath, out var copy))
                return Task.FromResult(copy.DiskHash);
            return Task.FromResult("default-hash");
        }

        public async Task<ConflictWorkingCopy> LoadWorkingCopyAsync(RepositoryContext context, string filePath, CancellationToken ct = default)
        {
            LoadWorkingCopyCalls++;
            if (OnLoadWorkingCopyAsync != null)
                await OnLoadWorkingCopyAsync(filePath, ct);

            if (WorkingCopies.TryGetValue(filePath, out var copy))
            {
                if (copy.Format == null && !copy.IsBinary && !copy.UnsupportedEncoding)
                {
                    return copy with { Format = new TextFileFormat(System.Text.Encoding.UTF8, false) };
                }
                return copy;
            }

            return new ConflictWorkingCopy
            {
                Path = filePath,
                Text = "",
                DiskHash = "default-hash",
                Format = new TextFileFormat(System.Text.Encoding.UTF8, false)
            };
        }

        public async Task<ResolutionSaveResult> SaveResolutionAsync(RepositoryContext context, string filePath, string text, TextFileFormat format, string expectedDiskHash, bool stage, CancellationToken ct = default)
        {
            SaveResolutionCalls.Add((filePath, text, stage));
            if (SaveGate != null)
                await SaveGate.Task;
            if (OnBeforeSaveAsync != null)
                await OnBeforeSaveAsync();

            if (stage && AutoRemoveResolvedConflicts && SaveResult.Saved)
            {
                Conflicts = Conflicts.Where(c => !string.Equals(c.FilePath, filePath, StringComparison.Ordinal)).ToList();
            }

            return SaveResult;
        }

        public Task ResolveWithSideAsync(RepositoryContext context, string filePath, ConflictSide side, CancellationToken ct = default)
        {
            ResolvedWithSideCalls.Add((filePath, side));
            if (AutoRemoveResolvedConflicts)
            {
                Conflicts = Conflicts.Where(c => !string.Equals(c.FilePath, filePath, StringComparison.Ordinal)).ToList();
            }
            return Task.CompletedTask;
        }

        public Task RecreateConflictAsync(RepositoryContext context, string filePath, CancellationToken ct = default)
        {
            RecreatedCalls.Add(filePath);
            if (AutoRemoveResolvedConflicts && !Conflicts.Any(c => string.Equals(c.FilePath, filePath, StringComparison.Ordinal)))
            {
                var list = Conflicts.ToList();
                list.Add(new ConflictFile { FilePath = filePath, Status = ConflictStatus.BothModified, IsBinary = false });
                Conflicts = list;
            }
            return Task.CompletedTask;
        }

        public async Task<ConflictSideLabels> GetSideLabelsAsync(RepositoryContext context, CancellationToken ct = default)
        {
            if (OnGetSideLabelsAsync != null)
                return await OnGetSideLabelsAsync();
            return SideLabels;
        }

        public Task<bool> LaunchExternalMergeToolAsync(RepositoryContext context, string filePath, CancellationToken ct = default)
        {
            ExternalToolCalls.Add(filePath);
            return Task.FromResult(true);
        }

        public Func<CancellationToken, Task>? OnContinueWithTokenAsync { get; set; }
        public int SkipCalls { get; set; }

        public async Task ContinueOperationAsync(RepositoryContext context, CancellationToken ct = default)
        {
            ContinueCalls++;
            if (OnContinueWithTokenAsync != null)
                await OnContinueWithTokenAsync(ct);
            else if (OnContinueAsync != null)
                await OnContinueAsync();
        }

        public Task SkipCurrentAsync(RepositoryContext context, CancellationToken ct = default)
        {
            SkipCalls++;
            return Task.CompletedTask;
        }

        public Task AbortOperationAsync(RepositoryContext context, CancellationToken ct = default)
        {
            AbortCalls++;
            return Task.CompletedTask;
        }
    }

    internal sealed class FakeDialogService : IDialogService
    {
        public bool ConfirmResult { get; set; } = true;
        public bool ConfirmDestructiveResult { get; set; } = true;
        public int ChooseResult { get; set; } = 0;
        public DialogIcon LastChooseIcon { get; set; } = DialogIcon.None;
        public int LastChooseDestructiveIndex { get; set; } = -1;
        public List<(string Title, string Effect, string ConfirmLabel)> ConfirmDestructiveCalls { get; } = [];
        public List<(string Title, string Message, IReadOnlyList<string> Options)> ChooseCalls { get; } = [];

        public Task<string?> PickFolderAsync(string title, string? initialDirectory = null) => Task.FromResult<string?>(null);
        public Task<string?> PickFileAsync(string title, string filter, string? initialDirectory = null) => Task.FromResult<string?>(null);

        public Task<bool> ConfirmAsync(string title, string message, bool destructive = false, string confirmLabel = "OK", bool defaultToCancel = false, DialogIcon icon = DialogIcon.None)
            => Task.FromResult(ConfirmResult);

        public Task<bool> ConfirmDestructiveAsync(string title, string effect, string confirmLabel)
        {
            ConfirmDestructiveCalls.Add((title, effect, confirmLabel));
            return Task.FromResult(ConfirmDestructiveResult);
        }

        public Task<int> ChooseAsync(string title, string message, IReadOnlyList<string> options, int defaultIndex = 0, int destructiveIndex = -1, DialogIcon icon = DialogIcon.None)
        {
            LastChooseIcon = icon;
            LastChooseDestructiveIndex = destructiveIndex;
            ChooseCalls.Add((title, message, options));
            return Task.FromResult(ChooseResult);
        }

        public Task ShowMessageAsync(string title, string message) => Task.CompletedTask;

        public Task<string?> PromptAsync(string title, string message, string? defaultValue = null, string confirmLabel = "OK", bool primaryConfirm = false)
            => Task.FromResult<string?>(null);
    }

    internal sealed class FakeSettingsService(AppSettings? initial = null) : ISettingsService
    {
        public AppSettings Current { get; } = initial ?? new();
        public event EventHandler<SettingsChangedEventArgs>? Changed { add { } remove { } }
        public Task UpdateAsync(Func<AppSettings, AppSettings> mutate, CancellationToken ct = default) => Task.CompletedTask;
        public Task FlushAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    internal sealed class FakeThemeProbe : ISystemThemeProbe
    {
        public bool IsLightTheme => false;
        public bool IsHighContrast => false;
#pragma warning disable CS0067
        public event EventHandler? Changed;
#pragma warning restore CS0067
    }

    internal sealed class FakeVault : ICredentialVaultService
    {
        public Task StoreTokenAsync(string key, string token, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> RetrieveTokenAsync(string key, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task DeleteTokenAsync(string key, CancellationToken ct = default) => Task.CompletedTask;
    }

    internal sealed class FakeNetworkRunner : IRepoNetworkActionRunner
    {
        public bool IsBusy => false;
        public RepoNetworkProgress? CurrentProgress => null;
#pragma warning disable CS0067
        public event EventHandler<RepoNetworkProgress>? ProgressChanged;
#pragma warning restore CS0067
        public Task RunAsync(RepositoryContext repo, string actionName, Func<IProgress<RepoNetworkProgress>?, CancellationToken, Task> action, CancellationToken ct = default)
            => Task.CompletedTask;
        public void CancelCurrent() { }
    }

    internal sealed class TestRepoFixture : IDisposable
    {
        public string Path { get; }
        public string GitDir => System.IO.Path.Combine(Path, ".git");

        public TestRepoFixture()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "historia-conflict-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
            RunGit(Path, "init", "-b", "main");
            RunGit(Path, "config", "user.name", "Test");
            RunGit(Path, "config", "user.email", "test@example.com");

            var file = System.IO.Path.Combine(Path, "f.txt");
            File.WriteAllText(file, "init\n");
            RunGit(Path, "add", ".");
            RunGit(Path, "commit", "-m", "initial commit", "--no-gpg-sign");
        }

        public void SetOperation(OperationState op)
        {
            ClearOperation();
            switch (op)
            {
                case OperationState.Merging:
                    File.WriteAllText(System.IO.Path.Combine(GitDir, "MERGE_HEAD"), "1111111111111111111111111111111111111111\n");
                    break;
                case OperationState.Rebasing:
                    Directory.CreateDirectory(System.IO.Path.Combine(GitDir, "rebase-merge"));
                    break;
                case OperationState.CherryPicking:
                    File.WriteAllText(System.IO.Path.Combine(GitDir, "CHERRY_PICK_HEAD"), "1111111111111111111111111111111111111111\n");
                    break;
                case OperationState.Reverting:
                    File.WriteAllText(System.IO.Path.Combine(GitDir, "REVERT_HEAD"), "1111111111111111111111111111111111111111\n");
                    break;
                case OperationState.Bisecting:
                    File.WriteAllText(System.IO.Path.Combine(GitDir, "BISECT_LOG"), "bisect log\n");
                    break;
            }
        }

        public void ClearOperation()
        {
            try { File.Delete(System.IO.Path.Combine(GitDir, "MERGE_HEAD")); } catch { }
            try { File.Delete(System.IO.Path.Combine(GitDir, "CHERRY_PICK_HEAD")); } catch { }
            try { File.Delete(System.IO.Path.Combine(GitDir, "REVERT_HEAD")); } catch { }
            try { File.Delete(System.IO.Path.Combine(GitDir, "BISECT_LOG")); } catch { }
            try { Directory.Delete(System.IO.Path.Combine(GitDir, "rebase-merge"), true); } catch { }
            try { Directory.Delete(System.IO.Path.Combine(GitDir, "rebase-apply"), true); } catch { }
        }

        public void CreateConflict()
        {
            RunGit(Path, "checkout", "-b", "branch1");
            File.WriteAllText(System.IO.Path.Combine(Path, "f.txt"), "branch1 line\n");
            RunGit(Path, "commit", "-am", "branch1 commit", "--no-gpg-sign");

            RunGit(Path, "checkout", "main");
            File.WriteAllText(System.IO.Path.Combine(Path, "f.txt"), "main line\n");
            RunGit(Path, "commit", "-am", "main commit", "--no-gpg-sign");

            RunGit(Path, "merge", "branch1");
        }

        public void MarkResolved()
        {
            RunGit(Path, "add", "f.txt");
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
            p.WaitForExit();
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                    Directory.Delete(Path, recursive: true);
            }
            catch { }
        }
    }

    #endregion
}
