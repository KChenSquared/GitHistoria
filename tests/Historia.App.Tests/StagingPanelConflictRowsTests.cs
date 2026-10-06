namespace HistoriaApp.Tests;

[Trait("tier", "ui")]
public class StagingPanelConflictRowsTests : IDisposable
{
    private readonly TestRepoFixture _fixture;
    private readonly ServiceProvider _sp;
    private readonly RepositoryContext _context;

    public StagingPanelConflictRowsTests()
    {
        _fixture = new TestRepoFixture();
        _sp = CreateServices();
        _sp.GetRequiredService<Historia.Theming.AvaloniaThemeApplier>();
        var ws = _sp.GetRequiredService<IWorkspaceService>();
        _context = ws.OpenAsync(_fixture.Path).GetAwaiter().GetResult();
        _context.State.RefreshRefsAsync().GetAwaiter().GetResult();
        ws.Focus(_context);
    }

    public void Dispose()
    {
        try { _context.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
        try { _sp.Dispose(); } catch { }
        try { _fixture.Dispose(); } catch { }
    }

    private static void Click(Control control)
    {
        control.RaiseEvent(new PointerPressedEventArgs(
            control,
            new Pointer(1, PointerType.Mouse, true),
            control,
            new Point(5, 5),
            0,
            new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonPressed),
            KeyModifiers.None));
    }

    [AvaloniaFact]
    public async Task Conflicted_files_render_inside_Changes_with_red_header_and_Conflict_label()
    {
        _fixture.CreateConflict();
        await _context.State.RefreshStatusAsync();
        Dispatcher.UIThread.RunJobs();

        var panel = new StagingPanelView(_sp, _context);
        Dispatcher.UIThread.RunJobs();

        var rows = panel.ChangesRowsForTesting.Children;
        Assert.NotEmpty(rows);

        var header = Assert.IsType<TextBlock>(rows[0]);
        Assert.Equal("Conflicted — resolve these before committing", header.Text);

        var border = Assert.IsType<Border>(rows[1]);
        var grid = Assert.IsType<Grid>(border.Child);
        Assert.Equal(2, grid.ColumnDefinitions.Count);

        var conflictLabel = grid.Children.OfType<TextBlock>().First(tb => tb.Text == "Conflict");
        Assert.NotNull(conflictLabel);

        var pathLabel = grid.Children.OfType<TextBlock>().First(tb => tb.Text == "f.txt");
        Assert.NotNull(pathLabel);
    }

    [AvaloniaFact]
    public async Task Conflicted_rows_have_no_checkbox_and_no_discard()
    {
        _fixture.CreateConflict();
        await _context.State.RefreshStatusAsync();
        Dispatcher.UIThread.RunJobs();

        var panel = new StagingPanelView(_sp, _context);
        Dispatcher.UIThread.RunJobs();

        Assert.True(panel.ConflictedRowCacheForTesting.TryGetValue("f.txt", out var border));
        Assert.NotNull(border);

        Assert.Empty(border!.GetLogicalDescendants().OfType<CheckBox>());
        Assert.Empty(border.GetLogicalDescendants().OfType<Button>());
    }

    [AvaloniaFact]
    public async Task Separate_conflict_strip_is_gone()
    {
        _fixture.CreateConflict();
        await _context.State.RefreshStatusAsync();
        Dispatcher.UIThread.RunJobs();

        var panel = new StagingPanelView(_sp, _context);
        Dispatcher.UIThread.RunJobs();

        var dock = Assert.IsType<DockPanel>(panel.Content);
        Assert.Equal(2, dock.Children.Count);
        Assert.DoesNotContain(dock.Children, c => c.GetType().Name == "Border" && ((Border)c).Child is StackPanel sp && sp.Children.OfType<TextBlock>().Any(t => t.Text?.StartsWith("Conflicted") == true));
    }

    [AvaloniaFact]
    public async Task Clicking_conflicted_row_closes_diff_and_sets_SelectedConflictPath()
    {
        _fixture.CreateConflict();
        await _context.State.RefreshStatusAsync();
        Dispatcher.UIThread.RunJobs();

        var panel = new StagingPanelView(_sp, _context);
        Dispatcher.UIThread.RunJobs();

        _context.State.SetOpenDiff(new DiffRequest
        {
            Path = "f.txt",
            Source = DiffSource.UnstagedChanges
        });
        Assert.NotNull(_context.State.OpenDiff);

        Assert.True(panel.ConflictedRowCacheForTesting.TryGetValue("f.txt", out var border));
        Click(border!);
        Dispatcher.UIThread.RunJobs();

        Assert.Null(_context.State.OpenDiff);
        Assert.Equal("f.txt", _context.State.SelectedConflictPath);
    }

