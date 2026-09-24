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

        var repoView = new RepositoryView(_sp);
        var window = new Window { Content = repoView };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var resolver = _conflictHost.TryGet(_context);
        Assert.NotNull(resolver);
        Assert.True(resolver.KeepOpenForContinue);

        // Mark resolved
        _fixture.MarkResolved();
        await _context.State.RefreshStatusAsync();
        Dispatcher.UIThread.RunJobs();

        // RepositoryView should still retain the resolver because KeepOpenForContinue is true and op is Merging
        Assert.NotNull(_conflictHost.TryGet(_context));

        window.Close();
    }

    [AvaloniaFact]
    public async Task Continue_that_stops_on_new_conflicts_keeps_same_resolver_instance()
    {
        _conflicts.Conflicts = [new ConflictFile { FilePath = "f.txt", Status = ConflictStatus.BothModified, IsBinary = false }];
        _conflicts.WorkingCopies["f.txt"] = new ConflictWorkingCopy { Path = "f.txt", Text = TwoConflictText, DiskHash = "hash1" };

        _fixture.SetOperation(OperationState.Rebasing);
        _fixture.CreateConflict();
        await _context.State.RefreshStatusAsync();

        var repoView = new RepositoryView(_sp);
        var window = new Window { Content = repoView };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var firstInstance = _conflictHost.TryGet(_context);
        Assert.NotNull(firstInstance);

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

        window.Close();
    }

    [AvaloniaFact(Skip = "Defect U1 — un-skipped by M3.2")]
    public async Task Bisecting_with_0_conflicts_shows_op_banner_without_continue_button()
    {
        var repoView = new RepositoryView(_sp);
        var window = new Window { Content = repoView };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        _fixture.SetOperation(OperationState.Bisecting);
        await _context.State.RefreshStatusAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.True(repoView.OpBoxForTesting.IsVisible);
        Assert.False(repoView.OpContinueButtonForTesting.IsVisible);

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
        view.KeepMine();
        await Task.Delay(100);
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

    [AvaloniaFact(Skip = "Defect F18 — un-skipped by M3.2")]
    public async Task Take_theirs_confirmed_on_dirty_buffer_reloads_and_unregisters_unsaved()
    {
        _conflicts.Conflicts = [new ConflictFile { FilePath = "f.txt", Status = ConflictStatus.BothModified, IsBinary = false }];
        _conflicts.WorkingCopies["f.txt"] = new ConflictWorkingCopy { Path = "f.txt", Text = TwoConflictText, DiskHash = "hash1" };

        var view = new ConflictResolverView(_sp, _context);
        await view.LoadAsync();
        Dispatcher.UIThread.RunJobs();

        view.ResultEditorForTesting.Document.Insert(0, "dirty\n");
        Assert.True(view.HasUnsavedWork);

        _dialogs.ConfirmDestructiveResult = true;
        _conflicts.WorkingCopies["f.txt"] = new ConflictWorkingCopy { Path = "f.txt", Text = "reloaded text", DiskHash = "hash-reloaded" };

        await view.TakeTheirsCurrentAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.False(view.HasUnsavedWork);
        Assert.Equal("reloaded text", view.ResultEditorForTesting.Text);
    }

    [AvaloniaFact]
    public async Task Dirty_buffer_survives_repository_view_reconstruction()
    {
        _conflicts.Conflicts = [new ConflictFile { FilePath = "f.txt", Status = ConflictStatus.BothModified, IsBinary = false }];
        _conflicts.WorkingCopies["f.txt"] = new ConflictWorkingCopy { Path = "f.txt", Text = TwoConflictText, DiskHash = "hash1" };

        _fixture.CreateConflict();
        await _context.State.RefreshStatusAsync();

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
        window.Content = null;
        Dispatcher.UIThread.RunJobs();

        // Resolver in host is not disposed
        var cachedResolver = _conflictHost.TryGet(_context);
        Assert.NotNull(cachedResolver);
        Assert.Same(resolver, cachedResolver);
        Assert.True(cachedResolver.HasUnsavedWork);

        // Mount second RepositoryView
        var repoView2 = new RepositoryView(_sp);
        window.Content = repoView2;
        Dispatcher.UIThread.RunJobs();

        Assert.Contains("preserved edit", cachedResolver.ResultEditorForTesting.Text);
        Assert.True(cachedResolver.HasUnsavedWork);

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
        view.KeepOpenForContinue = true;
        Dispatcher.UIThread.RunJobs();

        // Terminal continue moves sequencer to next stop with 0 conflicts
        _conflicts.SideLabels = _conflicts.SideLabels with { StopId = "stop-2" };
        _conflicts.Conflicts = [];
        await _context.State.RefreshStatusAsync();

        await Task.Delay(100);
        Dispatcher.UIThread.RunJobs();

        Assert.False(view.KeepOpenForContinue);
    }

    [AvaloniaFact(Skip = "Defect F18 — un-skipped by M3.2")]
    public async Task Resolved_file_stays_in_list_with_checkmark()
    {
        _conflicts.Conflicts = [new ConflictFile { FilePath = "f.txt", Status = ConflictStatus.BothModified, IsBinary = false }];
        _conflicts.WorkingCopies["f.txt"] = new ConflictWorkingCopy { Path = "f.txt", Text = TwoConflictText, DiskHash = "hash1" };

        var view = new ConflictResolverView(_sp, _context);
        await view.LoadAsync();
        Dispatcher.UIThread.RunJobs();

        await view.SaveCurrentFileAsync(stage: true);
        Dispatcher.UIThread.RunJobs();

        Assert.Contains("✓", view.FileListBoxForTesting.Items.Cast<object>().First().ToString());
    }

    [AvaloniaFact(Skip = "Defect F11 — un-skipped by M2.1")]
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

    #region Fakes

    private sealed class FakeConflictService : IConflictService
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
        public Func<Task>? OnBeforeSaveAsync { get; set; }
        public Func<Task>? OnContinueAsync { get; set; }
        public List<(string Path, ConflictSide Side)> ResolvedWithSideCalls { get; } = [];
        public List<string> RecreatedCalls { get; } = [];
        public List<string> ExternalToolCalls { get; } = [];

        public Task<IReadOnlyList<ConflictFile>> GetConflictsAsync(RepositoryContext context, CancellationToken ct = default)
            => Task.FromResult(Conflicts);

        public Task<ConflictWorkingCopy> LoadWorkingCopyAsync(RepositoryContext context, string filePath, CancellationToken ct = default)
        {
            if (WorkingCopies.TryGetValue(filePath, out var copy))
                return Task.FromResult(copy);

            return Task.FromResult(new ConflictWorkingCopy
            {
                Path = filePath,
                Text = "",
                DiskHash = "default-hash"
            });
        }

        public async Task<ResolutionSaveResult> SaveResolutionAsync(RepositoryContext context, string filePath, string text, TextFileFormat format, string expectedDiskHash, bool stage, CancellationToken ct = default)
        {
            if (OnBeforeSaveAsync != null)
                await OnBeforeSaveAsync();

            return SaveResult;
        }

        public Task ResolveWithSideAsync(RepositoryContext context, string filePath, ConflictSide side, CancellationToken ct = default)
        {
            ResolvedWithSideCalls.Add((filePath, side));
            return Task.CompletedTask;
        }

        public Task RecreateConflictAsync(RepositoryContext context, string filePath, CancellationToken ct = default)
        {
            RecreatedCalls.Add(filePath);
            return Task.CompletedTask;
        }

        public Task<ConflictSideLabels> GetSideLabelsAsync(RepositoryContext context, CancellationToken ct = default)
            => Task.FromResult(SideLabels);

        public Task<bool> LaunchExternalMergeToolAsync(RepositoryContext context, string filePath, CancellationToken ct = default)
        {
            ExternalToolCalls.Add(filePath);
            return Task.FromResult(true);
        }

        public async Task ContinueOperationAsync(RepositoryContext context, CancellationToken ct = default)
        {
            if (OnContinueAsync != null)
                await OnContinueAsync();
        }

        public Task SkipCurrentAsync(RepositoryContext context, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task AbortOperationAsync(RepositoryContext context, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private sealed class FakeDialogService : IDialogService
    {
        public bool ConfirmResult { get; set; } = true;
        public bool ConfirmDestructiveResult { get; set; } = true;
        public int ChooseResult { get; set; } = 0;

        public Task<string?> PickFolderAsync(string title, string? initialDirectory = null) => Task.FromResult<string?>(null);
        public Task<string?> PickFileAsync(string title, string filter, string? initialDirectory = null) => Task.FromResult<string?>(null);

        public Task<bool> ConfirmAsync(string title, string message, bool destructive = false, string confirmLabel = "OK", bool defaultToCancel = false, DialogIcon icon = DialogIcon.None)
            => Task.FromResult(ConfirmResult);

        public Task<bool> ConfirmDestructiveAsync(string title, string effect, string confirmLabel)
            => Task.FromResult(ConfirmDestructiveResult);

        public Task<int> ChooseAsync(string title, string message, IReadOnlyList<string> options, int defaultIndex = 0, int destructiveIndex = -1, DialogIcon icon = DialogIcon.None)
            => Task.FromResult(ChooseResult);

        public Task ShowMessageAsync(string title, string message) => Task.CompletedTask;

        public Task<string?> PromptAsync(string title, string message, string? defaultValue = null, string confirmLabel = "OK", bool primaryConfirm = false)
            => Task.FromResult<string?>(null);
    }

    private sealed class FakeSettingsService(AppSettings? initial = null) : ISettingsService
    {
        public AppSettings Current { get; } = initial ?? new();
        public event EventHandler<SettingsChangedEventArgs>? Changed { add { } remove { } }
        public Task UpdateAsync(Func<AppSettings, AppSettings> mutate, CancellationToken ct = default) => Task.CompletedTask;
        public Task FlushAsync(CancellationToken ct = default) => Task.CompletedTask;
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

    private sealed class FakeNetworkRunner : IRepoNetworkActionRunner
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

    private sealed class TestRepoFixture : IDisposable
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
