namespace Historia.Views;

/// <summary>Landing page — open / clone / init and the recents list (V4 port of Home.razor).</summary>
public sealed class HomeView : UserControl, IDisposable
{
    private readonly IWorkspaceService _workspace;
    private readonly ICloneService _cloneService;
    private readonly IRecentRepositoryService _recents;
    private readonly IDialogService _dialogs;
    private readonly INotificationService _notifications;
    private readonly INavigationBus _nav;
    private readonly IExternalLauncher _launcher;
    private readonly IProfileService _profiles;
    private readonly IRepoLayoutService _repoLayout;
    private readonly Historia.Services.AutoFetch.IAutoFetchService _autoFetch;
    private readonly ISubmoduleService _submodules;

    // Avalonia 11.3.20 Fluent vertical ScrollBar expanded width is 16 px (Avalonia.Themes.Fluent Controls/ScrollBar.xaml).
    // Inset content by 16 px + 4 px margin so the scrollbar sits fully clear of the rows and remove buttons.
    private const double ScrollBarExpandedWidth = 16;
    private const double RecentScrollerRightPadding = ScrollBarExpandedWidth + 4;

    private readonly UniformWidthStackPanel _recentList = new() { Spacing = 4 };
    private readonly TextBlock _cloneStatus = Text("", "ColorSuccess", 12);
    private readonly Button _cloneCancel;
    private readonly Border _cloneBox = new();
    private readonly Button _cloneBtn;
    private bool _busy;

    public HomeView(IServiceProvider sp) : this(sp, "") { }

    /// <param name="intent">v8 M8: <c>"open"</c> / <c>"clone"</c> / <c>"init"</c> from the
    /// app-band <c>+</c> menu — the matching flow is launched once the panel is shown.</param>
    public HomeView(IServiceProvider sp, string intent)
    {
        _workspace = sp.GetRequiredService<IWorkspaceService>();
        _cloneService = sp.GetRequiredService<ICloneService>();
        _recents = sp.GetRequiredService<IRecentRepositoryService>();
        _dialogs = sp.GetRequiredService<IDialogService>();
        _notifications = sp.GetRequiredService<INotificationService>();
        _nav = sp.GetRequiredService<INavigationBus>();
        _launcher = sp.GetRequiredService<IExternalLauncher>();
        _profiles = sp.GetRequiredService<IProfileService>();
        _repoLayout = sp.GetRequiredService<IRepoLayoutService>();
        _autoFetch = sp.GetRequiredService<Historia.Services.AutoFetch.IAutoFetchService>();
        _submodules = sp.GetRequiredService<ISubmoduleService>();

        var root = new StackPanel { Spacing = 16, Margin = new Thickness(32), MaxWidth = 640 };

        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, VerticalAlignment = VerticalAlignment.Center };
        title.Children.Add(BindThemedMark(
            new Image { Width = 60, Height = 60, VerticalAlignment = VerticalAlignment.Center },
            sp.GetRequiredService<Historia.Services.Theming.IThemeService>()));
        title.Children.Add(Text("Historia", "ColorText", 28, FontWeight.SemiBold));
        root.Children.Add(title);
        root.Children.Add(Text("Navigate every timeline without the paradox.", "ColorTextMuted", 13));

        _cloneBtn = TextButton("Clone…", () => _ = CloneAsync());
        var actions = BuildActionRow(
            TextButton("Open repository…", () => _ = OpenAsync(), primary: true),
            _cloneBtn,
            TextButton("New repository…", () => _ = InitAsync()));
        root.Children.Add(actions);

        _cloneCancel = TextButton("Cancel", () => _cloneService.Cancel());
        _cloneCancel.Classes.Add("danger");

        _cloneBox.IsVisible = false;
        _cloneBox.Padding = new Thickness(10, 6);
        _cloneBox.CornerRadius = new CornerRadius(4);
        // Clone progress reads green like the "Stage all" pill (border + status text); Cancel
        // reads red like "Unstage all" (Button.danger chrome).
        _cloneBox.Dyn(Border.BackgroundProperty, "ColorSurface").Dyn(Border.BorderBrushProperty, "ColorSuccess");
        _cloneBox.BorderThickness = new Thickness(1);
        var cloneRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        cloneRow.Children.Add(_cloneStatus);
        cloneRow.Children.Add(_cloneCancel);
        _cloneBox.Child = cloneRow;
        root.Children.Add(_cloneBox);

        root.Children.Add(Text("Recent", "ColorTextMuted", 12, FontWeight.SemiBold));
        // Right padding keeps the scrollbar clear of the recent folder boxes and their
        // remove buttons whether minimized or expanded on hover.
        root.Children.Add(BuildRecentScroller(_recentList));

        Content = ClosablePane(new ScrollViewer
        {
            Content = root,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        }, sp, requireFocusedRepo: true);
        this.Dyn(BackgroundProperty, "ColorChrome");

