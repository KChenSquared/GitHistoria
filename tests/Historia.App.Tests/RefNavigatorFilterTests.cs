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
            Path = TestScratch.NewDirectory("historia-refnav-");
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
            TestGit.Run(dir, args);
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

    private static (RefNavigatorView View, ServiceProvider Sp, TestRepoFixture Fixture, RepositoryContext Context, Window Window) CreateView()
    {
        var fixture = new TestRepoFixture();
        var sp = CreateServices();
        var ws = sp.GetRequiredService<IWorkspaceService>();
        var ctx = ws.OpenAsync(fixture.Path).GetAwaiter().GetResult();
        ctx.MarkFirstGraphPaintDone();
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

    private static List<Border> GetSectionHeaders(RefNavigatorView view) =>
        view.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("section-header")).ToList();

    // The filter button is in every header; it is shown only for sections that can be filtered.
    private static Button? GetFilterButton(Border header) =>
        header.GetVisualDescendants().OfType<Button>()
            .FirstOrDefault(b => b.IsVisible && ToolTip.GetTip(b)?.ToString() == "Filter this section");

    private static TextBox FilterBox(RefNavigatorView view, int sectionIndex) =>
        GetSectionHeaders(view)[sectionIndex].GetVisualAncestors().OfType<StackPanel>().First()
            .Children.OfType<TextBox>().Single();

    private static void SimulateClick(Window window, Button button)
    {
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        var at = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
        window.MouseDown(at, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        window.MouseUp(at, MouseButton.Left);
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
            Assert.True(setup.View.ViewModel.Local.IsOpen);
            var header = GetSectionHeaders(setup.View)[0];
            var filterBtn = GetFilterButton(header);
            Assert.NotNull(filterBtn);

            SimulateClick(setup.Window, filterBtn!);

            Assert.True(setup.View.ViewModel.Local.IsOpen, "Section must stay expanded after filter icon click");
            Assert.True(setup.View.ViewModel.Local.IsFilterOpen, "Filter must be open after icon click");
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

            SimulateClick(setup.Window, filterBtn!);

            var section = GetSectionHeaders(setup.View)[0].GetVisualAncestors().OfType<StackPanel>().First();
            var headerIndex = section.Children.IndexOf(GetSectionHeaders(setup.View)[0]);
            var box = FilterBox(setup.View, 0);
            Assert.True(headerIndex >= 0);
            Assert.True(box.IsEffectivelyVisible);
            Assert.Equal(headerIndex + 1, section.Children.IndexOf(box));
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
            Assert.False(setup.View.ViewModel.Tags.IsOpen);
            var tagsHeader = GetSectionHeaders(setup.View)[2];
            var filterBtn = GetFilterButton(tagsHeader);
            Assert.NotNull(filterBtn);

            SimulateClick(setup.Window, filterBtn!);

            Assert.True(setup.View.ViewModel.Tags.IsOpen, "Tags section must expand when filter icon is clicked");
            Assert.True(setup.View.ViewModel.Tags.IsFilterOpen, "Tags filter must be open");
        }
        finally
        {
            Cleanup(setup);
        }
    }

    private static ListBox GetSectionBody(RefNavigatorView view, int sectionIndex) =>
        GetSectionHeaders(view)[sectionIndex].GetVisualAncestors().OfType<StackPanel>().First()
            .Children.OfType<ListBox>().Single();

    // A section's list is virtualized and capped in height, so only the rows in view have containers.
    // Row texts are read from the rows the list is bound to; the realized containers are checked to
    // show the first of them.
    private static IReadOnlyList<RefRowViewModel> Rows(ListBox body) =>
        (IReadOnlyList<RefRowViewModel>)body.ItemsSource!;

    private static string? TextOf(RefRowViewModel row) => row switch
    {
        RefFolderRowViewModel folder => folder.Segment,
        RefLeafRowViewModel leaf => leaf.Text,
        _ => null,
    };

    private static List<string> GetRowTexts(ListBox body) =>
        Rows(body).Select(TextOf).OfType<string>().ToList();

    private static List<string> RealizedRowTexts(ListBox body)
    {
        var texts = new List<string>();
        foreach (var item in body.GetVisualDescendants().OfType<ListBoxItem>().OrderBy(i => i.Bounds.Y))
        {
            var row = (Border)item.Presenter!.Child!;
            var text = row.GetVisualDescendants().OfType<TextBlock>()
                .FirstOrDefault(t => t.IsEffectivelyVisible && t.Text != "⋯" && t.Text != "▾" && t.Text != "▸")?.Text;
            if (text is not null)
                texts.Add(text);
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
            SimulateClick(setup.Window, filterBtn);

            FilterBox(setup.View, 0).Text = "auth";
            Dispatcher.UIThread.RunJobs();

            var body = GetSectionBody(setup.View, 0);
            var texts = GetRowTexts(body);
            var shown = RealizedRowTexts(body);
            Assert.NotEmpty(shown);
            Assert.Equal(texts.Take(shown.Count), shown);

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
            SimulateClick(setup.Window, filterBtn);

            FilterBox(setup.View, 0).Text = "authz";
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
            SimulateClick(setup.Window, filterBtn);
            FilterBox(setup.View, 0).Text = "auth";
            Dispatcher.UIThread.RunJobs();

            var bodyFiltered = GetSectionBody(setup.View, 0);

            // Find hairline row separating hoisted from tree
            var rows = Rows(bodyFiltered);
            var hairlineIdx = rows.ToList().FindIndex(r => r is RefSeparatorRowViewModel);
            Assert.True(hairlineIdx >= 0, "Hairline row should exist");

            var belowHairlineTexts = rows.Skip(hairlineIdx + 1).Select(TextOf).OfType<string>().ToList();

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
            SimulateClick(setup.Window, filterBtn);

            FilterBox(setup.View, 0).Text = "auth";
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
            SimulateClick(setup.Window, filterBtn);

            FilterBox(setup.View, 0).Text = "zzzz";
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
            SimulateClick(setup.Window, filterBtn);

            var box = FilterBox(setup.View, 0);
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
            SimulateClick(setup.Window, filterBtn);

            var box = FilterBox(setup.View, 0);
            box.Text = "auth";
            Dispatcher.UIThread.RunJobs();

            box.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
            Dispatcher.UIThread.RunJobs();

            Assert.False(setup.View.ViewModel.Local.IsFilterOpen);
            Assert.Equal(string.Empty, box.Text);
            Assert.False(box.IsEffectivelyVisible);

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
            SimulateClick(setup.Window, filterBtn);

            FilterBox(setup.View, 0).Text = "auth";
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
            SimulateClick(setup.Window, filterBtn);

            // Wait for stashes background load to finish
            for (int i = 0; i < 50; i++)
            {
                Dispatcher.UIThread.RunJobs();
                var currentTexts = GetRowTexts(GetSectionBody(setup.View, 3));
                if (!currentTexts.Contains("Loading…"))
                    break;
                await Task.Delay(20);
            }

            FilterBox(setup.View, 3).Text = "wip";
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
