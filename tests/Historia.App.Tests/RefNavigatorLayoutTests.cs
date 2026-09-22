namespace HistoriaApp.Tests;

[Trait("tier", "ui")]
public sealed class RefNavigatorLayoutTests
{
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

        public TestRepoFixture(IEnumerable<string>? extraBranchNames = null)
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "historia-refnav-layout-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
            RunGit(Path, "init");
            RunGit(Path, "config", "user.name", "Test");
            RunGit(Path, "config", "user.email", "test@example.com");

            var file = System.IO.Path.Combine(Path, "f.txt");
            File.WriteAllText(file, "init\n");
            RunGit(Path, "add", ".");
            RunGit(Path, "commit", "-m", "c1", "--no-gpg-sign");

            RunGit(Path, "branch", "feature/auth-rework");
            RunGit(Path, "branch", "feature/auth-tests");
            RunGit(Path, "branch", "fix/authz-header");
            RunGit(Path, "branch", "chore/deps");

            RunGit(Path, "tag", "v1.0.0");
            RunGit(Path, "tag", "v1.1.0");

            RunGit(Path, "remote", "add", "origin", "https://example.com/repo.git");
            RunGit(Path, "update-ref", "refs/remotes/origin/feature/auth", "HEAD");

            File.WriteAllText(file, "wip\n");
            RunGit(Path, "stash", "push", "-m", "wip: stash work");

            if (extraBranchNames is not null)
                foreach (var name in extraBranchNames)
                    RunGit(Path, "branch", name);
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

    private static (RefNavigatorView View, ServiceProvider Sp, TestRepoFixture Fixture, RepositoryContext Context, Window Window) CreateView(
        double windowWidth = 300, IEnumerable<string>? extraBranchNames = null)
    {
        var fixture = new TestRepoFixture(extraBranchNames);
        var sp = CreateServices();
        var ws = sp.GetRequiredService<IWorkspaceService>();
        var ctx = ws.OpenAsync(fixture.Path).GetAwaiter().GetResult();
        ctx.State.RefreshRefsAsync().GetAwaiter().GetResult();
        ws.Focus(ctx);
        var view = new RefNavigatorView(sp);
        var window = new Window { Width = windowWidth, Height = 800, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (view, sp, fixture, ctx, window);
    }

    private static void Cleanup((RefNavigatorView View, ServiceProvider Sp, TestRepoFixture Fixture, RepositoryContext Context, Window Window) ctx)
    {
        try { ctx.Window.Close(); } catch { }
        try { ctx.View.Dispose(); } catch { }
        try { ctx.Context.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
        try { ctx.Sp.Dispose(); } catch { }
        try { ctx.Fixture.Dispose(); } catch { }
    }

    private static List<Border> GetSectionHeaders(RefNavigatorView view)
    {
        var headers = new List<Border>();
        foreach (var child in view._root.Children)
        {
            if (child is Border b && b.Child is Grid g && g.ColumnDefinitions.Count >= 3)
                headers.Add(b);
        }
        return headers;
    }

    private static StackPanel GetSectionBody(RefNavigatorView view, int sectionIndex)
    {
        var headers = GetSectionHeaders(view);
        var header = headers[sectionIndex];
        var headerIdx = view._root.Children.IndexOf(header);
        for (int i = headerIdx + 1; i < view._root.Children.Count; i++)
        {
            if (view._root.Children[i] is ScrollViewer sv && sv.Content is StackPanel sp)
                return sp;
            if (headers.Contains(view._root.Children[i]))
                break;
        }
        throw new InvalidOperationException($"No body found for section {sectionIndex}");
    }

    private static double RightEdgeInView(Control view, Control element)
    {
        var topLeft = element.TranslatePoint(new Point(0, 0), view) ?? default;
        return topLeft.X + element.Bounds.Width;
    }

    [AvaloniaFact]
    public void NoSectionHeaderHasAnAddButton()
    {
        var setup = CreateView();
        try
        {
            var headers = GetSectionHeaders(setup.View);
            Assert.True(headers.Count >= 6);

            foreach (var header in headers)
            {
                Assert.True(header.Child is Grid);
                var grid = (Grid)header.Child!;
                foreach (var button in grid.Children.OfType<Button>())
                {
                    Assert.NotEqual("New branch", ToolTip.GetTip(button)?.ToString());
                    Assert.NotEqual("＋", button.Content?.ToString());
                }
            }
        }
        finally
        {
            Cleanup(setup);
        }
    }

    [AvaloniaFact]
    public void LongBranchNameIsTrimmedBeforeTheGutter()
    {
        var longName = "worktree-agent-" + new string('x', 60);
        var setup = CreateView(extraBranchNames: [longName]);
        try
        {
            var body = GetSectionBody(setup.View, 0);
            var row = body.Children.OfType<Border>()
                .FirstOrDefault(b => ToolTip.GetTip(b)?.ToString() == longName);
            Assert.NotNull(row);

            var label = row!.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == longName);
            Assert.Equal(TextTrimming.CharacterEllipsis, label.TextTrimming);

            var limit = setup.View.Bounds.Width - RefNavigatorView.RowRightGutter;
            Assert.True(RightEdgeInView(setup.View, label) <= limit + 0.5,
                $"label right edge {RightEdgeInView(setup.View, label)} exceeds gutter limit {limit}");
        }
        finally
        {
            Cleanup(setup);
        }
    }

    [AvaloniaFact]
    public void EveryRowEndsAtTheSameRightEdge()
    {
        var setup = CreateView();
        try
        {
            // Expand every collapsed section (Tags, Stashes, Submodules, Worktrees).
            void Click(Border header) => header.RaiseEvent(new PointerPressedEventArgs(header,
                new Pointer(1, PointerType.Mouse, true), header, new Point(5, 5), 0,
                new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonPressed), KeyModifiers.None));

            var headersToExpand = new[] { 2, 3, 4, 5 };
            foreach (var idx in headersToExpand)
            {
                var header = GetSectionHeaders(setup.View)[idx];
                Click(header);
                Dispatcher.UIThread.RunJobs();
            }

            for (int i = 0; i < 50; i++)
            {
                Dispatcher.UIThread.RunJobs();
                var texts = GetSectionBody(setup.View, 3).Children.OfType<Border>()
                    .SelectMany(b => b.GetVisualDescendants().OfType<TextBlock>())
                    .Select(t => t.Text);
                if (!texts.Contains("Loading…")) break;
                System.Threading.Thread.Sleep(20);
            }

            var limit = setup.View.Bounds.Width - RefNavigatorView.RowRightGutter;
            var headers = GetSectionHeaders(setup.View);
            Assert.True(headers.Count >= 6);

            // The Border itself always stretches to the panel width; what must respect the
            // gutter is its padded-in content (the Grid holding the label/action), since a
            // Border's Padding shrinks its child's arranged area, not its own outer bounds.
            foreach (var header in headers)
            {
                Assert.NotNull(header.Child);
                var edge = RightEdgeInView(setup.View, header.Child!);
                Assert.True(edge <= limit + 0.5, $"header right edge {edge} exceeds gutter limit {limit}");
            }

            for (int i = 0; i < headers.Count; i++)
            {
                StackPanel body;
                try { body = GetSectionBody(setup.View, i); }
                catch (InvalidOperationException) { continue; }

                foreach (var row in body.Children.OfType<Border>())
                {
                    if (row.Child is null) continue;
                    var edge = RightEdgeInView(setup.View, row.Child);
                    Assert.True(edge <= limit + 0.5, $"row right edge {edge} exceeds gutter limit {limit}");
                }
            }
        }
        finally
        {
            Cleanup(setup);
        }
    }