    [AvaloniaFact]
    public async Task Clicking_a_conflicted_row_reopens_a_closed_resolver()
    {
        _fixture.CreateConflict();
        await _context.State.RefreshStatusAsync();
        // A refresh already in flight can finish with the pre-conflict status; closing before the
        // session starts would be undone by the start.
        for (int i = 0; i < 50 && !_context.State.IsConflictSessionActive; i++)
        {
            await Task.Delay(50);
            await _context.State.RefreshStatusAsync();
        }
        Assert.True(_context.State.IsConflictSessionActive);
        Dispatcher.UIThread.RunJobs();

        var panel = new StagingPanelView(_sp, _context);
        Dispatcher.UIThread.RunJobs();
        Assert.True(panel.ConflictedRowCacheForTesting.TryGetValue("f.txt", out var border));

        Click(border!);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("f.txt", _context.State.SelectedConflictPath);

        // The path is already selected, so only the closed flag can bring the resolver back.
        _context.State.SetConflictResolverClosed(true);
        Assert.True(_context.State.IsConflictResolverClosed);

        Click(border!);
        Dispatcher.UIThread.RunJobs();

        Assert.False(_context.State.IsConflictResolverClosed);
        Assert.Equal("f.txt", _context.State.SelectedConflictPath);
    }

    [AvaloniaFact]
    public async Task Stage_all_and_select_all_exclude_conflicted_paths()
    {
        _fixture.CreateConflict();
        File.WriteAllText(System.IO.Path.Combine(_fixture.Path, "u.txt"), "untracked content\n");
        await _context.State.RefreshStatusAsync();
        Dispatcher.UIThread.RunJobs();

        var panel = new StagingPanelView(_sp, _context);
        Dispatcher.UIThread.RunJobs();

        var stageAllBtn = panel.GetLogicalDescendants().OfType<Button>().First(b => b.Content?.ToString() == "Stage all");
        stageAllBtn.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            await _context.State.RefreshStatusAsync();
            Dispatcher.UIThread.RunJobs();
            if (_context.State.Status.Staged.Any(s => s.Path == "u.txt"))
            {
                break;
            }
            await Task.Delay(50);
        }

        Assert.Contains(_context.State.Status.Conflicted, c => c.Path == "f.txt");
        Assert.Contains(_context.State.Status.Staged, s => s.Path == "u.txt");
    }

    [AvaloniaFact]
    public async Task Highlight_follows_SelectedConflictPath()
    {
        _fixture.CreateConflicts("f.txt", "g.txt");
        await _context.State.RefreshStatusAsync();
        Dispatcher.UIThread.RunJobs();

        var panel = new StagingPanelView(_sp, _context);
        var window = new Window { Content = panel };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();

            Assert.True(panel.ConflictedRowCacheForTesting.TryGetValue("f.txt", out var rowF));
            Assert.True(panel.ConflictedRowCacheForTesting.TryGetValue("g.txt", out var rowG));

            _context.State.SetSelectedConflictPath("f.txt");
            Dispatcher.UIThread.RunJobs();

            Assert.NotEqual(Brushes.Transparent, rowF!.Background);
            Assert.Equal(Brushes.Transparent, rowG!.Background);

            _context.State.SetSelectedConflictPath("g.txt");
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(Brushes.Transparent, rowF.Background);
            Assert.NotEqual(Brushes.Transparent, rowG.Background);

            _context.State.SetSelectedConflictPath(null);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(Brushes.Transparent, rowF.Background);
            Assert.Equal(Brushes.Transparent, rowG.Background);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Changes_count_includes_conflicted()
    {
        _fixture.CreateConflict();
        File.WriteAllText(System.IO.Path.Combine(_fixture.Path, "u.txt"), "untracked content\n");
        await _context.State.RefreshStatusAsync();
        Dispatcher.UIThread.RunJobs();

        var panel = new StagingPanelView(_sp, _context);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("Changes (2)", panel.ChangesHeaderForTesting.Text);
        Assert.DoesNotContain(panel.ChangesRowsForTesting.Children.OfType<TextBlock>(), tb => tb.Text == "No changes.");
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
        sc.AddSingleton<Historia.Theming.AvaloniaThemeApplier>();
        return sc.BuildServiceProvider();
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
        public string Path { get; }

        public TestRepoFixture()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "historia-staging-conflict-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
            RunGit(Path, "init", "-b", "main");
            RunGit(Path, "config", "user.name", "Test");
            RunGit(Path, "config", "user.email", "test@example.com");

            var file = System.IO.Path.Combine(Path, "f.txt");
            File.WriteAllText(file, "init\n");
            RunGit(Path, "add", ".");
            RunGit(Path, "commit", "-m", "initial commit", "--no-gpg-sign");
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

        public void CreateConflicts(params string[] files)
        {
            RunGit(Path, "checkout", "-b", "branch_conflict");
            foreach (var f in files)
            {
                File.WriteAllText(System.IO.Path.Combine(Path, f), "branch line for " + f + "\n");
                RunGit(Path, "add", f);
            }
            RunGit(Path, "commit", "-m", "branch commit", "--no-gpg-sign");

            RunGit(Path, "checkout", "main");
            foreach (var f in files)
            {
                File.WriteAllText(System.IO.Path.Combine(Path, f), "main line for " + f + "\n");
                RunGit(Path, "add", f);
            }
            RunGit(Path, "commit", "-m", "main commit", "--no-gpg-sign");

            RunGit(Path, "merge", "branch_conflict");
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
}
