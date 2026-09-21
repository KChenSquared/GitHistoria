namespace HistoriaApp.Tests;

[Trait("tier", "ui")]
public class CommitDetailPanelLayoutTests : IDisposable
{
    private readonly TestRepoFixture _fixture;
    private readonly ServiceProvider _sp;
    private readonly RepositoryContext _context;

    public CommitDetailPanelLayoutTests()
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

    [AvaloniaFact]
    public async Task Message_section_shows_the_raw_message()
    {
        const string rawMessage = "feat: title line 1\ntitle line 2\n\nbody paragraph 1\n\n- bullet item";
        _fixture.CreateCommitWithRawMessage(rawMessage);
        await _context.State.RefreshStatusAsync();
        var headId = _fixture.HeadCommitId;

        var view = new CommitDetailPanelView(_sp, _context, headId);
        var window = new Window { Width = 600, Height = 600, Content = view };
        window.Show();
        try
        {
            await WaitForCommitLoadedAsync(view);

            var tb = Assert.IsType<SelectableTextBlock>(view.MessageHostForTesting.Content);
            var inlinesText = string.Concat(tb.Inlines!.OfType<Avalonia.Controls.Documents.Run>().Select(r => r.Text));
            Assert.Equal(rawMessage, inlinesText);

            var allButtons = view.GetVisualDescendants().OfType<Button>().ToList();
            Assert.DoesNotContain(allButtons, b => b.Content is "Raw" or "Rendered");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Author_line_is_16_and_uses_text_colour()
    {
        var headId = _fixture.HeadCommitId;
        var view = new CommitDetailPanelView(_sp, _context, headId);
        var window = new Window { Width = 600, Height = 600, Content = view };
        window.Show();
        try
        {
            await WaitForCommitLoadedAsync(view);

            var metaRow = Assert.IsType<Grid>(view.MetaHostForTesting.Content);
            var author = Assert.IsType<TextBlock>(metaRow.Children[0]);
            var right = Assert.IsType<StackPanel>(metaRow.Children[1]);
            var dateText = Assert.IsType<TextBlock>(right.Children[0]);
            var hash = Assert.IsType<TextBlock>(right.Children[1]);

            Assert.Equal(16, author.FontSize);
            Assert.Equal(16, dateText.FontSize);
            Assert.Equal(16, hash.FontSize);
            Assert.NotNull(author.Foreground);
            if (Application.Current?.TryFindResource("ColorText", out var res) == true && res is IBrush expectedBrush)
            {
                Assert.Equal(expectedBrush, author.Foreground);
            }
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Parents_line_is_12_gray_label_white_underlined_hashes()
    {
        var headId = _fixture.HeadCommitId;
        var view = new CommitDetailPanelView(_sp, _context, headId);
        var window = new Window { Width = 600, Height = 600, Content = view };
        window.Show();
        try
        {
            await WaitForCommitLoadedAsync(view);

            var parentsRow = Assert.IsType<StackPanel>(view.ParentsHostForTesting.Content);
            var label = Assert.IsType<TextBlock>(parentsRow.Children[0]);
            Assert.Equal(12, label.FontSize);
            Assert.Equal("Parent(s):", label.Text);
            Assert.NotNull(label.Foreground);
            if (Application.Current?.TryFindResource("ColorTextMuted", out var mutedRes) == true && mutedRes is IBrush expectedMuted)
            {
                Assert.Equal(expectedMuted, label.Foreground);
            }

            var hash = Assert.IsType<TextBlock>(parentsRow.Children[1]);
            Assert.Equal(12, hash.FontSize);
            Assert.Equal(TextDecorations.Underline, hash.TextDecorations);
            Assert.NotNull(hash.Foreground);
            if (Application.Current?.TryFindResource("ColorText", out var textRes) == true && textRes is IBrush expectedText)
            {
                Assert.Equal(expectedText, hash.Foreground);
            }
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Parents_line_shows_every_parent()
    {
        // 1. Root commit (0 parents)
        var rootId = _fixture.RootCommitId;
        var rootView = new CommitDetailPanelView(_sp, _context, rootId);
        var rootWindow = new Window { Width = 600, Height = 600, Content = rootView };
        rootWindow.Show();
        try
        {
            await WaitForCommitLoadedAsync(rootView);
            var rootParentsRow = Assert.IsType<StackPanel>(rootView.ParentsHostForTesting.Content);
            Assert.Equal(2, rootParentsRow.Children.Count);
            var noneText = Assert.IsType<TextBlock>(rootParentsRow.Children[1]);
            Assert.Equal("none", noneText.Text);
        }
        finally
        {
            rootWindow.Close();
        }

        // 2. Normal commit (1 parent)
        var normalId = _fixture.HeadCommitId;
        var normalView = new CommitDetailPanelView(_sp, _context, normalId);
        var normalWindow = new Window { Width = 600, Height = 600, Content = normalView };
        normalWindow.Show();
        try
        {
            await WaitForCommitLoadedAsync(normalView);
            var normalParentsRow = Assert.IsType<StackPanel>(normalView.ParentsHostForTesting.Content);
            Assert.Equal(2, normalParentsRow.Children.Count);
            var parentHash = Assert.IsType<TextBlock>(normalParentsRow.Children[1]);
            Assert.Equal(rootId.Sha[..10], parentHash.Text);
        }
        finally
        {
            normalWindow.Close();
        }

        // 3. Merge commit (2 parents)
        var (mergeId, parent1Id, parent2Id) = _fixture.CreateMergeCommit();
        var mergeView = new CommitDetailPanelView(_sp, _context, mergeId);
        var mergeWindow = new Window { Width = 600, Height = 600, Content = mergeView };
        mergeWindow.Show();
        try
        {
            await WaitForCommitLoadedAsync(mergeView);
            var mergeParentsRow = Assert.IsType<StackPanel>(mergeView.ParentsHostForTesting.Content);
            Assert.Equal(3, mergeParentsRow.Children.Count);
            var hash1 = Assert.IsType<TextBlock>(mergeParentsRow.Children[1]);
            var hash2 = Assert.IsType<TextBlock>(mergeParentsRow.Children[2]);
            Assert.Equal(parent1Id.Sha[..10], hash1.Text);
            Assert.Equal(parent2Id.Sha[..10], hash2.Text);
        }
        finally
        {
            mergeWindow.Close();
        }
    }

    [AvaloniaFact]
    public async Task Clicking_a_parent_focuses_it()
    {
        var (mergeId, parent1Id, parent2Id) = _fixture.CreateMergeCommit();
        var view = new CommitDetailPanelView(_sp, _context, mergeId);
        var window = new Window { Width = 600, Height = 600, Content = view };
        window.Show();
        try
        {
            await WaitForCommitLoadedAsync(view);

            var parentsRow = Assert.IsType<StackPanel>(view.ParentsHostForTesting.Content);
            var hash2 = Assert.IsType<TextBlock>(parentsRow.Children[2]);

            LibGit2Sharp.ObjectId? revealed = null;
            _context.State.RevealCommitRequested += (_, e) => revealed = e.CommitId;

            // Right click does not navigate or reveal
            var ptr = new Pointer(1, PointerType.Mouse, true);
            var peRight = new PointerPressedEventArgs(hash2, ptr, hash2, new Point(5, 5), 0,
                new PointerPointProperties(RawInputModifiers.RightMouseButton, PointerUpdateKind.RightButtonPressed), KeyModifiers.None);
            hash2.RaiseEvent(peRight);
            Dispatcher.UIThread.RunJobs();

            Assert.Null(revealed);
            Assert.NotEqual(parent2Id, _context.State.SelectedCommitId);

            // Left click selects and reveals parent 2
            var peLeft = new PointerPressedEventArgs(hash2, ptr, hash2, new Point(5, 5), 0,
                new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed), KeyModifiers.None);
            hash2.RaiseEvent(peLeft);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(parent2Id, _context.State.SelectedCommitId);
            Assert.Equal(parent2Id, revealed);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Top_section_has_the_changes_border()
    {
        var headId = _fixture.HeadCommitId;
        var view = new CommitDetailPanelView(_sp, _context, headId);
        Assert.Equal(new Thickness(1, 1, 1, 0), view.TopSectionForTesting.BorderThickness);
    }

    [AvaloniaFact]
    public async Task Files_bar_aligns_with_staged_bar()
    {
        var headId = _fixture.HeadCommitId;
        var windowStaging = new Window { Width = 800, Height = 900 };
        var windowDetail = new Window { Width = 800, Height = 900 };
        var stagingView = new StagingPanelView(_sp, _context);
        var detailView = new CommitDetailPanelView(_sp, _context, headId);

        windowStaging.Content = stagingView;
        windowDetail.Content = detailView;
        windowStaging.Show();
        windowDetail.Show();
        try
        {
            await WaitForCommitLoadedAsync(detailView);
            Dispatcher.UIThread.RunJobs();

            var filesY = detailView.FilesSectionForTesting.TranslatePoint(new Point(0, 0), windowDetail)!.Value.Y;
            var stagedSection = windowStaging.GetVisualDescendants().OfType<Border>().First(b => Grid.GetRow(b) == 2);
            var stagedY = stagedSection.TranslatePoint(new Point(0, 0), windowStaging)!.Value.Y;

            Assert.Equal(filesY, stagedY, 1);

            // Drag detail pane's divider handle down by 40 px
            var handle = detailView.FilesDividerHandleForTesting;
            var handleCenter = handle.TranslatePoint(new Point(handle.Bounds.Width / 2, handle.Bounds.Height / 2), windowDetail)!.Value;

            Avalonia.Headless.HeadlessWindowExtensions.MouseDown(windowDetail, handleCenter, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();

            Avalonia.Headless.HeadlessWindowExtensions.MouseMove(windowDetail, new Point(handleCenter.X, handleCenter.Y + 40));
            Dispatcher.UIThread.RunJobs();

            Avalonia.Headless.HeadlessWindowExtensions.MouseUp(windowDetail, new Point(handleCenter.X, handleCenter.Y + 40), MouseButton.Left);
            Dispatcher.UIThread.RunJobs();

            var newStoredHeight = SectionFrame.GetSplitHeight(_context.Id);
            Assert.True(newStoredHeight > SectionFrame.DefaultTopHeight);

            // Re-attach stagingView and verify it adopts the updated stored height
            windowStaging.Content = null;
            Dispatcher.UIThread.RunJobs();
            windowStaging.Content = stagingView;
            Dispatcher.UIThread.RunJobs();

            var splitArea = windowStaging.GetVisualDescendants().OfType<Grid>().First(g => g.RowDefinitions.Count == 3);
            Assert.Equal(newStoredHeight, splitArea.RowDefinitions[0].Height.Value);
        }
        finally
        {
            windowStaging.Close();
            windowDetail.Close();
        }
    }

    private static async Task WaitForCommitLoadedAsync(CommitDetailPanelView view)
    {
        for (var i = 0; i < 100; i++)
        {
            Dispatcher.UIThread.RunJobs();
            if (view.MetaHostForTesting.Content is not null && view.MessageHostForTesting.Content is not null)
                return;
            await Task.Delay(20);
        }
        throw new TimeoutException("Commit detail did not finish loading in time.");
    }

    private sealed class TestRepoFixture : IDisposable
    {
        public string Path { get; }
        public LibGit2Sharp.ObjectId RootCommitId { get; }
        public LibGit2Sharp.ObjectId HeadCommitId => new(RunGitOutput(Path, "rev-parse", "HEAD"));

        public TestRepoFixture()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "historia-detail-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
            RunGit(Path, "init", "-b", "main");
            RunGit(Path, "config", "user.name", "Test");
            RunGit(Path, "config", "user.email", "test@example.com");

            var file = System.IO.Path.Combine(Path, "f.txt");
            File.WriteAllText(file, "init\n");
            RunGit(Path, "add", ".");
            RunGit(Path, "commit", "-m", "initial commit", "--no-gpg-sign");
            RootCommitId = new LibGit2Sharp.ObjectId(RunGitOutput(Path, "rev-parse", "HEAD"));

            File.WriteAllText(file, "second\n");
            RunGit(Path, "add", ".");
            RunGit(Path, "commit", "-m", "second commit", "--no-gpg-sign");
        }

        public void CreateCommitWithRawMessage(string msg)
        {
            var file = System.IO.Path.Combine(Path, "f.txt");
            File.AppendAllText(file, "more\n");
            RunGit(Path, "add", ".");
            RunGit(Path, "commit", "-m", msg, "--no-gpg-sign");
        }

        public (LibGit2Sharp.ObjectId MergeId, LibGit2Sharp.ObjectId Parent1, LibGit2Sharp.ObjectId Parent2) CreateMergeCommit()
        {
            RunGit(Path, "checkout", "-b", "side");
            var sideFile = System.IO.Path.Combine(Path, "side.txt");
            File.WriteAllText(sideFile, "side\n");
            RunGit(Path, "add", ".");
            RunGit(Path, "commit", "-m", "side commit", "--no-gpg-sign");
            var parent2 = new LibGit2Sharp.ObjectId(RunGitOutput(Path, "rev-parse", "HEAD"));

            RunGit(Path, "checkout", "main");
            var mainFile = System.IO.Path.Combine(Path, "main.txt");
            File.WriteAllText(mainFile, "main\n");
            RunGit(Path, "add", ".");
            RunGit(Path, "commit", "-m", "main commit", "--no-gpg-sign");
            var parent1 = new LibGit2Sharp.ObjectId(RunGitOutput(Path, "rev-parse", "HEAD"));

            RunGit(Path, "merge", "side", "--no-ff", "-m", "merge side into main", "--no-gpg-sign");
            var mergeId = new LibGit2Sharp.ObjectId(RunGitOutput(Path, "rev-parse", "HEAD"));
            return (mergeId, parent1, parent2);
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

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    foreach (var f in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories))
                    {
                        var attr = File.GetAttributes(f);
                        if ((attr & FileAttributes.ReadOnly) != 0)
                            File.SetAttributes(f, attr & ~FileAttributes.ReadOnly);
                    }
                    Directory.Delete(Path, true);
                }
            }
            catch { }
        }
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
        sc.AddSingleton<LibGit2Service>();
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
