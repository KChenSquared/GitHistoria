namespace HistoriaApp.Tests;

[Trait("tier", "ui")]
public class RepositoryTopBarTests : IDisposable
{
    private readonly TestRepoFixture _fixture;
    private readonly ServiceProvider _sp;
    private readonly RepositoryContext _context;

    public RepositoryTopBarTests()
    {
        _fixture = new TestRepoFixture();
        _sp = CreateServices();
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

    [Fact]
    public void Working_copy_change_count_counts_unique_paths_including_untracked()
    {
        static FileStatusEntry Entry(string path, FileChangeKind kind) =>
            new() { Path = path, Kind = kind, IsBinary = false, IsSubmodule = false };

        var status = RepositoryStatus.Empty with
        {
            Staged =
            [
                Entry("file1.txt", FileChangeKind.Added),
                Entry("file2.txt", FileChangeKind.Modified),
            ],
            Unstaged =
            [
                Entry("file2.txt", FileChangeKind.Modified), // duplicate path across staged/unstaged
                Entry("file3.txt", FileChangeKind.Modified),
            ],
            Untracked =
            [
                Entry("untracked.txt", FileChangeKind.Untracked),
            ],
            Conflicted =
            [
                Entry("file4.txt", FileChangeKind.Conflicted),
            ],
        };

        var count = RepositoryViewModel.WorkingCopyChangeCount(status);
        Assert.Equal(5, count); // file1, file2, file3, untracked, file4
    }

    [AvaloniaFact]
    public void Top_bar_shows_branch_in_staging_and_working_copy_in_detail()
    {
        var view = new RepositoryView(_sp);
        var window = new Window { Width = 800, Height = 600, Content = view };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();

            // Initial: no selection -> branch bar visible, working copy hidden
            Assert.True(view.BranchBarForTesting.IsVisible);
            Assert.False(view.WorkingCopyButtonForTesting.IsVisible);

            // Select a commit -> branch bar hidden, working copy visible
            var headId = _fixture.HeadCommitId;
            _context.State.SetSelectedCommit(headId);
            Dispatcher.UIThread.RunJobs();

            Assert.False(view.BranchBarForTesting.IsVisible);
            Assert.True(view.WorkingCopyButtonForTesting.IsVisible);

            // Deselect -> back to branch bar
            _context.State.SetSelectedCommit(null);
            Dispatcher.UIThread.RunJobs();

            Assert.True(view.BranchBarForTesting.IsVisible);
            Assert.False(view.WorkingCopyButtonForTesting.IsVisible);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Working_copy_bar_is_primary_only_when_dirty()
    {
        var view = new RepositoryView(_sp);
        var window = new Window { Width = 800, Height = 600, Content = view };
        window.Show();
        try
        {
            var headId = _fixture.HeadCommitId;
            _context.State.SetSelectedCommit(headId);
            Dispatcher.UIThread.RunJobs();

            // Clean tree: (Changes: 0), .flat, not .primary
            Assert.Equal("← Working Copy (Changes: 0)", view.WorkingCopyTextBlockForTesting.Text);
            Assert.Contains("flat", view.WorkingCopyButtonForTesting.Classes);
            Assert.DoesNotContain("primary", view.WorkingCopyButtonForTesting.Classes);
            Assert.Null(ToolTip.GetTip(view.WorkingCopyButtonForTesting));
            Assert.Null(ToolTip.GetTip(view.WorkingCopyTextBlockForTesting));

            // Modify a tracked file and refresh status
            _fixture.ModifyTrackedFile();
            await _context.State.RefreshStatusAsync();

            for (var i = 0; i < 50; i++)
            {
                Dispatcher.UIThread.RunJobs();
                if (view.WorkingCopyButtonForTesting.Classes.Contains("primary"))
                    break;
                await Task.Delay(20);
            }

            // Dirty tree: (Changes: 1), .primary, not .flat
            Assert.Equal("← Working Copy (Changes: 1)", view.WorkingCopyTextBlockForTesting.Text);
            Assert.Contains("primary", view.WorkingCopyButtonForTesting.Classes);
            Assert.DoesNotContain("flat", view.WorkingCopyButtonForTesting.Classes);
            Assert.Null(ToolTip.GetTip(view.WorkingCopyButtonForTesting));
            Assert.Null(ToolTip.GetTip(view.WorkingCopyTextBlockForTesting));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Working_copy_bar_fills_the_bar()
    {
        var view = new RepositoryView(_sp);
        var window = new Window { Width = 800, Height = 600, Content = view };
        window.Show();
        try
        {
            var headId = _fixture.HeadCommitId;
            _context.State.SetSelectedCommit(headId);
            Dispatcher.UIThread.RunJobs();

            var topBarBounds = view.TopBarForTesting.Bounds;
            var btnBounds = view.WorkingCopyButtonForTesting.Bounds;

            Assert.True(btnBounds.Width > 0);
            Assert.True(btnBounds.Height > 0);
            Assert.Equal(topBarBounds.Width, btnBounds.Width);
            Assert.Equal(topBarBounds.Height, btnBounds.Height);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Top_bar_is_the_same_height_in_both_modes()
    {
        var view = new RepositoryView(_sp);
        var window = new Window { Width = 800, Height = 600, Content = view };
        window.Show();
        try
        {
            const double expected = 30 + 2 * SectionFrame.HeaderGap;
            Assert.Equal(34, expected);
            Assert.Equal(expected, view.TopBarForTesting.Height);

            // Staging mode
            _context.State.SetSelectedCommit(null);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(34, view.TopBarForTesting.Bounds.Height);
            Assert.Equal(SectionFrame.HeaderGap, view.BranchBarForTesting.Margin.Top);

            // Detail mode
            var headId = _fixture.HeadCommitId;
            _context.State.SetSelectedCommit(headId);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(34, view.TopBarForTesting.Bounds.Height);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Working_copy_text_is_centred()
    {
        var view = new RepositoryView(_sp);
        var window = new Window { Width = 800, Height = 600, Content = view };
        window.Show();
        try
        {
            var headId = _fixture.HeadCommitId;
            _context.State.SetSelectedCommit(headId);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(Avalonia.Layout.HorizontalAlignment.Center, view.WorkingCopyButtonForTesting.HorizontalContentAlignment);

            var button = view.WorkingCopyButtonForTesting;
            var textBlock = view.WorkingCopyTextBlockForTesting;

            var origin = textBlock.TranslatePoint(new Point(0, 0), button);
            Assert.NotNull(origin);

            var leftGap = origin.Value.X;
            var rightGap = button.Bounds.Width - (origin.Value.X + textBlock.Bounds.Width);

            Assert.True(button.Bounds.Width > 0);
            Assert.True(textBlock.Bounds.Width > 0);
            Assert.InRange(Math.Abs(leftGap - rightGap), 0, 1.01);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Branch_and_working_copy_text_are_16_bold()
    {
        var view = new RepositoryView(_sp);
        var window = new Window { Width = 800, Height = 600, Content = view };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(16, view.BranchTextBlockForTesting.FontSize);
            Assert.Equal(FontWeight.Bold, view.BranchTextBlockForTesting.FontWeight);

            Assert.Equal(16, view.WorkingCopyButtonForTesting.FontSize);
            Assert.Equal(FontWeight.Bold, view.WorkingCopyButtonForTesting.FontWeight);

            Assert.Equal(16, view.WorkingCopyTextBlockForTesting.FontSize);
            Assert.Equal(FontWeight.Bold, view.WorkingCopyTextBlockForTesting.FontWeight);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Detail_router_hosts_the_repository_view_for_the_repo_route()
    {
        _sp.GetRequiredService<INavigationBus>().ReportRoute("repo");
        var router = new DetailRouter(_sp);
        var window = new Window { Width = 800, Height = 600, Content = router };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(DetailPage.Repo, router.ViewModel.Current.Page);
            var view = Assert.Single(router.GetVisualDescendants().OfType<RepositoryView>());
            Assert.Equal("main", view.ViewModel.BranchText);
            Assert.Single(router.GetVisualDescendants().OfType<NotificationHostView>());
        }
        finally
        {
            window.Close();
            router.Dispose();
        }
    }

    private sealed class TestRepoFixture : IDisposable
    {
        public string Path { get; }
        public GitObjectId HeadCommitId => GitObjectId.Parse(RunGitOutput(Path, "rev-parse", "HEAD"));

        public TestRepoFixture()
        {
            Path = TestScratch.NewDirectory("historia-topbar-tests-");
            RunGit(Path, "init", "-b", "main");
            RunGit(Path, "config", "user.name", "Test");
            RunGit(Path, "config", "user.email", "test@example.com");

            var file = System.IO.Path.Combine(Path, "f.txt");
            File.WriteAllText(file, "init\n");
            RunGit(Path, "add", ".");
            RunGit(Path, "commit", "-m", "initial commit", "--no-gpg-sign");
        }

        public void ModifyTrackedFile()
        {
            var file = System.IO.Path.Combine(Path, "f.txt");
            File.AppendAllText(file, "modification\n");
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

        private static string RunGitOutput(string dir, params string[] args)
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
            var stdout = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            return stdout.Trim();
        }

        public void Dispose() => TestScratch.Delete(Path);
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
}
