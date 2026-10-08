namespace HistoriaApp.Tests;

[Trait("tier", "ui")]
public sealed class StagingPanelMessageResizeTests : IDisposable
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

    private readonly List<string> _tempDirs = [];
    private readonly List<StagingPanelView> _views = [];
    private readonly List<Window> _windows = [];
    private readonly ServiceProvider _sp;

    public StagingPanelMessageResizeTests()
    {
        var sc = new ServiceCollection();
        var settings = new InMemorySettingsService();
        sc.AddSingleton<ISettingsService>(settings);
        sc.AddSingleton<IUiDispatcher, Historia.Platform.UiDispatcher>();
        sc.AddSingleton<IDialogService>(new AiTestSupport.Dialogs());
        sc.AddSingleton<IScrollDirectionProbe, Historia.Platform.ScrollDirectionProbe>();
        sc.AddSingleton<IRepoActionBus, RepoActionBus>();
        sc.AddSingleton<IRepoNetworkActionRunner, RepoNetworkActionRunner>();
        sc.AddSingleton<IAutoFetchHost, AutoFetchHost>();
        sc.AddSingleton<IAutoFetchService, AutoFetchService>();
        sc.AddSingleton<INavigationBus, NavigationBus>();
        sc.AddSingleton<IKeyboardCommandRouter, KeyboardCommandRouter>();
        sc.AddSingleton<INotificationService>(new AiTestSupport.Notifications());
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
        _sp = sc.BuildServiceProvider();
        _sp.GetRequiredService<Historia.Theming.AvaloniaThemeApplier>();
    }

    public void Dispose()
    {
        foreach (var w in _windows)
            try { w.Close(); } catch { }
        foreach (var v in _views)
            try { v.Dispose(); } catch { }
        try { _sp.Dispose(); } catch { }
        foreach (var dir in _tempDirs)
            try { Directory.Delete(dir, true); } catch { }
    }

    private (RepositoryContext Context, StagingPanelView View, Window Window) CreateView(double width = 400, double height = 900)
    {
        var repoDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "historia-staging-resize-" + Guid.NewGuid().ToString("N"));
        _tempDirs.Add(repoDir);
        Directory.CreateDirectory(repoDir);
        LibGit2Sharp.Repository.Init(repoDir);
        File.WriteAllText(System.IO.Path.Combine(repoDir, "a.txt"), "one\n");
        using (var repo = new LibGit2Sharp.Repository(repoDir))
        {
            repo.Config.Set("user.name", "Test");
            repo.Config.Set("user.email", "test@example.com");
            repo.Config.Set("commit.gpgsign", false);
            LibGit2Sharp.Commands.Stage(repo, "a.txt");
            var sig = new LibGit2Sharp.Signature("Test", "test@example.com", DateTimeOffset.Now);
            repo.Commit("initial", sig, sig, new LibGit2Sharp.CommitOptions());
        }

        var ws = _sp.GetRequiredService<IWorkspaceService>();
        var context = ws.OpenAsync(repoDir).GetAwaiter().GetResult();
        ws.Focus(context);

        var view = new StagingPanelView(_sp, context);
        _views.Add(view);

        var window = new Window { Width = width, Height = height, Content = view };
        _windows.Add(window);
        window.Show();
        Dispatcher.UIThread.RunJobs();

        return (context, view, window);
    }

    [AvaloniaFact]
    public void Default_box_height_is_minimum()
    {
        var (_, view, _) = CreateView();
        Assert.Equal(96, view.MessageForTesting.Height);
    }

    [AvaloniaFact]
    public void Drag_up_grows_only_the_message_box()
    {
        var (_, view, window) = CreateView();
        var handle = view.MessageResizeHandleForTesting!;
        var handleCenter = handle.TranslatePoint(new Point(handle.Bounds.Width / 2, handle.Bounds.Height / 2), window)!.Value;

        var optionsRow = view.CommitOptionsRowForTesting!;
        var optionsHeight = optionsRow.Bounds.Height;
        var optionsYFromBottom = window.Bounds.Height - optionsRow.TranslatePoint(new Point(0, 0), window)!.Value.Y;

        var messageSectionGrid = (Grid)view.MessageSectionForTesting!.Child!;
        var scrollViewer = messageSectionGrid.Children.OfType<ScrollViewer>().First();
        var messageBox = (StackPanel)scrollViewer.Content!;
        var author = messageBox.Children[4];
        var actions = messageBox.Children[5];
        var authorHeight = author.Bounds.Height;
        var authorYFromBottom = window.Bounds.Height - author.TranslatePoint(new Point(0, 0), window)!.Value.Y;
        var actionsHeight = actions.Bounds.Height;
        var actionsYFromBottom = window.Bounds.Height - actions.TranslatePoint(new Point(0, 0), window)!.Value.Y;

        var splitArea = view.SplitAreaForTesting!;
        var initialSplitAreaHeight = splitArea.Bounds.Height;

        Avalonia.Headless.HeadlessWindowExtensions.MouseDown(window, handleCenter, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Avalonia.Headless.HeadlessWindowExtensions.MouseMove(window, new Point(handleCenter.X, handleCenter.Y - 80));
        Dispatcher.UIThread.RunJobs();
        Avalonia.Headless.HeadlessWindowExtensions.MouseUp(window, new Point(handleCenter.X, handleCenter.Y - 80), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(176, view.MessageForTesting.Height);
        Assert.Equal(optionsHeight, optionsRow.Bounds.Height);
        Assert.InRange(Math.Abs(optionsYFromBottom - (window.Bounds.Height - optionsRow.TranslatePoint(new Point(0, 0), window)!.Value.Y)), 0, 1.01);
        Assert.Equal(authorHeight, author.Bounds.Height);
        Assert.InRange(Math.Abs(authorYFromBottom - (window.Bounds.Height - author.TranslatePoint(new Point(0, 0), window)!.Value.Y)), 0, 1.01);
        Assert.Equal(actionsHeight, actions.Bounds.Height);
        Assert.InRange(Math.Abs(actionsYFromBottom - (window.Bounds.Height - actions.TranslatePoint(new Point(0, 0), window)!.Value.Y)), 0, 1.01);
        Assert.InRange(Math.Abs(splitArea.Bounds.Height - (initialSplitAreaHeight - 80)), 0, 1.01);
    }

    [AvaloniaFact]
    public void Drag_down_clamps_to_minimum()
    {
        var (_, view, window) = CreateView();
        var handle = view.MessageResizeHandleForTesting!;
        var handleCenter = handle.TranslatePoint(new Point(handle.Bounds.Width / 2, handle.Bounds.Height / 2), window)!.Value;

        Avalonia.Headless.HeadlessWindowExtensions.MouseDown(window, handleCenter, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Avalonia.Headless.HeadlessWindowExtensions.MouseMove(window, new Point(handleCenter.X, handleCenter.Y + 100));
        Dispatcher.UIThread.RunJobs();
        Avalonia.Headless.HeadlessWindowExtensions.MouseUp(window, new Point(handleCenter.X, handleCenter.Y + 100), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(96, view.MessageForTesting.Height);
    }

    [AvaloniaFact]
    public void Drag_far_up_preserves_list_minimums()
    {
        var (_, view, window) = CreateView();
        var handle = view.MessageResizeHandleForTesting!;
        var handleCenter = handle.TranslatePoint(new Point(handle.Bounds.Width / 2, handle.Bounds.Height / 2), window)!.Value;

        Avalonia.Headless.HeadlessWindowExtensions.MouseDown(window, handleCenter, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Avalonia.Headless.HeadlessWindowExtensions.MouseMove(window, new Point(handleCenter.X, handleCenter.Y - 2000));
        Dispatcher.UIThread.RunJobs();
        Avalonia.Headless.HeadlessWindowExtensions.MouseUp(window, new Point(handleCenter.X, handleCenter.Y - 2000), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        var splitArea = view.SplitAreaForTesting!;
        var changesSection = (Border)splitArea.Children[0];
        var stagedSection = (Border)splitArea.Children[1];

        Assert.True(changesSection.Bounds.Height >= 150);
        Assert.True(stagedSection.Bounds.Height >= 150);
        Assert.True(splitArea.Bounds.Height >= SectionFrame.SplitAreaMinHeight);
    }

    [AvaloniaFact]
    public void Changes_split_restored_after_box_shrinks()
    {
        var (context, view, window) = CreateView(width: 400, height: 900);
        var splitArea = view.SplitAreaForTesting!;
        var handle = view.MessageResizeHandleForTesting!;
        var handleCenter = handle.TranslatePoint(new Point(handle.Bounds.Width / 2, handle.Bounds.Height / 2), window)!.Value;

        // Set Changes split near the max (e.g. 500 px)
        SectionFrame.SetSplitHeight(context.Id, 500);
        SectionFrame.ApplySplitHeight(splitArea, splitArea.RowDefinitions[0], context.Id);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(500, splitArea.RowDefinitions[0].Height.Value);

        // Grow the box until Changes is clamped
        Avalonia.Headless.HeadlessWindowExtensions.MouseDown(window, handleCenter, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Avalonia.Headless.HeadlessWindowExtensions.MouseMove(window, new Point(handleCenter.X, handleCenter.Y - 250));
        Dispatcher.UIThread.RunJobs();
        Avalonia.Headless.HeadlessWindowExtensions.MouseUp(window, new Point(handleCenter.X, handleCenter.Y - 250), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.True(splitArea.RowDefinitions[0].Height.Value < 500);
        Assert.Equal(500, SectionFrame.GetSplitHeight(context.Id));

        // Shrink the box back
        var newHandleCenter = handle.TranslatePoint(new Point(handle.Bounds.Width / 2, handle.Bounds.Height / 2), window)!.Value;
        Avalonia.Headless.HeadlessWindowExtensions.MouseDown(window, newHandleCenter, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Avalonia.Headless.HeadlessWindowExtensions.MouseMove(window, new Point(newHandleCenter.X, newHandleCenter.Y + 250));
        Dispatcher.UIThread.RunJobs();
        Avalonia.Headless.HeadlessWindowExtensions.MouseUp(window, new Point(newHandleCenter.X, newHandleCenter.Y + 250), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(500, splitArea.RowDefinitions[0].Height.Value);
        Assert.Equal(500, SectionFrame.GetSplitHeight(context.Id));
    }

    [AvaloniaFact]
    public void Window_shrink_clamps_and_regrow_restores()
    {
        var (_, view, window) = CreateView(width: 400, height: 900);
        var handle = view.MessageResizeHandleForTesting!;
        var handleCenter = handle.TranslatePoint(new Point(handle.Bounds.Width / 2, handle.Bounds.Height / 2), window)!.Value;

        // Grow box to 200 (drag up by 104)
        Avalonia.Headless.HeadlessWindowExtensions.MouseDown(window, handleCenter, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Avalonia.Headless.HeadlessWindowExtensions.MouseMove(window, new Point(handleCenter.X, handleCenter.Y - 104));
        Dispatcher.UIThread.RunJobs();
        Avalonia.Headless.HeadlessWindowExtensions.MouseUp(window, new Point(handleCenter.X, handleCenter.Y - 104), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(200, view.MessageForTesting.Height);

        // Shrink window so the room is gone
        window.Height = 400;
        Dispatcher.UIThread.RunJobs();

        Assert.True(view.MessageForTesting.Height < 200);
        Assert.True(view.MessageForTesting.Height >= 96);

        // Restore window
        window.Height = 900;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(200, view.MessageForTesting.Height);
    }

    [AvaloniaFact]
    public void Height_is_remembered_per_repo_for_the_session()
    {
        var (context, view, window) = CreateView();
        var handle = view.MessageResizeHandleForTesting!;
        var handleCenter = handle.TranslatePoint(new Point(handle.Bounds.Width / 2, handle.Bounds.Height / 2), window)!.Value;

        // Grow box by 50 px -> 146 px
        Avalonia.Headless.HeadlessWindowExtensions.MouseDown(window, handleCenter, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Avalonia.Headless.HeadlessWindowExtensions.MouseMove(window, new Point(handleCenter.X, handleCenter.Y - 50));
        Dispatcher.UIThread.RunJobs();
        Avalonia.Headless.HeadlessWindowExtensions.MouseUp(window, new Point(handleCenter.X, handleCenter.Y - 50), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(146, view.MessageForTesting.Height);

        // Dispose view and create a new view for the same context
        view.Dispose();
        _views.Remove(view);

        var view2 = new StagingPanelView(_sp, context);
        _views.Add(view2);
        window.Content = view2;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(146, view2.MessageForTesting.Height);

        // A view for a second repo starts at 96
        var (_, viewRepo2, _) = CreateView();
        Assert.Equal(96, viewRepo2.MessageForTesting.Height);
    }

    [AvaloniaFact]
    public void Handle_is_hit_at_the_border_line()
    {
        var (_, view, window) = CreateView();
        var messageSection = view.MessageSectionForTesting!;
        var sectionTop = messageSection.TranslatePoint(new Point(0, 0), window)!.Value.Y;
        var x = window.Bounds.Width / 2;

        var hitAtLine = window.InputHitTest(new Point(x, sectionTop));
        var hitAboveLine = window.InputHitTest(new Point(x, sectionTop - 2));

        Assert.Same(view.MessageResizeHandleForTesting, hitAtLine);
        Assert.Same(view.MessageResizeHandleForTesting, hitAboveLine);
        Assert.NotNull(view.MessageResizeHandleForTesting!.Cursor);
    }

    [AvaloniaFact]
    public void Handle_adds_no_height()
    {
        var (_, view, _) = CreateView();
        var messageSection = view.MessageSectionForTesting!;
        var grid = (Grid)messageSection.Child!;
        var scrollViewer = grid.Children.OfType<ScrollViewer>().First();

        Assert.Equal(scrollViewer.Bounds.Height + 2, messageSection.Bounds.Height);
        Assert.Equal(((Control)scrollViewer.Content!).DesiredSize.Height, scrollViewer.Bounds.Height);
    }
}
