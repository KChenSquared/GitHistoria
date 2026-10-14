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
            TestScratch.Delete(dir);
    }

    private (RepositoryContext Context, StagingPanelView View, Window Window) CreateView(double width = 400, double height = 900)
    {
        var repoDir = TestScratch.NewDirectory("historia-staging-resize-");
        _tempDirs.Add(repoDir);
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
        Assert.Equal(96, view.MessageInput.Height);
    }

    [AvaloniaFact]
    public void Drag_up_grows_only_the_message_box()
    {
        var (_, view, window) = CreateView();
        var handle = view.MessageResizeHandle;
        var handleCenter = handle.TranslatePoint(new Point(handle.Bounds.Width / 2, handle.Bounds.Height / 2), window)!.Value;

        var optionsRow = view.CommitOptionsRow;
        var optionsHeight = optionsRow.Bounds.Height;
        var optionsYFromBottom = window.Bounds.Height - optionsRow.TranslatePoint(new Point(0, 0), window)!.Value.Y;

        var messageSectionGrid = (Grid)view.MessageSection.Child!;
        var scrollViewer = messageSectionGrid.Children.OfType<ScrollViewer>().First();
        var messageBox = (StackPanel)scrollViewer.Content!;
        var author = messageBox.Children[4];
        var actions = messageBox.Children[5];
        var authorHeight = author.Bounds.Height;
        var authorYFromBottom = window.Bounds.Height - author.TranslatePoint(new Point(0, 0), window)!.Value.Y;
        var actionsHeight = actions.Bounds.Height;
        var actionsYFromBottom = window.Bounds.Height - actions.TranslatePoint(new Point(0, 0), window)!.Value.Y;

        var splitArea = view.SplitHost;
        var initialSplitAreaHeight = splitArea.Bounds.Height;

        Avalonia.Headless.HeadlessWindowExtensions.MouseDown(window, handleCenter, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Avalonia.Headless.HeadlessWindowExtensions.MouseMove(window, new Point(handleCenter.X, handleCenter.Y - 80));
        Dispatcher.UIThread.RunJobs();
        Avalonia.Headless.HeadlessWindowExtensions.MouseUp(window, new Point(handleCenter.X, handleCenter.Y - 80), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(176, view.MessageInput.Height);
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
        var handle = view.MessageResizeHandle;
        var handleCenter = handle.TranslatePoint(new Point(handle.Bounds.Width / 2, handle.Bounds.Height / 2), window)!.Value;

        Avalonia.Headless.HeadlessWindowExtensions.MouseDown(window, handleCenter, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Avalonia.Headless.HeadlessWindowExtensions.MouseMove(window, new Point(handleCenter.X, handleCenter.Y + 100));
        Dispatcher.UIThread.RunJobs();
        Avalonia.Headless.HeadlessWindowExtensions.MouseUp(window, new Point(handleCenter.X, handleCenter.Y + 100), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(96, view.MessageInput.Height);
    }

    [AvaloniaFact]
    public void Drag_far_up_preserves_list_minimums()
    {
        var (_, view, window) = CreateView();
        var handle = view.MessageResizeHandle;
        var handleCenter = handle.TranslatePoint(new Point(handle.Bounds.Width / 2, handle.Bounds.Height / 2), window)!.Value;

        Avalonia.Headless.HeadlessWindowExtensions.MouseDown(window, handleCenter, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Avalonia.Headless.HeadlessWindowExtensions.MouseMove(window, new Point(handleCenter.X, handleCenter.Y - 2000));
        Dispatcher.UIThread.RunJobs();
        Avalonia.Headless.HeadlessWindowExtensions.MouseUp(window, new Point(handleCenter.X, handleCenter.Y - 2000), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        var splitArea = view.SplitHost;
        var changesSection = splitArea.TopFrame;
        var stagedSection = splitArea.Frame;

        Assert.True(changesSection.Bounds.Height >= 150);
        Assert.True(stagedSection.Bounds.Height >= 150);
        Assert.True(splitArea.Bounds.Height >= SectionFrame.SplitAreaMinHeight);
    }

    [AvaloniaFact]
    public void Changes_split_restored_after_box_shrinks()
    {
        var (context, view, window) = CreateView(width: 400, height: 900);
        var splitArea = view.SplitHost;
        var handle = view.MessageResizeHandle;
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
        var handle = view.MessageResizeHandle;
        var handleCenter = handle.TranslatePoint(new Point(handle.Bounds.Width / 2, handle.Bounds.Height / 2), window)!.Value;

        // Grow box to 200 (drag up by 104)
        Avalonia.Headless.HeadlessWindowExtensions.MouseDown(window, handleCenter, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Avalonia.Headless.HeadlessWindowExtensions.MouseMove(window, new Point(handleCenter.X, handleCenter.Y - 104));
        Dispatcher.UIThread.RunJobs();
        Avalonia.Headless.HeadlessWindowExtensions.MouseUp(window, new Point(handleCenter.X, handleCenter.Y - 104), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(200, view.MessageInput.Height);

        // Shrink window so the room is gone
        window.Height = 400;
        Dispatcher.UIThread.RunJobs();

        Assert.True(view.MessageInput.Height < 200);
        Assert.True(view.MessageInput.Height >= 96);

        // Restore window
        window.Height = 900;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(200, view.MessageInput.Height);
    }

    [AvaloniaFact]
    public void Window_shrink_below_strip_height_drops_box_to_minimum_and_regrow_restores()
    {
        var (_, view, window) = CreateView(width: 400, height: 900);
        var handle = view.MessageResizeHandle;
        var handleCenter = handle.TranslatePoint(new Point(handle.Bounds.Width / 2, handle.Bounds.Height / 2), window)!.Value;

        Avalonia.Headless.HeadlessWindowExtensions.MouseDown(window, handleCenter, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Avalonia.Headless.HeadlessWindowExtensions.MouseMove(window, new Point(handleCenter.X, handleCenter.Y - 304));
        Dispatcher.UIThread.RunJobs();
        Avalonia.Headless.HeadlessWindowExtensions.MouseUp(window, new Point(handleCenter.X, handleCenter.Y - 304), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(400, view.MessageInput.Height);

        // Shorter than the strip itself, so the lists are laid out at zero height.
        window.Height = 250;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(96, view.MessageInput.Height);

        window.Height = 900;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(400, view.MessageInput.Height);
    }

    [AvaloniaFact]
    public void Stored_height_taller_than_a_short_window_starts_at_minimum()
    {
        var (context, view, window) = CreateView(width: 400, height: 900);
        var handle = view.MessageResizeHandle;
        var handleCenter = handle.TranslatePoint(new Point(handle.Bounds.Width / 2, handle.Bounds.Height / 2), window)!.Value;

        Avalonia.Headless.HeadlessWindowExtensions.MouseDown(window, handleCenter, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Avalonia.Headless.HeadlessWindowExtensions.MouseMove(window, new Point(handleCenter.X, handleCenter.Y - 304));
        Dispatcher.UIThread.RunJobs();
        Avalonia.Headless.HeadlessWindowExtensions.MouseUp(window, new Point(handleCenter.X, handleCenter.Y - 304), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(400, view.MessageInput.Height);

        var shortView = new StagingPanelView(_sp, context);
        _views.Add(shortView);
        var shortWindow = new Window { Width = 400, Height = 250, Content = shortView };
        _windows.Add(shortWindow);
        shortWindow.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(96, shortView.MessageInput.Height);
    }

    [AvaloniaFact]
    public void Height_is_remembered_per_repo_for_the_session()
    {
        var (context, view, window) = CreateView();
        var handle = view.MessageResizeHandle;
        var handleCenter = handle.TranslatePoint(new Point(handle.Bounds.Width / 2, handle.Bounds.Height / 2), window)!.Value;

        // Grow box by 50 px -> 146 px
        Avalonia.Headless.HeadlessWindowExtensions.MouseDown(window, handleCenter, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Avalonia.Headless.HeadlessWindowExtensions.MouseMove(window, new Point(handleCenter.X, handleCenter.Y - 50));
        Dispatcher.UIThread.RunJobs();
        Avalonia.Headless.HeadlessWindowExtensions.MouseUp(window, new Point(handleCenter.X, handleCenter.Y - 50), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(146, view.MessageInput.Height);

        // Dispose view and create a new view for the same context
        view.Dispose();
        _views.Remove(view);

        var view2 = new StagingPanelView(_sp, context);
        _views.Add(view2);
        window.Content = view2;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(146, view2.MessageInput.Height);

        // A view for a second repo starts at 96
        var (_, viewRepo2, _) = CreateView();
        Assert.Equal(96, viewRepo2.MessageInput.Height);
    }

    [AvaloniaFact]
    public void Handle_is_hit_at_the_border_line()
    {
        var (_, view, window) = CreateView();
        // Hit testing reads the last rendered scene; render the current layout first.
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        var messageSection = view.MessageSection;
        var sectionTop = messageSection.TranslatePoint(new Point(0, 0), window)!.Value.Y;
        var x = window.Bounds.Width / 2;

        var hitAtLine = window.InputHitTest(new Point(x, sectionTop));
        var hitAboveLine = window.InputHitTest(new Point(x, sectionTop - 2));

        Assert.Same(view.MessageResizeHandle, hitAtLine);
        Assert.Same(view.MessageResizeHandle, hitAboveLine);
        Assert.NotNull(view.MessageResizeHandle.Cursor);
    }

    [AvaloniaFact]
    public void Handle_adds_no_height()
    {
        var (_, view, _) = CreateView();
        var messageSection = view.MessageSection;
        var grid = (Grid)messageSection.Child!;
        var scrollViewer = grid.Children.OfType<ScrollViewer>().First();

        Assert.Equal(scrollViewer.Bounds.Height + 2, messageSection.Bounds.Height);
        Assert.Equal(((Control)scrollViewer.Content!).DesiredSize.Height, scrollViewer.Bounds.Height);
    }

    private static void Drag(Window window, Control handle, params double[] offsets)
    {
        var start = handle.TranslatePoint(new Point(handle.Bounds.Width / 2, handle.Bounds.Height / 2), window)!.Value;
        Avalonia.Headless.HeadlessWindowExtensions.MouseDown(window, start, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        var end = start;
        foreach (var offset in offsets)
        {
            end = new Point(start.X, start.Y + offset);
            Avalonia.Headless.HeadlessWindowExtensions.MouseMove(window, end);
            Dispatcher.UIThread.RunJobs();
        }
        Avalonia.Headless.HeadlessWindowExtensions.MouseUp(window, end, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    private static double GrowMessageBoxToMax(StagingPanelView view, Window window)
    {
        Drag(window, view.MessageResizeHandle, -2000);
        return view.MessageInput.Height;
    }

    private void AssertSplitDragBorrowsAndIsRemembered(Func<StagingPanelView, Control> handleOf)
    {
        var (context, view, window) = CreateView(width: 400, height: 900);
        var h = GrowMessageBoxToMax(view, window);

        Drag(window, handleOf(view), 100);

        var splitArea = view.SplitHost;
        var changesRow = splitArea.RowDefinitions[0];
        var stagedSection = splitArea.Frame;

        Assert.InRange(Math.Abs(changesRow.Height.Value - 250), 0, 1.01);
        Assert.True(stagedSection.Bounds.Height >= 150);
        Assert.InRange(Math.Abs(view.MessageInput.Height - (h - 100)), 0, 1.01);
        Assert.InRange(Math.Abs(SectionFrame.GetSplitHeight(context.Id) - 250), 0, 1.01);

        view.Dispose();
        _views.Remove(view);

        var view2 = new StagingPanelView(_sp, context);
        _views.Add(view2);
        window.Content = view2;
        Dispatcher.UIThread.RunJobs();

        Assert.InRange(Math.Abs(view2.MessageInput.Height - (h - 100)), 0, 1.01);
    }

    [AvaloniaFact]
    public void Split_drag_down_past_staged_min_shrinks_message_box()
    {
        AssertSplitDragBorrowsAndIsRemembered(v => v.SplitHost.SplitHandle);
    }

    [AvaloniaFact]
    public void Staged_divider_drag_also_borrows()
    {
        AssertSplitDragBorrowsAndIsRemembered(v => v.SplitHost.DividerHandle);
    }

    [AvaloniaFact]
    public void Split_drag_down_stops_at_message_box_minimum()
    {
        var (_, view, window) = CreateView(width: 400, height: 900);
        var h = GrowMessageBoxToMax(view, window);

        Drag(window, view.SplitHost.SplitHandle, 5000);

        var splitArea = view.SplitHost;
        var changesRow = splitArea.RowDefinitions[0];
        var stagedSection = splitArea.Frame;
        var messageSection = view.MessageSection;

        Assert.Equal(96, view.MessageInput.Height);
        Assert.InRange(Math.Abs(changesRow.Height.Value - (150 + (h - 96))), 0, 1.01);
        Assert.True(stagedSection.Bounds.Height >= 150);

        var splitAreaBottom = splitArea.TranslatePoint(new Point(0, splitArea.Bounds.Height), window)!.Value.Y;
        var messageSectionTop = messageSection.TranslatePoint(new Point(0, 0), window)!.Value.Y;
        Assert.True(splitAreaBottom <= messageSectionTop + 1.01);
    }

    [AvaloniaFact]
    public void Split_drag_reversed_in_one_gesture_grows_staged_not_box()
    {
        var (context, view, window) = CreateView(width: 400, height: 900);
        var h = GrowMessageBoxToMax(view, window);

        Drag(window, view.SplitHost.SplitHandle, 100, 0);

        var splitArea = view.SplitHost;
        var changesRow = splitArea.RowDefinitions[0];
        var stagedSection = splitArea.Frame;

        Assert.InRange(Math.Abs(view.MessageInput.Height - (h - 100)), 0, 1.01);
        Assert.Equal(150, changesRow.Height.Value);
        Assert.InRange(Math.Abs(stagedSection.Bounds.Height - 250), 0, 1.01);

        view.Dispose();
        _views.Remove(view);

        var view2 = new StagingPanelView(_sp, context);
        _views.Add(view2);
        window.Content = view2;
        Dispatcher.UIThread.RunJobs();

        Assert.InRange(Math.Abs(view2.MessageInput.Height - (h - 100)), 0, 1.01);
    }

    [AvaloniaFact]
    public void Split_drag_reversed_then_down_again_borrows_further()
    {
        var (_, view, window) = CreateView(width: 400, height: 900);
        var h = GrowMessageBoxToMax(view, window);

        Drag(window, view.SplitHost.SplitHandle, 100, 0, 150);

        var splitArea = view.SplitHost;
        var changesRow = splitArea.RowDefinitions[0];
        var stagedSection = splitArea.Frame;

        Assert.InRange(Math.Abs(view.MessageInput.Height - (h - 150)), 0, 1.01);
        Assert.InRange(Math.Abs(changesRow.Height.Value - 300), 0, 1.01);
        Assert.True(stagedSection.Bounds.Height >= 150);
    }

    [AvaloniaFact]
    public void Staged_divider_drag_reversed_grows_staged_not_box()
    {
        var (context, view, window) = CreateView(width: 400, height: 900);
        var h = GrowMessageBoxToMax(view, window);

        Drag(window, view.SplitHost.DividerHandle, 100, 0);

        var splitArea = view.SplitHost;
        var changesRow = splitArea.RowDefinitions[0];
        var stagedSection = splitArea.Frame;

        Assert.InRange(Math.Abs(view.MessageInput.Height - (h - 100)), 0, 1.01);
        Assert.Equal(150, changesRow.Height.Value);
        Assert.InRange(Math.Abs(stagedSection.Bounds.Height - 250), 0, 1.01);

        view.Dispose();
        _views.Remove(view);

        var view2 = new StagingPanelView(_sp, context);
        _views.Add(view2);
        window.Content = view2;
        Dispatcher.UIThread.RunJobs();

        Assert.InRange(Math.Abs(view2.MessageInput.Height - (h - 100)), 0, 1.01);
    }

    [AvaloniaFact]
    public void Message_handle_regrows_box_after_split_borrow()
    {
        var (_, view, window) = CreateView(width: 400, height: 900);
        var h = GrowMessageBoxToMax(view, window);

        Drag(window, view.SplitHost.SplitHandle, 100);
        Assert.InRange(Math.Abs(view.MessageInput.Height - (h - 100)), 0, 1.01);

        Drag(window, view.MessageResizeHandle, -100);
        Assert.InRange(Math.Abs(view.MessageInput.Height - h), 0, 1.01);
        Assert.InRange(Math.Abs(view.SplitHost.Frame.Bounds.Height - 150), 0, 1.01);
    }

    [AvaloniaFact]
    public void Split_drag_up_does_not_regrow_box()
    {
        var (_, view, window) = CreateView(width: 400, height: 900);
        var h = GrowMessageBoxToMax(view, window);

        Drag(window, view.SplitHost.SplitHandle, 100);

        var shrunkBoxH = view.MessageInput.Height;
        Assert.InRange(Math.Abs(shrunkBoxH - (h - 100)), 0, 1.01);

        Drag(window, view.SplitHost.SplitHandle, -100);

        Assert.Equal(shrunkBoxH, view.MessageInput.Height);
        Assert.Equal(150, view.SplitHost.RowDefinitions[0].Height.Value);
    }

    [AvaloniaFact]
    public void Split_drag_with_box_at_minimum_is_blocked_at_staged_min()
    {
        var (context, view, window) = CreateView(width: 400, height: 900);
        var splitArea = view.SplitHost;
        SectionFrame.SetSplitHeight(context.Id, 2000);
        SectionFrame.ApplySplitHeight(splitArea, splitArea.RowDefinitions[0], context.Id);
        Dispatcher.UIThread.RunJobs();

        var maxChanges = splitArea.RowDefinitions[0].Height.Value;

        Drag(window, view.SplitHost.SplitHandle, 200);

        var stagedSection = splitArea.Frame;

        Assert.Equal(96, view.MessageInput.Height);
        Assert.Equal(maxChanges, splitArea.RowDefinitions[0].Height.Value);
        Assert.True(stagedSection.Bounds.Height >= 150);
    }

    [AvaloniaFact]
    public void Split_drag_in_short_window_keeps_remembered_box_height()
    {
        var (_, view, window) = CreateView(width: 400, height: 900);
        Drag(window, view.MessageResizeHandle, -104);
        Assert.Equal(200, view.MessageInput.Height);

        window.Height = 400;
        Dispatcher.UIThread.RunJobs();
        var clampedBox = view.MessageInput.Height;
        Assert.True(clampedBox < 200);

        Drag(window, view.SplitHost.SplitHandle, -20);
        Assert.Equal(clampedBox, view.MessageInput.Height);

        window.Height = 900;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(200, view.MessageInput.Height);
    }
}