    [AvaloniaFact]
    public void TrimmingReflowsOnResize()
    {
        var longName = "worktree-agent-" + new string('y', 60);
        var setup = CreateView(windowWidth: 300, extraBranchNames: [longName]);
        try
        {
            var body = GetSectionBody(setup.View, 0);
            var row = body.Children.OfType<Border>().First(b => ToolTip.GetTip(b)?.ToString() == longName);
            var label = row.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == longName);
            var narrowEdge = RightEdgeInView(setup.View, label);
            var narrowWidth = label.Bounds.Width;

            setup.Window.Width = 900;
            Dispatcher.UIThread.RunJobs();

            var bodyAfter = GetSectionBody(setup.View, 0);
            var rowAfter = bodyAfter.Children.OfType<Border>().First(b => ToolTip.GetTip(b)?.ToString() == longName);
            var labelAfter = rowAfter.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == longName);
            var wideWidth = labelAfter.Bounds.Width;
            var wideLimit = setup.View.Bounds.Width - RefNavigatorView.RowRightGutter;

            Assert.True(wideWidth > narrowWidth, $"label should grow when widened: {wideWidth} vs {narrowWidth}");
            Assert.True(RightEdgeInView(setup.View, labelAfter) <= wideLimit + 0.5);
        }
        finally
        {
            Cleanup(setup);
        }
    }

    [AvaloniaFact]
    public void RemoteTreeLeafTooltipIsFullRefName()
    {
        var setup = CreateView();
        try
        {
            void Click(Border row) => row.RaiseEvent(new PointerPressedEventArgs(row,
                new Pointer(1, PointerType.Mouse, true), row, new Point(5, 5), 0,
                new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonPressed), KeyModifiers.None));

            // Remotes section starts expanded (index 1); "origin" (depth 0, expanded by default)
            // holds a "feature" folder (depth 1, collapsed by default) holding leaf "auth" —
            // full ref name "origin/feature/auth". Expand the nested folder to reach the leaf.
            var body = GetSectionBody(setup.View, 1);
            var folderRow = body.Children.OfType<Border>()
                .FirstOrDefault(b => ToolTip.GetTip(b)?.ToString() == "feature");
            Assert.NotNull(folderRow);
            Click(folderRow!);
            Dispatcher.UIThread.RunJobs();

            body = GetSectionBody(setup.View, 1);
            var leafRow = body.Children.OfType<Border>()
                .FirstOrDefault(b => ToolTip.GetTip(b)?.ToString() == "origin/feature/auth");
            Assert.NotNull(leafRow);
        }
        finally
        {
            Cleanup(setup);
        }
    }
}
