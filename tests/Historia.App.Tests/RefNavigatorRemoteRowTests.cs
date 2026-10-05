namespace HistoriaApp.Tests;

/// <summary>Every configured remote gets a sidebar row with the remote context menu, even with no
/// remote-tracking branches; stale tracking groups render without a menu. Remote rows sort
/// case-insensitively and carry a numbered cloud when two or more remotes exist; a right-click
/// opens the menu without toggling the row; the Remotes header has an Add remote button.</summary>
[Trait("tier", "ui")]
public sealed class RefNavigatorRemoteRowTests
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

    private sealed class RepoFixture : IDisposable
    {
        public string Path { get; }

        public RepoFixture(bool withUpstream = true, string[]? extraRemotes = null)
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "historia-remoterow-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
            RunGit("init");
            RunGit("config", "user.name", "Test");
            RunGit("config", "user.email", "test@example.com");
            File.WriteAllText(System.IO.Path.Combine(Path, "f.txt"), "init\n");
            RunGit("add", ".");
            RunGit("commit", "-m", "c1", "--no-gpg-sign");

            RunGit("remote", "add", "origin", "https://example.com/repo.git");
            RunGit("update-ref", "refs/remotes/origin/main", "HEAD");
            RunGit("update-ref", "refs/remotes/origin/feature/x", "HEAD");
            // Configured but never fetched: no remote-tracking branches.
            if (withUpstream)
                RunGit("remote", "add", "upstream", "https://example.com/upstream.git");
            foreach (var extra in extraRemotes ?? [])
                RunGit("remote", "add", extra, $"git@example.com:o/{extra}.git");
            // Tracking refs whose remote is not configured.
            RunGit("update-ref", "refs/remotes/ghost/main", "HEAD");
        }

        private void RunGit(params string[] args)
        {
            var psi = new System.Diagnostics.ProcessStartInfo("git")
            {
                WorkingDirectory = Path,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
            using var p = System.Diagnostics.Process.Start(psi)!;
            p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
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

    private static (RefNavigatorView View, ServiceProvider Sp, RepoFixture Fixture, RepositoryContext Context, Window Window) CreateView(
        bool withUpstream = true, string[]? extraRemotes = null)
    {
        var fixture = new RepoFixture(withUpstream, extraRemotes);
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

    private static void Cleanup((RefNavigatorView View, ServiceProvider Sp, RepoFixture Fixture, RepositoryContext Context, Window Window) s)
    {
        try { s.Window.Close(); } catch { }
        try { s.View.Dispose(); } catch { }
        try { s.Context.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
        try { s.Sp.Dispose(); } catch { }
        try { s.Fixture.Dispose(); } catch { }
    }

    // Folder rows: a caret and a name, plus a cloud on a remote root. Leaves have four columns.
    private static IEnumerable<Border> FolderRows(RefNavigatorView view) =>
        view.GetVisualDescendants().OfType<Border>()
            .Where(b => b.Child is Grid { ColumnDefinitions.Count: 2 or 3 } && b.Height == 24);

    private static TextBlock RowName(Border row) =>
        row.GetVisualDescendants().OfType<TextBlock>().Last(t => t.Parent is Grid);

    private static Border? FolderRowNamed(RefNavigatorView view, string name) =>
        FolderRows(view).FirstOrDefault(b => RowName(b).Text == name);

    /// <summary>The digit on a remote root row's cloud, "" for a plain cloud, null for no cloud.</summary>
    private static string? CloudDigit(Border row)
    {
        if (row.Child is not Grid { ColumnDefinitions.Count: 3 } grid)
            return null;
        var cloud = grid.Children.OfType<Panel>().Single(c => Grid.GetColumn(c) == 1);
        return cloud.GetVisualDescendants().OfType<TextBlock>().SingleOrDefault()?.Text ?? "";
    }

    private static List<string> RemoteRowOrder(RefNavigatorView view) =>
        FolderRows(view).Where(b => CloudDigit(b) is not null).Select(b => RowName(b).Text!).ToList();

    private static List<string> MenuHeaders(Border row) =>
        (row.ContextMenu!.ItemsSource as IEnumerable<Control>)!
            .OfType<MenuItem>().Select(m => m.Header!.ToString()!).ToList();

    [AvaloniaFact]
    public void Remote_without_tracking_branches_still_gets_a_row_and_menu()
    {
        var setup = CreateView();
        try
        {
            Assert.Equal(2, setup.Context.State.Remotes.Count);

            var row = FolderRowNamed(setup.View, "upstream");
            Assert.NotNull(row);
            Assert.NotNull(row!.ContextMenu);
            Assert.Equal(
                ["Open In Browser", "Fetch", "Prune", "Edit…", "Set Push URL…", "Delete…", "Copy URL"],
                MenuHeaders(row));

            var origin = FolderRowNamed(setup.View, "origin");
            Assert.NotNull(origin?.ContextMenu);
        }
        finally
        {
            Cleanup(setup);
        }
    }

    [AvaloniaFact]
    public void Rename_remote_item_is_gone()
    {
        var setup = CreateView();
        try
        {
            foreach (var name in new[] { "origin", "upstream" })
                Assert.DoesNotContain("Rename remote…", MenuHeaders(FolderRowNamed(setup.View, name)!));
        }
        finally
        {
            Cleanup(setup);
        }
    }

    private static string Caret(Border row) =>
        row.GetVisualDescendants().OfType<TextBlock>().First().Text!;

    private static void Click(Window window, Border row, MouseButton button)
    {
        var at = row.TranslatePoint(new Point(row.Bounds.Width / 2, row.Bounds.Height / 2), window)!.Value;
        // Hit testing reads the composed scene, so let a render tick publish the current tree.
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        window.MouseDown(at, button);
        Dispatcher.UIThread.RunJobs();
        window.MouseUp(at, button);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void Right_click_on_remote_root_opens_its_menu()
    {
        var setup = CreateView();
        try
        {
            var row = FolderRowNamed(setup.View, "origin")!;
            Click(setup.Window, row, MouseButton.Right);

            Assert.NotNull(row.GetVisualRoot());
            Assert.True(row.ContextMenu!.IsOpen);
            row.ContextMenu.Close();
        }
        finally
        {
            Cleanup(setup);
        }
    }

    [AvaloniaFact]
    public void Right_click_does_not_toggle_the_remote_folder()
    {
        var setup = CreateView();
        try
        {
            var before = Caret(FolderRowNamed(setup.View, "origin")!);
            Click(setup.Window, FolderRowNamed(setup.View, "origin")!, MouseButton.Right);
            FolderRowNamed(setup.View, "origin")!.ContextMenu?.Close();

            Assert.Equal(before, Caret(FolderRowNamed(setup.View, "origin")!));
        }
        finally
        {
            Cleanup(setup);
        }
    }

    [AvaloniaFact]
    public void Left_click_still_toggles_the_remote_folder()
    {
        var setup = CreateView();
        try
        {
            Assert.Equal("▾", Caret(FolderRowNamed(setup.View, "origin")!));
            Click(setup.Window, FolderRowNamed(setup.View, "origin")!, MouseButton.Left);
            Assert.Equal("▸", Caret(FolderRowNamed(setup.View, "origin")!));
        }
        finally
        {
            Cleanup(setup);
        }
    }

    [AvaloniaFact]
    public void Stale_tracking_group_renders_without_a_remote_menu()
    {
        var setup = CreateView();
        try
        {
            var row = FolderRowNamed(setup.View, "ghost");
            Assert.NotNull(row);
            Assert.Null(row!.ContextMenu);
        }
        finally
        {
            Cleanup(setup);
        }
    }

    [AvaloniaFact]
    public void Remotes_header_has_an_add_button_that_does_not_toggle_the_section()
    {
        var setup = CreateView();
        try
        {
            var requested = 0;
            setup.View.AddRemoteOverrideForTesting = () => requested++;
            var button = setup.View.GetVisualDescendants().OfType<Button>()
                .Single(b => ToolTip.GetTip(b)?.ToString() == "Add remote…");
            Assert.True(setup.View.RemotesOpen);

            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            var at = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), setup.Window)!.Value;
            setup.Window.MouseDown(at, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            setup.Window.MouseUp(at, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(1, requested);
            Assert.True(setup.View.RemotesOpen);
        }
        finally
        {
            Cleanup(setup);
        }
    }

    [AvaloniaFact]
    public void Remote_roots_show_numbered_clouds_when_two_or_more()
    {
        var setup = CreateView();
        try
        {
            Assert.Equal("1", CloudDigit(FolderRowNamed(setup.View, "origin")!));
            Assert.Equal("2", CloudDigit(FolderRowNamed(setup.View, "upstream")!));
            Assert.Equal("", CloudDigit(FolderRowNamed(setup.View, "ghost")!));

            Assert.Equal("1 · origin", ToolTip.GetTip(FolderRowNamed(setup.View, "origin")!));
            Assert.Equal("ghost", ToolTip.GetTip(FolderRowNamed(setup.View, "ghost")!));
        }
        finally
        {
            Cleanup(setup);
        }
    }

    [AvaloniaFact]
    public void Single_remote_root_shows_plain_cloud()
    {
        var setup = CreateView(withUpstream: false);
        try
        {
            var origin = FolderRowNamed(setup.View, "origin")!;
            Assert.Equal("", CloudDigit(origin));
            Assert.Equal("origin", ToolTip.GetTip(origin));
        }
        finally
        {
            Cleanup(setup);
        }
    }

    [AvaloniaFact]
    public void Remote_rows_follow_case_insensitive_order()
    {
        var setup = CreateView(extraRemotes: ["Zeta", "alpha"]);
        try
        {
            Assert.Equal(["alpha", "ghost", "origin", "upstream", "Zeta"], RemoteRowOrder(setup.View));
            Assert.Equal(
                ["1", "", "2", "3", "4"],
                RemoteRowOrder(setup.View).Select(n => CloudDigit(FolderRowNamed(setup.View, n)!)).ToList());
        }
        finally
        {
            Cleanup(setup);
        }
    }

    [AvaloniaFact]
    public void Path_segment_folders_have_no_cloud()
    {
        var setup = CreateView();
        try
        {
            var feature = FolderRowNamed(setup.View, "feature");
            Assert.NotNull(feature);
            Assert.Null(CloudDigit(feature!));
            Assert.DoesNotContain(feature!.GetVisualDescendants(), v => v is Avalonia.Controls.Shapes.Path);
        }
        finally
        {
            Cleanup(setup);
        }
    }
}
