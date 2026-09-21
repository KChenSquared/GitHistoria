namespace HistoriaApp.Tests;

[Trait("tier", "ui")]
public sealed class RefNavigatorFilterTests
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

        public TestRepoFixture()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "historia-refnav-" + Guid.NewGuid().ToString("N"));
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

    private static (RefNavigatorView View, ServiceProvider Sp, TestRepoFixture Fixture, RepositoryContext Context, Window Window) CreateView()
    {
        var fixture = new TestRepoFixture();
        var sp = CreateServices();
        var ws = sp.GetRequiredService<IWorkspaceService>();
        var ctx = ws.OpenAsync(fixture.Path).GetAwaiter().GetResult();
        ctx.State.RefreshRefsAsync().GetAwaiter().GetResult();
        ws.Focus(ctx);
        var view = new RefNavigatorView(sp);
        var window = new Window { Width = 300, Height = 800, Content = view };
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

    private static Button? GetFilterButton(Border header)
    {
        if (header.Child is not Grid g) return null;
        return g.Children.OfType<Button>().FirstOrDefault(b => ToolTip.GetTip(b)?.ToString() == "Filter this section");
    }

    private static void SimulateClick(Button button)
    {
        var p = new Pointer(1, PointerType.Mouse, true);
        var pe = new PointerPressedEventArgs(button, p, button, new Point(5, 5), 0,
            new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonPressed), KeyModifiers.None);
        button.RaiseEvent(pe);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void EachFilterableSectionHasAFilterIcon()
    {
        var setup = CreateView();
        try
        {
            var headers = GetSectionHeaders(setup.View);
            Assert.True(headers.Count >= 6);

            // 0: Local Branches, 1: Remotes, 2: Tags, 3: Stashes, 4: Submodules, 5: Worktrees
            Assert.NotNull(GetFilterButton(headers[0]));
            Assert.NotNull(GetFilterButton(headers[1]));
            Assert.NotNull(GetFilterButton(headers[2]));
            Assert.NotNull(GetFilterButton(headers[3]));
            Assert.Null(GetFilterButton(headers[4]));
            Assert.Null(GetFilterButton(headers[5]));
        }
        finally
        {
            Cleanup(setup);
        }
    }

    [AvaloniaFact]
    public void FilterIconClickDoesNotCollapseTheSection()
    {
        var setup = CreateView();
        try
        {
            Assert.True(setup.View.LocalOpen);
            var header = GetSectionHeaders(setup.View)[0];
            var filterBtn = GetFilterButton(header);
            Assert.NotNull(filterBtn);

            SimulateClick(filterBtn!);

            Assert.True(setup.View.LocalOpen, "Section must stay expanded after filter icon click");
            Assert.True(setup.View.LocalFilterOpen, "Filter must be open after icon click");
        }
        finally
        {
            Cleanup(setup);
        }
    }

    [AvaloniaFact]
    public void FilterIconOpensABoxBelowTheHeader()
    {
        var setup = CreateView();
        try
        {
            var header = GetSectionHeaders(setup.View)[0];
            var filterBtn = GetFilterButton(header);
            Assert.NotNull(filterBtn);

            SimulateClick(filterBtn!);

            var headersAfter = GetSectionHeaders(setup.View);
            var headerIndex = setup.View._root.Children.IndexOf(headersAfter[0]);
            Assert.True(headerIndex >= 0);
            Assert.Equal(headerIndex + 1, setup.View._root.Children.IndexOf(setup.View._localFilterBox));
        }
        finally
        {
            Cleanup(setup);
        }
    }

    [AvaloniaFact]
    public void OpeningAFilterExpandsACollapsedSection()
    {
        var setup = CreateView();
        try
        {
            Assert.False(setup.View.TagsOpen);
            var tagsHeader = GetSectionHeaders(setup.View)[2];
            var filterBtn = GetFilterButton(tagsHeader);
            Assert.NotNull(filterBtn);

            SimulateClick(filterBtn!);

            Assert.True(setup.View.TagsOpen, "Tags section must expand when filter icon is clicked");
            Assert.True(setup.View.TagsFilterOpen, "Tags filter must be open");
        }
        finally
        {
            Cleanup(setup);
        }
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

    private static List<string> GetRowTexts(StackPanel body)
    {
        var texts = new List<string>();
        foreach (var c in body.Children)
        {
            if (c is Border b)
            {
                var tb = b.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => t.Text != "⋯" && t.Text != "▾" && t.Text != "▸");
                if (tb?.Text is not null)
                    texts.Add(tb.Text);
            }
        }
        return texts;
    }

    [AvaloniaFact]
    public void MatchesAreHoistedAboveTheTree()
    {
        var setup = CreateView();
        try
        {
            var filterBtn = GetFilterButton(GetSectionHeaders(setup.View)[0])!;
            SimulateClick(filterBtn);

            setup.View._localFilterBox.Text = "auth";
            Dispatcher.UIThread.RunJobs();

            var body = GetSectionBody(setup.View, 0);
            var texts = GetRowTexts(body);

            // Hoisted rows appear first, followed by folder row "feature"
            var reworkIdx = texts.IndexOf("feature/auth-rework");
            var testsIdx = texts.IndexOf("feature/auth-tests");
            var featureFolderIdx = texts.IndexOf("feature");

            Assert.True(reworkIdx >= 0, "feature/auth-rework should be hoisted");
            Assert.True(testsIdx >= 0, "feature/auth-tests should be hoisted");
            Assert.True(featureFolderIdx >= 0, "feature folder should be in tree below");
            Assert.True(reworkIdx < featureFolderIdx, "feature/auth-rework must be above the feature folder row");
            Assert.True(testsIdx < featureFolderIdx, "feature/auth-tests must be above the feature folder row");
        }
        finally
        {
            Cleanup(setup);
        }
    }

    [AvaloniaFact]
    public void HoistedOrderFollowsTheScore()
    {
        var setup = CreateView();
        try
        {
            var filterBtn = GetFilterButton(GetSectionHeaders(setup.View)[0])!;
            SimulateClick(filterBtn);

            setup.View._localFilterBox.Text = "authz";
            Dispatcher.UIThread.RunJobs();

            var body = GetSectionBody(setup.View, 0);
            var texts = GetRowTexts(body);

            Assert.NotEmpty(texts);
            Assert.Equal("fix/authz-header", texts[0]);
        }
        finally
        {
            Cleanup(setup);
        }
    }

    [AvaloniaFact]
    public void TreeIsUnchangedBelowTheBlock()
    {
        var setup = CreateView();
        try
        {
            // First get unfiltered tree rows
            var bodyUnfiltered = GetSectionBody(setup.View, 0);
            var unfilteredRows = GetRowTexts(bodyUnfiltered);

            // Open filter and type
            var filterBtn = GetFilterButton(GetSectionHeaders(setup.View)[0])!;
            SimulateClick(filterBtn);
            setup.View._localFilterBox.Text = "auth";
            Dispatcher.UIThread.RunJobs();

            var bodyFiltered = GetSectionBody(setup.View, 0);

            // Find hairline border separating hoisted from tree
            var hairlineIdx = -1;
            for (int i = 0; i < bodyFiltered.Children.Count; i++)
            {
                if (bodyFiltered.Children[i] is Border b && b.Height == 1 && b.Child is null)
                {
                    hairlineIdx = i;
                    break;
                }
            }
            Assert.True(hairlineIdx >= 0, "Hairline border should exist");

            var belowHairlineTexts = new List<string>();
            for (int i = hairlineIdx + 1; i < bodyFiltered.Children.Count; i++)
            {
                if (bodyFiltered.Children[i] is Border b)
                {
                    var tb = b.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => t.Text != "⋯" && t.Text != "▾" && t.Text != "▸");
                    if (tb?.Text is not null)
                        belowHairlineTexts.Add(tb.Text);
                }
            }

            Assert.Equal(unfilteredRows, belowHairlineTexts);
        }
        finally
        {
            Cleanup(setup);
        }
    }

    [AvaloniaFact]
    public void NonMatchesAreNotHidden()
    {
        var setup = CreateView();
        try
        {
            var filterBtn = GetFilterButton(GetSectionHeaders(setup.View)[0])!;
            SimulateClick(filterBtn);

            setup.View._localFilterBox.Text = "auth";
            Dispatcher.UIThread.RunJobs();

            var body = GetSectionBody(setup.View, 0);
            var texts = GetRowTexts(body);

            Assert.Contains(texts, t => t.Contains("chore/deps") || t == "chore" || t == "deps");
        }
        finally
        {
            Cleanup(setup);
        }
    }

    [AvaloniaFact]
    public void NoMatchesShowsTheEmptyRow()
    {
        var setup = CreateView();
        try
        {
            var filterBtn = GetFilterButton(GetSectionHeaders(setup.View)[0])!;
            SimulateClick(filterBtn);

            setup.View._localFilterBox.Text = "zzzz";
            Dispatcher.UIThread.RunJobs();

            var body = GetSectionBody(setup.View, 0);
            var texts = GetRowTexts(body);

            Assert.Equal("No matches.", texts[0]);
        }
        finally
        {
            Cleanup(setup);
        }
    }

    [AvaloniaFact]
    public void CaretAndFocusSurviveTyping()
    {
        var setup = CreateView();
        try
        {
            var filterBtn = GetFilterButton(GetSectionHeaders(setup.View)[0])!;
            SimulateClick(filterBtn);

            var box = setup.View._localFilterBox;
            box.Focus();
            box.Text = "a";
            box.SelectionStart = 1;
            box.SelectionEnd = 1;
            Dispatcher.UIThread.RunJobs();

            box.Text = "au";
            box.SelectionStart = 2;
            box.SelectionEnd = 2;
            Dispatcher.UIThread.RunJobs();

            Assert.True(box.IsFocused, "Filter box should still be focused after typing");
            Assert.Equal(2, box.SelectionStart);
        }
        finally
        {
            Cleanup(setup);
        }
    }

    [AvaloniaFact]
    public void EscapeClearsAndClosesTheFilter()
    {
        var setup = CreateView();
        try
        {
            var filterBtn = GetFilterButton(GetSectionHeaders(setup.View)[0])!;
            SimulateClick(filterBtn);

            var box = setup.View._localFilterBox;
            box.Text = "auth";
            Dispatcher.UIThread.RunJobs();

            box.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
            Dispatcher.UIThread.RunJobs();

            Assert.False(setup.View.LocalFilterOpen);
            Assert.Equal(string.Empty, box.Text);
            Assert.DoesNotContain(box, setup.View._root.Children);

            // Tree intact
            var body = GetSectionBody(setup.View, 0);
            var texts = GetRowTexts(body);
            Assert.Contains(texts, t => t.Contains("chore/deps") || t == "chore" || t == "deps");
        }
        finally
        {
            Cleanup(setup);
        }
    }

    [AvaloniaFact]
    public void SectionCountsAreUnaffected()
    {
        var setup = CreateView();
        try
        {
            var header = GetSectionHeaders(setup.View)[0];
            var title = header.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text?.StartsWith("LOCAL BRANCHES", StringComparison.OrdinalIgnoreCase) == true).Text;
            Assert.Equal("LOCAL BRANCHES (5)", title);

            var filterBtn = GetFilterButton(header)!;
            SimulateClick(filterBtn);

            setup.View._localFilterBox.Text = "auth";
            Dispatcher.UIThread.RunJobs();

            var titleAfter = GetSectionHeaders(setup.View)[0].GetVisualDescendants().OfType<TextBlock>().First(t => t.Text?.StartsWith("LOCAL BRANCHES", StringComparison.OrdinalIgnoreCase) == true).Text;
            Assert.Equal("LOCAL BRANCHES (5)", titleAfter);
        }
        finally
        {
            Cleanup(setup);
        }
    }

    [AvaloniaFact]
    public async Task StashFilterMatchesTheMessage()
    {
        var setup = CreateView();
        try
        {
            var headers = GetSectionHeaders(setup.View);
            var stashHeader = headers[3];
            var filterBtn = GetFilterButton(stashHeader)!;
            SimulateClick(filterBtn);

            // Wait for stashes background load to finish
            for (int i = 0; i < 50; i++)
            {
                Dispatcher.UIThread.RunJobs();
                var currentTexts = GetRowTexts(GetSectionBody(setup.View, 3));
                if (!currentTexts.Contains("Loading…"))
                    break;
                await Task.Delay(20);
            }

            setup.View._stashesFilterBox.Text = "wip";
            Dispatcher.UIThread.RunJobs();

            var body = GetSectionBody(setup.View, 3);
            var texts = GetRowTexts(body);

            Assert.Contains(texts, t => t.Contains("wip: stash work"));
        }
        finally
        {
            Cleanup(setup);
        }
    }
}
