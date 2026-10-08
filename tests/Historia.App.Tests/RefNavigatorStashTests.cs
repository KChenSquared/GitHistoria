namespace HistoriaApp.Tests;

[Trait("tier", "ui")]
public sealed class RefNavigatorStashTests
{
    private sealed class StubStashService(IReadOnlyList<StashEntry> entries) : IStashService
    {
        public Task<IReadOnlyList<StashEntry>> ListAsync(RepositoryContext context, CancellationToken ct = default)
            => Task.FromResult(entries);

        public Task<bool> SaveAsync(RepositoryContext context, StashSaveOptions options, CancellationToken ct = default)
            => Task.FromResult(true);

        public Task<bool> StashStagedAsync(RepositoryContext context, string? message, CancellationToken ct = default)
            => Task.FromResult(true);

        public Task ApplyAndDropAsync(RepositoryContext context, GitObjectId commitId, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task ApplyAsync(RepositoryContext context, GitObjectId commitId, bool restoreIndex, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task DropAsync(RepositoryContext context, GitObjectId commitId, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task BranchFromAsync(RepositoryContext context, GitObjectId commitId, string branchName, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task ClearAsync(RepositoryContext context, CancellationToken ct = default)
            => Task.CompletedTask;
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
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "historia-refnav-stash-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
            RunGit("init");
            RunGit("config", "user.name", "Test");
            RunGit("config", "user.email", "test@example.com");

            var file = System.IO.Path.Combine(Path, "f.txt");
            File.WriteAllText(file, "init\n");
            RunGit("add", ".");
            RunGit("commit", "-m", "c1", "--no-gpg-sign");
        }

        public void RunGit(params string[] args)
        {
            var psi = new System.Diagnostics.ProcessStartInfo("git")
            {
                WorkingDirectory = Path,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = System.Diagnostics.Process.Start(psi)!;
            p.WaitForExit();
            if (p.ExitCode != 0)
                throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {p.StandardError.ReadToEnd()}");
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

    private static (ServiceProvider Sp, StubStashService Stashes) CreateServices(IReadOnlyList<StashEntry> stashes)
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

        var stashStub = new StubStashService(stashes);
        sc.AddSingleton<IStashService>(stashStub);

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
        return (sc.BuildServiceProvider(), stashStub);
    }

    private static (RefNavigatorView View, ServiceProvider Sp, StubStashService Stashes, TestRepoFixture Fixture, RepositoryContext Context, Window Window) CreateView(IReadOnlyList<StashEntry> stashes)
    {
        var fixture = new TestRepoFixture();
        var (sp, stashStub) = CreateServices(stashes);
        var ws = sp.GetRequiredService<IWorkspaceService>();
        var ctx = ws.OpenAsync(fixture.Path).GetAwaiter().GetResult();
        ctx.MarkFirstGraphPaintDone();
        ctx.State.RefreshRefsAsync().GetAwaiter().GetResult();
        ws.Focus(ctx);
        var view = new RefNavigatorView(sp);
        var window = new Window { Width = 300, Height = 800, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (view, sp, stashStub, fixture, ctx, window);
    }

    private static void Cleanup((RefNavigatorView View, ServiceProvider Sp, StubStashService Stashes, TestRepoFixture Fixture, RepositoryContext Context, Window Window) setup)
    {
        try { setup.Window.Close(); } catch { }
        try { setup.View.Dispose(); } catch { }
        try { setup.Context.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
        try { setup.Sp.Dispose(); } catch { }
        try { setup.Fixture.Dispose(); } catch { }
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

    private static void ExpandSection(Border header)
    {
        var p = new Pointer(1, PointerType.Mouse, true);
        var pe = new PointerPressedEventArgs(header, p, header, new Point(5, 5), 0,
            new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonPressed), KeyModifiers.None);
        header.RaiseEvent(pe);
        Dispatcher.UIThread.RunJobs();
    }

    private static void SimulateClick(Control control)
    {
        var p = new Pointer(1, PointerType.Mouse, true);
        var pe = new PointerPressedEventArgs(control, p, control, new Point(5, 5), 0,
            new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonPressed), KeyModifiers.None);
        control.RaiseEvent(pe);
        Dispatcher.UIThread.RunJobs();
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
        throw new InvalidOperationException($"Body for section {sectionIndex} not found");
    }

    private static List<string> GetRowTexts(StackPanel body)
    {
        var texts = new List<string>();
        foreach (var child in body.Children)
        {
            if (child is Border b)
            {
                var tb = b.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => t.Text != "⋯" && t.Text != "▾" && t.Text != "▸");
                if (tb?.Text is not null)
                    texts.Add(tb.Text);
            }
        }
        return texts;
    }

    private static (GitObjectId Id1, GitObjectId Id2, List<StashEntry> Entries) CreateSampleStashes()
    {
        var id1 = new GitObjectId("1111111111111111111111111111111111111111");
        var id2 = new GitObjectId("2222222222222222222222222222222222222222");
        var entries = new List<StashEntry>
        {
            new() { Index = 0, CommitId = id1, Message = "On a: x", AuthorName = "T", AuthorEmail = "t@e.com", CreatedAt = DateTimeOffset.Now, BranchName = "a" },
            new() { Index = 1, CommitId = id2, Message = "On b: y", AuthorName = "T", AuthorEmail = "t@e.com", CreatedAt = DateTimeOffset.Now, BranchName = "b" },
        };
        return (id1, id2, entries);
    }

    [AvaloniaFact]
    public async Task Stash_rows_are_numbered_from_one()
    {
        var (_, _, entries) = CreateSampleStashes();
        var setup = CreateView(entries);
        try
        {
            var headers = GetSectionHeaders(setup.View);
            ExpandSection(headers[3]);

            for (var i = 0; i < 50; i++)
            {
                Dispatcher.UIThread.RunJobs();
                var current = GetRowTexts(GetSectionBody(setup.View, 3));
                if (current.Count >= 2 && current[0] == "1. On a: x")
                    break;
                await Task.Delay(20);
            }

            var rows = GetRowTexts(GetSectionBody(setup.View, 3));
            Assert.Contains("1. On a: x", rows);
            Assert.Contains("2. On b: y", rows);
            Assert.All(rows, r => Assert.DoesNotContain("stash@{", r));
        }
        finally
        {
            Cleanup(setup);
        }
    }

    [AvaloniaFact]
    public async Task Stash_filter_matches_the_displayed_label()
    {
        var (_, _, entries) = CreateSampleStashes();
        var setup = CreateView(entries);
        try
        {
            var headers = GetSectionHeaders(setup.View);
            ExpandSection(headers[3]);

            for (var i = 0; i < 50; i++)
            {
                Dispatcher.UIThread.RunJobs();
                var current = GetRowTexts(GetSectionBody(setup.View, 3));
                if (current.Count >= 2 && current[0] == "1. On a: x")
                    break;
                await Task.Delay(20);
            }

            var filterBtn = GetFilterButton(headers[3]);
            Assert.NotNull(filterBtn);
            SimulateClick(filterBtn!);

            setup.View._stashesFilterBox.Text = "2. On";
            Dispatcher.UIThread.RunJobs();

            var rows = GetRowTexts(GetSectionBody(setup.View, 3));
            Assert.NotEmpty(rows);
            Assert.Equal("2. On b: y", rows[0]);
        }
        finally
        {
            Cleanup(setup);
        }
    }

    [AvaloniaFact]
    public async Task Clicking_a_stash_selects_without_reveal()
    {
        var (id1, _, entries) = CreateSampleStashes();
        var setup = CreateView(entries);
        try
        {
            var headers = GetSectionHeaders(setup.View);
            ExpandSection(headers[3]);

            for (var i = 0; i < 50; i++)
            {
                Dispatcher.UIThread.RunJobs();
                var current = GetRowTexts(GetSectionBody(setup.View, 3));
                if (current.Count >= 2 && current[0] == "1. On a: x")
                    break;
                await Task.Delay(20);
            }

            var revealRaised = false;
            setup.Context.State.RevealCommitRequested += (_, _) => revealRaised = true;

            var body = GetSectionBody(setup.View, 3);
            var firstRow = body.Children.OfType<Border>().First(b =>
            {
                var tb = b.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => t.Text != "⋯" && t.Text != "▾" && t.Text != "▸");
                return tb?.Text == "1. On a: x";
            });

            SimulateClick(firstRow);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(id1, setup.Context.State.SelectedCommitId);
            Assert.False(revealRaised);
        }
        finally
        {
            Cleanup(setup);
        }
    }
}