        _recents.Changed += OnRecentsChanged;
        _cloneService.Changed += OnCloneServiceChanged;
        RenderRecents();
        RenderCloneStatus();

        if (intent == "clone" && _cloneService.IsRunning)
        {
            // Already running: the box is already rendered.
        }
        else
        {
            var launch = intent switch
            {
                "open" => (Func<Task>)OpenAsync,
                "clone" => CloneAsync,
                "init" => InitAsync,
                _ => null,
            };
            if (launch is not null)
                Avalonia.Threading.Dispatcher.UIThread.Post(() => _ = launch());
        }
    }

    private void OnRecentsChanged(object? s, EventArgs e)
        => Avalonia.Threading.Dispatcher.UIThread.Post(RenderRecents);

    private void OnCloneServiceChanged(object? s, EventArgs e)
        => Avalonia.Threading.Dispatcher.UIThread.Post(RenderCloneStatus);

    private void RenderCloneStatus()
    {
        _cloneBtn.IsEnabled = !_cloneService.IsRunning;
        if (_cloneService.Current is { } status)
        {
            _cloneStatus.Text = status.Describe();
            _cloneCancel.IsEnabled = !status.CancelRequested;
            _cloneBox.IsVisible = true;
        }
        else
        {
            _cloneBox.IsVisible = false;
        }
    }

    private void RenderRecents()
    {
        _recentList.Children.Clear();
        if (_recents.Items.Count == 0)
        {
            _recentList.Children.Add(Text("No repositories opened yet.", "ColorTextMuted"));
            return;
        }
        foreach (var r in _recents.Items)
        {
            var rr = r;
            var label = new StackPanel
            {
                Margin = new Thickness(10, 6),
                Children =
                {
                    Text(r.DisplayName + (r.IsStale ? "  (missing)" : ""), "ColorText", 13),
                    Text(r.Path, "ColorTextFaint", 11),
                },
            };

            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            grid.Children.Add(label);
            var remove = IconButton("close", "Remove", () => _ = _recents.RemoveAsync(rr.Path));
            Grid.SetColumn(remove, 1);
            grid.Children.Add(remove);

            // Transparent fill + grey outline at rest; list-hover fill on pointer-over
            // (matches Button.secondary / V3 .recent-item).
            var row = new Border
            {
                Child = grid,
                Background = Brushes.Transparent,
                CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(1),
                Cursor = new Avalonia.Input.Cursor(
                    r.IsStale ? Avalonia.Input.StandardCursorType.Arrow : Avalonia.Input.StandardCursorType.Hand),
            }.Dyn(Border.BorderBrushProperty, "ColorBorderStrong");
            row.WithRevealMenu(_launcher, () => rr.Path, "Open repository location");
            row.Opacity = r.IsStale ? 0.5 : 1;
            row.PointerEntered += (_, _) => { if (!rr.IsStale) row.Dyn(Border.BackgroundProperty, "ColorListHover"); };
            row.PointerExited += (_, _) => row.Background = Brushes.Transparent;
            row.PointerPressed += (_, e) =>
            {
                if (!rr.IsStale && !_busy && !_cloneService.IsRunning && e.GetCurrentPoint(row).Properties.IsLeftButtonPressed)
                    _ = OpenPathAsync(rr.Path);
            };
            _recentList.Children.Add(row);
        }
    }

    private async Task OpenAsync()
    {
        var path = await _dialogs.PickFolderAsync("Open a Git repository");
        if (path is not null)
            await OpenPathAsync(path);
    }

    private async Task OpenPathAsync(string path)
    {
        _busy = true;
        try
        {
            // §12.1.1: on a cold start this tells the auto-fetch loop to run an [InitialFetch].
            _autoFetch.NotifyRepoEntering(Historia.Services.AutoFetch.RepoEntrySource.Opened);
            await _workspace.OpenAsync(path);
            _nav.RequestNavigate("repo");
        }
        catch (Exception ex)
        {
            _notifications.Publish(NotificationSeverity.Error, $"Could not open repository: {ex.Message}", ex.NotificationDetails());
        }
        finally { _busy = false; }
    }

    private async Task InitAsync()
    {
        var path = await _dialogs.PickFolderAsync("Choose a folder for the new repository");
        if (path is null) return;
        var branch = await _dialogs.PromptAsync("Initial branch", "Name for the initial branch:", "main");
        if (branch is null) return;

        _busy = true;
        try
        {
            // §12.1.1: a fresh init is already current — start the loop with no [InitialFetch].
            _autoFetch.NotifyRepoEntering(Historia.Services.AutoFetch.RepoEntrySource.ClonedOrInitialized);
            await _workspace.InitAsync(path, string.IsNullOrWhiteSpace(branch) ? "main" : branch.Trim());
            _nav.RequestNavigate("repo");
        }
        catch (Exception ex)
        {
            _notifications.Publish(NotificationSeverity.Error, $"Could not initialize repository: {ex.Message}", ex.NotificationDetails());
        }
        finally { _busy = false; }
    }

    /// <summary>Parent of the most recently opened repo, else the user's home folder.</summary>
    private string DefaultCloneRoot()
    {
        var recent = _recents.Items.FirstOrDefault()?.Path;
        if (!string.IsNullOrEmpty(recent))
        {
            var parent = System.IO.Path.GetDirectoryName(recent.TrimEnd('/', '\\'));
            if (!string.IsNullOrEmpty(parent))
                return parent;
        }
        return HomeDirectory;
    }

    private async Task CloneAsync()
    {
        if (_cloneService.IsRunning)
            return;

        var r = await Historia.Platform.CloneDialog.ShowAsync(_dialogs, DefaultCloneRoot(), GuessRepoName, _profiles.GetAllProfiles());
        if (r is null) return;

        if (_cloneService.IsRunning)
            return;

        var options = new CloneOptions
        {
            Depth = r.Shallow ? 1 : null,
            Sparse = r.Sparse,
        };

        try
        {
            await _cloneService.StartAsync(new CloneRequest(r.Url, r.TargetDirectory, options, r.ProfileId));
        }
        catch (InvalidOperationException ex)
        {
            _notifications.Publish(NotificationSeverity.Error, ex.Message);
            return;
        }

        if (FindClonedRepository(_workspace.OpenRepositories, r.TargetDirectory) is { } cloned)
            await OfferSubmoduleInitAsync(cloned);
    }

    internal static bool PathsEqual(string a, string b)
    {
        var normA = a.Replace('/', '\\').TrimEnd('/', '\\');
        var normB = b.Replace('/', '\\').TrimEnd('/', '\\');
        return string.Equals(normA, normB,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    internal static RepositoryContext? FindClonedRepository(IEnumerable<RepositoryContext> open, string targetDirectory) =>
        open.FirstOrDefault(ctx => PathsEqual(ctx.RepositoryPath, targetDirectory));

    /// <summary>F5: a freshly cloned repository with a top-level `.gitmodules` leaves its
    /// submodules uninitialized unless the user opts in here — matches how "Init & update"
    /// already behaves from the sidebar (non-destructive, so a plain <c>ConfirmAsync</c>).</summary>
    private async Task OfferSubmoduleInitAsync(RepositoryContext after)
    {
        if (after.IsBare || !File.Exists(System.IO.Path.Combine(after.RepositoryPath, ".gitmodules")))
            return;

        var proceed = await _dialogs.ConfirmAsync(
            "Initialize submodules?",
            "This repository has submodules. Initialize and check them out now?",
            confirmLabel: "Initialize");
        if (!proceed) return;

        _notifications.Publish(NotificationSeverity.Info, "Initializing submodules…");
        try
        {
            await _submodules.InitAndUpdateAsync(after, null, true, after.Lifetime);
            await after.State.RefreshAllAsync(after.Lifetime);
            _notifications.Publish(NotificationSeverity.Success, "Submodules initialized.");
        }
        catch (Exception ex)
        {
            _notifications.Publish(NotificationSeverity.Error, $"Could not initialize submodules: {ex.Message}", ex.NotificationDetails());
        }
    }

    private static string GuessRepoName(string url)
    {
        var trimmed = url.Trim().TrimEnd('/');
        var lastSlash = trimmed.LastIndexOfAny(['/', ':']);
        var name = lastSlash >= 0 ? trimmed[(lastSlash + 1)..] : trimmed;
        return name.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }

    public void Dispose()
    {
        _recents.Changed -= OnRecentsChanged;
        _cloneService.Changed -= OnCloneServiceChanged;
    }

    internal static Panel BuildActionRow(params Control[] buttons)
    {
        var panel = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            ItemSpacing = 8,
            LineSpacing = 8,
        };
        foreach (var b in buttons)
            panel.Children.Add(b);
        return panel;
    }

    internal static ScrollViewer BuildRecentScroller(Control content) =>
        new()
        {
            Content = content,
            MaxHeight = 380,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Padding = new Thickness(0, 0, RecentScrollerRightPadding, 0),
        };

    private sealed class UniformWidthStackPanel : StackPanel
    {
        protected override Size ArrangeOverride(Size finalSize)
        {
            var maxW = 0.0;
            foreach (var child in Children)
            {
                if (child.IsVisible && child.DesiredSize.Width > maxW)
                    maxW = child.DesiredSize.Width;
            }
            var width = Math.Max(maxW, finalSize.Width);
            return base.ArrangeOverride(new Size(width, finalSize.Height));
        }
    }
}
